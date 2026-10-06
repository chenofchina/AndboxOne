using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace AndboxOne.Core;

/// <summary>APK 离线解析结果。</summary>
public sealed class ApkInfo
{
    public string Package { get; set; } = "";
    public string VersionName { get; set; } = "";
    public int VersionCode { get; set; }
    public string Label { get; set; } = "";
    public string LauncherActivity { get; set; } = "";
    public List<string> Permissions { get; set; } = new();
    public byte[]? IconPng { get; set; }
}

/// <summary>
/// ApkInspector —— 纯离线 APK 检查器。
///
/// 两条解析路径：
///   A. 若 RuntimeSdk\build-tools 下存在可选的 aapt.exe，用 `aapt dump badging`（信息最全）；
///   B. 否则使用内置的二进制 AXML (Android Binary XML) 解析引擎——
///      直接解包 ZIP 中的 AndroidManifest.xml，手工解出：
///        包名 / versionName / versionCode / uses-permission 列表 /
///        application label / LAUNCHER Activity（MAIN + LAUNCHER intent-filter 匹配，
///        含 activity-alias 的 targetActivity 处理）。
///
/// 图标提取：解析 resources.arsc 属于重量级方案，这里采用工程折衷——
/// 扫描 APK 内 res/**mipmap|drawable**/*launcher|icon*.png|jpg 条目，
/// 解码后按像素面积取最大者（覆盖绝大多数应用；缺失则回退内置占位图标）。
/// PNG 统一重编码，供 ICO 封装与快捷方式使用。
/// </summary>
public static partial class ApkInspector
{
    private const string AndroidNs = "http://schemas.android.com/apk/res/android";

    // ------------------------------------------------------------------
    //  对外入口
    // ------------------------------------------------------------------

    public static ApkInfo? Inspect(string apkPath, string? aaptPath = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(aaptPath) && File.Exists(aaptPath))
            {
                ApkInfo? viaAapt = TryAapt(apkPath, aaptPath);
                if (viaAapt is not null)
                {
                    viaAapt.IconPng ??= ExtractIconPng(apkPath);
                    return viaAapt;
                }
                EventBus.Warn("APK", "aapt badging 失败，回退内置 AXML 引擎");
            }
            return InspectViaAxml(apkPath);
        }
        catch (Exception ex)
        {
            EventBus.Error("APK", $"解析失败 {Path.GetFileName(apkPath)}: {ex.Message}");
            return null;
        }
    }

    private static ApkInfo? TryAapt(string apkPath, string aaptPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = aaptPath,
                Arguments = $"dump badging \"{apkPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using Process p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20000);
            if (p.ExitCode != 0 || output.Length == 0) return null;
            return ParseBadging(output);
        }
        catch (Exception ex)
        {
            EventBus.Warn("APK", $"aapt 调用异常: {ex.Message}");
            return null;
        }
    }

    /// <summary>解析 aapt dump badging 输出。</summary>
    public static ApkInfo ParseBadging(string output)
    {
        var info = new ApkInfo();
        foreach (string line in output.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("package:"))
            {
                Match m = Regex.Match(t, @"package:\s*name='([^']*)'.*?versionCode='(\d+)'?.*?versionName='([^']*)'");
                if (m.Success)
                {
                    info.Package = m.Groups[1].Value;
                    if (int.TryParse(m.Groups[2].Value, out int vc)) info.VersionCode = vc;
                    info.VersionName = m.Groups[3].Value;
                }
                else
                {
                    Match n = Regex.Match(t, @"package:\s*name='([^']*)'");
                    info.Package = n.Success ? n.Groups[1].Value : info.Package;
                }
            }
            else if (t.StartsWith("application-label:") && info.Label.Length == 0)
                info.Label = t["application-label:".Length..].Trim('\'');
            else if (t.StartsWith("launchable-activity:"))
            {
                Match m = Regex.Match(t, @"name='([^']*)'");
                if (m.Success) info.LauncherActivity = m.Groups[1].Value;
            }
            else if (t.StartsWith("uses-permission:"))
            {
                Match m = Regex.Match(t, @"uses-permission:\s*'([^']*)'");
                if (m.Success) info.Permissions.Add(m.Groups[1].Value);
            }
        }
        info.Permissions = info.Permissions.Distinct().ToList();
        return info;
    }

    // ------------------------------------------------------------------
    //  内置 AXML 引擎
    // ------------------------------------------------------------------

    private static ApkInfo? InspectViaAxml(string apkPath)
    {
        using ZipArchive zip = ZipFile.OpenRead(apkPath);
        ZipArchiveEntry? manifest = zip.GetEntry("AndroidManifest.xml")
            ?? throw new InvalidDataException("APK 中未找到 AndroidManifest.xml");

        byte[] axml;
        using (Stream s = manifest.Open())
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            axml = ms.ToArray();
        }

        var info = new ApkInfo();
        ParseAxml(axml, info);

        if (info.Package.Length == 0)
            throw new InvalidDataException("AXML 解析未取得 package（压缩包可能加密或非标准 APK）");

        info.IconPng = ExtractIconPng(zip);
        return info;
    }

    internal static void ParseAxml(byte[] data, ApkInfo info)
    {
        if (data.Length < 8) throw new InvalidDataException("AXML 过短");
        ushort fileType = BitConverter.ToUInt16(data, 0);
        if (fileType != 0x0003) throw new InvalidDataException($"非二进制 XML (type=0x{fileType:X4})——资源已混淆，请放置 aapt 后重试");

        int fileSize = (int)BitConverter.ToUInt32(data, 4);

        // ---- StringPool（第一个子块）----
        int pos = 8;
        List<string> strings = ReadStringPool(data, pos, out int poolSize);
        pos += poolSize;

        // ---- 遍历元素块 ----
        var stack = new Stack<Ctx>();
        // 当前挂起的 activity 名与 intent-filter 状态
        string? pendingActivity = null;
        string? pendingAliasTarget = null;
        bool inIntentFilter = false;
        bool sawMain = false, sawLauncher = false;

        while (pos + 8 <= Math.Min(fileSize, data.Length))
        {
            ushort type = BitConverter.ToUInt16(data, pos);
            ushort headerSize = BitConverter.ToUInt16(data, pos + 2);
            int size = (int)BitConverter.ToUInt32(data, pos + 4);
            if (size <= 0) break;

            if (type == 0x0102) // START_ELEMENT
            {
                int nsIdx = BitConverter.ToInt32(data, pos + 16);
                int nameIdx = BitConverter.ToInt32(data, pos + 20);
                ushort attrStart = BitConverter.ToUInt16(data, pos + 24);
                ushort attrSize = BitConverter.ToUInt16(data, pos + 26);
                ushort attrCount = BitConverter.ToUInt16(data, pos + 28);
                string name = Str(strings, nameIdx);
                string ns = Str(strings, nsIdx);

                var attrs = new List<(string Ns, string Name, string? Raw, byte DataType, uint Data)>();
                int attrBase = pos + 16 + attrStart;
                for (int i = 0; i < attrCount; i++)
                {
                    int a = attrBase + i * attrSize;
                    if (a + 20 > data.Length) break;
                    int aNs = BitConverter.ToInt32(data, a);
                    int aName = BitConverter.ToInt32(data, a + 4);
                    int aRaw = BitConverter.ToInt32(data, a + 8);
                    byte dataType = data[a + 15];
                    uint aData = BitConverter.ToUInt32(data, a + 16);
                    attrs.Add((Str(strings, aNs), Str(strings, aName), aRaw >= 0 ? Str(strings, aRaw) : null, dataType, aData));
                }

                string val(string attrNs, string attrName)
                {
                    foreach (var a in attrs)
                        if (a.Name == attrName && (attrNs.Length == 0 ? a.Ns.Length == 0 : a.Ns == attrNs))
                        {
                            if (a.DataType == 0x03) return Str(strings, (int)a.Data); // STRING
                            if (a.Raw is not null) return a.Raw;
                            if (a.DataType == 0x10) return a.Data.ToString();          // INT_DEC
                            if (a.DataType == 0x12) return a.Data != 0 ? "true" : "false";
                            return a.Raw ?? "";
                        }
                    return "";
                }

                switch (name)
                {
                    case "manifest":
                        info.Package = val("", "package");
                        if (int.TryParse(val(AndroidNs, "versionCode"), out int vcode)) info.VersionCode = vcode;
                        info.VersionName = val(AndroidNs, "versionName");
                        break;
                    case "uses-permission":
                        string perm = val(AndroidNs, "name");
                        if (perm.Length > 0) info.Permissions.Add(perm);
                        break;
                    case "application":
                        string lbl = val(AndroidNs, "label");
                        if (lbl.Length > 0 && !lbl.StartsWith('@')) info.Label = lbl; // @string 引用需 arsc，放弃
                        break;
                    case "activity":
                        PushCtx();
                        pendingActivity = val(AndroidNs, "name");
                        pendingAliasTarget = null;
                        sawMain = sawLauncher = false;
                        break;
                    case "activity-alias":
                        PushCtx();
                        pendingActivity = val(AndroidNs, "targetActivity");
                        pendingAliasTarget = val(AndroidNs, "name");
                        sawMain = sawLauncher = false;
                        break;
                    case "intent-filter":
                        inIntentFilter = true;
                        sawMain = sawLauncher = false;
                        break;
                    case "action" when inIntentFilter:
                        if (val(AndroidNs, "name") == "android.intent.action.MAIN") sawMain = true;
                        break;
                    case "category" when inIntentFilter:
                        if (val(AndroidNs, "name") == "android.intent.category.LAUNCHER") sawLauncher = true;
                        break;
                    default:
                        PushCtx();
                        break;
                }

                void PushCtx() => stack.Push(new Ctx(name));
            }
            else if (type == 0x0103) // END_ELEMENT
            {
                int nameIdx = BitConverter.ToInt32(data, pos + 20);
                string name = Str(strings, nameIdx);

                if ((name == "activity" || name == "activity-alias") && pendingActivity is not null)
                {
                    if (sawMain && sawLauncher && info.LauncherActivity.Length == 0)
                        info.LauncherActivity = pendingActivity;
                    pendingActivity = null;
                    pendingAliasTarget = null;
                }
                else if (name == "intent-filter")
                {
                    inIntentFilter = false;
                }
                else if (stack.Count > 0)
                {
                    stack.Pop();
                }
            }

            pos += size;
        }

        if (info.LauncherActivity.Length > 0 && !info.LauncherActivity.Contains('.'))
        {
            // 相对类名补全：<manifest package>.<activity>
            info.LauncherActivity = $"{info.Package}.{info.LauncherActivity}";
        }
    }

    private sealed class Ctx(string name) { public string Name = name; }

    private static string Str(List<string> pool, int idx) =>
        idx >= 0 && idx < pool.Count ? pool[idx] : "";

    /// <summary>解析二进制 StringPool 块（UTF-8 与 UTF-16 双编码）。</summary>
    private static List<string> ReadStringPool(byte[] data, int offset, out int chunkSize)
    {
        ushort type = BitConverter.ToUInt16(data, offset);
        if (type != 0x0001) throw new InvalidDataException($"AXML 首子块非 StringPool (type=0x{type:X4})");

        ushort headerSize = BitConverter.ToUInt16(data, offset + 2);
        chunkSize = (int)BitConverter.ToUInt32(data, offset + 4);
        int stringCount = (int)BitConverter.ToUInt32(data, offset + 8);
        uint flags = BitConverter.ToUInt32(data, offset + 16);
        int stringsStart = (int)BitConverter.ToUInt32(data, offset + 20);
        bool isUtf8 = (flags & 0x100) != 0;

        var result = new List<string>(stringCount);
        int offsetsBase = offset + headerSize;
        for (int i = 0; i < stringCount; i++)
        {
            int strOffset = offset + stringsStart + (int)BitConverter.ToUInt32(data, offsetsBase + i * 4);
            result.Add(isUtf8 ? ReadUtf8String(data, strOffset) : ReadUtf16String(data, strOffset));
        }
        return result;
    }

    private static string ReadUtf8String(byte[] data, int pos)
    {
        // 字符数（u8，高位 0x80 时扩展 u16）——跳过
        if ((data[pos] & 0x80) != 0) pos += 2; else pos += 1;
        // 字节数
        int len;
        if ((data[pos] & 0x80) != 0) { len = ((data[pos] & 0x7F) << 8) | data[pos + 1]; pos += 2; }
        else { len = data[pos]; pos += 1; }
        if (pos + len > data.Length) len = Math.Max(0, data.Length - pos);
        return Encoding.UTF8.GetString(data, pos, len);
    }

    private static string ReadUtf16String(byte[] data, int pos)
    {
        int len;
        ushort first = BitConverter.ToUInt16(data, pos);
        if ((first & 0x8000) != 0)
        {
            len = ((first & 0x7FFF) << 16) | BitConverter.ToUInt16(data, pos + 2);
            pos += 4;
        }
        else { len = first; pos += 2; }
        if (pos + len * 2 > data.Length) len = Math.Max(0, (data.Length - pos) / 2);
        return Encoding.Unicode.GetString(data, pos, len * 2);
    }

    // ------------------------------------------------------------------
    //  图标提取 + ICO 封装
    // ------------------------------------------------------------------

    [GeneratedRegex(@"^res/.*?(mipmap|drawable).*?(launcher|icon).*?\.(png|jpg|jpeg)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex IconEntryRegex();

    /// <summary>从 APK 提取最大的一张 launcher/icon PNG（解码后统一重编码为 PNG）。</summary>
    public static byte[]? ExtractIconPng(string apkPath)
    {
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(apkPath);
            return ExtractIconPng(zip);
        }
        catch (Exception ex)
        {
            EventBus.Warn("APK", $"图标提取失败: {ex.Message}");
            return null;
        }
    }

    private static byte[]? ExtractIconPng(ZipArchive zip)
    {
        var candidates = zip.Entries
            .Where(e => IconEntryRegex().IsMatch(e.FullName) && e.Length > 0)
            .OrderByDescending(e => e.Length)
            .Take(6)
            .ToList();

        byte[]? best = null;
        long bestPixels = -1;
        foreach (ZipArchiveEntry e in candidates)
        {
            try
            {
                using Stream es = e.Open();
                using var ms = new MemoryStream();
                es.CopyTo(ms);
                ms.Position = 0;
                BitmapDecoder dec = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapSource src = dec.Frames[0];
                long pixels = (long)src.PixelWidth * src.PixelHeight;
                if (pixels <= bestPixels) continue;

                var enc = new PngBitmapEncoder();
                // 图标 >256px 时缩到 256（ICO 目录项宽度字节上限语义）
                int target = Math.Min(src.PixelWidth, src.PixelHeight) > 256 ? 256 : Math.Max(src.PixelWidth, src.PixelHeight);
                double scale = Math.Min(256.0 / src.PixelWidth, 256.0 / src.PixelHeight);
                if (scale < 1.0)
                {
                    int w = (int)(src.PixelWidth * scale), h = (int)(src.PixelHeight * scale);
                    var transformed = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(w / (double)src.PixelWidth, h / (double)src.PixelHeight));
                    transformed.Freeze();
                    enc.Frames.Add(BitmapFrame.Create(transformed));
                }
                else
                {
                    src.Freeze();
                    enc.Frames.Add(BitmapFrame.Create(src));
                }
                using var outMs = new MemoryStream();
                enc.Save(outMs);
                best = outMs.ToArray();
                bestPixels = pixels;
                _ = target;
            }
            catch { /* 单个候选损坏不影响其它 */ }
        }
        return best;
    }

    /// <summary>把 PNG 字节封装为 ICO 容器（Vista+ 原生支持 PNG-in-ICO）。</summary>
    public static byte[] WrapPngAsIco(byte[] png, int width, int height)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);          // reserved
        w.Write((ushort)1);          // type = icon
        w.Write((ushort)1);          // count
        w.Write((byte)(width >= 256 ? 0 : width));   // 0 表示 256
        w.Write((byte)(height >= 256 ? 0 : height));
        w.Write((byte)0);            // 调色板
        w.Write((byte)0);            // reserved
        w.Write((ushort)1);          // planes
        w.Write((ushort)32);         // bpp
        w.Write((uint)png.Length);
        w.Write(22u);                // 数据偏移 = 6 + 16
        w.Write(png);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>生成默认占位图标（荧光绿方块 + 深底），用于 APK 未提供可用图标时。</summary>
    public static byte[] GenerateDefaultIconPng()
    {
        int size = 96;
        var dv = new System.Windows.Media.DrawingVisual();
        using (System.Windows.Media.DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new System.Windows.Rect(0, 0, size, size));
            var green = System.Windows.Media.Brushes.LimeGreen;
            dc.DrawRoundedRectangle(green, null, new System.Windows.Rect(14, 14, size - 28, size - 28), 16, 16);
            var ft = new System.Windows.Media.FormattedText("A1",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface("Consolas"),
                34, System.Windows.Media.Brushes.Black, 1.0);
            dc.DrawText(ft, new System.Windows.Point((size - ft.Width) / 2, (size - ft.Height) / 2));
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
