using System.IO;

namespace AndboxOne.Core;

/// <summary>
/// ShortcutCreator —— 桌面级集成：APK 安装后的桌面快捷方式生成器。
///
/// 行为约定：
///   · 在 Windows 桌面生成 .lnk 快捷方式；双击后自动启动对应虚拟机
///     并运行该 App（命令行协议见 App.xaml.cs 的 --launch-app 分支）；
///   · 图标从 APK 中提取（ApkInspector），封装为 .ico 缓存到数据目录；
///   · .lnk 通过 WScript.Shell COM 接口创建（无需引用 COM 程序集）；
///     COM 不可用时自动降级为 .cmd 启动脚本，保证功能永远可用。
/// </summary>
public static class ShortcutCreator
{
    /// <summary>WScript.Shell COM CLSID。</summary>
    private static readonly Guid WshShellClsid = new("72C24DD5-D70A-438B-8A42-98424B88AFB8");

    /// <summary>
    /// 生成桌面快捷方式。
    /// </summary>
    /// <param name="env">环境管理器（取图标缓存目录）。</param>
    /// <param name="avdName">目标虚拟机名。</param>
    /// <param name="package">APK 包名。</param>
    /// <param name="activity">Launcher Activity（可为空，空则仅启动 App 主入口）。</param>
    /// <param name="displayName">快捷方式显示名（一般为 App 标签）。</param>
    /// <param name="iconPng">从 APK 提取的 PNG 图标（可空，空则用内置占位图标）。</param>
    /// <returns>快捷方式完整路径；失败抛异常（调用方负责 UI 提示）。</returns>
    public static string CreateDesktopShortcut(
        EnvironmentManager env, string avdName, string package, string activity,
        string displayName, byte[]? iconPng)
    {
        string exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位 AndboxOne 主程序");

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string safeName = Sanitize(string.IsNullOrEmpty(displayName) ? package : displayName);
        string lnkPath = Path.Combine(desktop, $"[{safeName}] {avdName}.lnk");

        // ---- 1. 图标：PNG → ICO 落盘缓存 ----
        string iconPath = Path.Combine(env.IconsDir, $"{Sanitize(package)}-{Sanitize(avdName)}.ico");
        try
        {
            byte[] png = iconPng ?? ApkInspector.GenerateDefaultIconPng();
            int w = 96, h = 96;
            try
            {
                using var ms = new MemoryStream(png);
                var dec = System.Windows.Media.Imaging.BitmapDecoder.Create(ms,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                w = dec.Frames[0].PixelWidth;
                h = dec.Frames[0].PixelHeight;
            }
            catch { /* 尺寸仅影响目录项语义，失败不影响封装 */ }
            File.WriteAllBytes(iconPath, ApkInspector.WrapPngAsIco(png, w, h));
        }
        catch (Exception ex)
        {
            EventBus.Warn("SHORTCUT", $"图标封装失败，快捷方式将使用默认图标: {ex.Message}");
            iconPath = exePath; // 退回主程序图标
        }

        // ---- 2. 命令行协议：--launch-app --avd ... --package ... --activity ... ----
        string arguments =
            $"--launch-app --avd \"{avdName}\" --package \"{package}\" --activity \"{activity}\"";

        // ---- 3. 创建 .lnk ----
        try
        {
            Type? wshType = Type.GetTypeFromCLSID(WshShellClsid, throwOnError: true);
            dynamic shell = Activator.CreateInstance(wshType!)!;
            try
            {
                dynamic lnk = shell.CreateShortcut(lnkPath);
                lnk.TargetPath = exePath;
                lnk.Arguments = arguments;
                lnk.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                lnk.IconLocation = $"{iconPath}, 0";
                lnk.Description = $"AndboxOne 快捷启动: {displayName} @ {avdName}";
                lnk.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
            EventBus.Info("SHORTCUT", $"桌面快捷方式已生成: {lnkPath}");
            return lnkPath;
        }
        catch (Exception ex)
        {
            EventBus.Warn("SHORTCUT", $"WScript.Shell COM 不可用（{ex.Message}），降级为 .cmd 启动脚本");
        }

        // ---- 4. 兜底：.cmd 启动脚本 ----
        string cmdPath = Path.Combine(desktop, $"[{safeName}] {avdName}.cmd");
        File.WriteAllText(cmdPath,
            "@echo off\r\n" +
            "rem AndboxOne 快捷启动（.lnk 创建失败的降级方案）\r\n" +
            $"start \"\" \"{exePath}\" {arguments}\r\n",
            new System.Text.UTF8Encoding(false));
        EventBus.Info("SHORTCUT", $"已生成启动脚本: {cmdPath}");
        return cmdPath;
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim().Trim('.').Replace(' ', '_');
    }
}
