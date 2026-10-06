using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AndboxOne.Core;

/// <summary>
/// DeviceProfile —— 设备配置定义模块。
///
/// 描述一台虚拟设备的外观与硬件：厂商、型号、设备名、分辨率、DPI、
/// CPU 核心数、内存大小、GPU 渲染模式、目标系统镜像（ABI / tag / API level）。
///
/// 支持能力：
///   · 内置预设：Pixel 5、Pixel 6、小米 11、三星 S21；
///   · 克隆（Clone）与 JSON 导出/导入（离线可搬运）；
///   · 作为 AVD config.ini 的生成源（见 AvdWriter）。
/// </summary>
public class DeviceProfile
{
    public string Name { get; set; } = "自定义设备";
    public string Manufacturer { get; set; } = "Google";
    public string Model { get; set; } = "Pixel 5";

    /// <summary>屏幕宽（竖屏短边，像素）。</summary>
    public int Width { get; set; } = 1080;
    /// <summary>屏幕高（竖屏长边，像素）。</summary>
    public int Height { get; set; } = 2340;
    public int Dpi { get; set; } = 440;
    public int CpuCores { get; set; } = 4;
    public int RamMb { get; set; } = 2048;
    /// <summary>数据分区大小（GB）。</summary>
    public int StorageGb { get; set; } = 8;

    /// <summary>GPU 渲染模式：auto / host / angle_indirect / swiftshader_indirect。</summary>
    public string GpuMode { get; set; } = "auto";
    /// <summary>无窗口启动（-no-window，纯后台跑）。</summary>
    public bool Headless { get; set; } = false;
    /// <summary>禁用快照（每次冷启动，-no-snapshot）。</summary>
    public bool NoSnapshot { get; set; } = false;

    public string Abi { get; set; } = "x86_64";
    public string TagId { get; set; } = "google_apis";
    public int ApiLevel { get; set; } = 30;

    [JsonIgnore]
    public string ImageTagDisplay => TagId == "google_apis" ? "Google APIs" : TagId;

    public DeviceProfile Clone() => (DeviceProfile)MemberwiseClone();

    // ---------------- JSON 导入/导出（离线搬运配置） ----------------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static DeviceProfile FromJson(string json)
    {
        DeviceProfile? p = JsonSerializer.Deserialize<DeviceProfile>(json, JsonOpts)
            ?? throw new InvalidDataException("JSON 反序列化结果为空");
        p.Sanitize();
        return p;
    }

    public bool JsonEquals(DeviceProfile other) => ToJson() == other.ToJson();

    // ---------------- 合法性钳制 ----------------

    private static readonly Regex AvdNameRegex = new(@"^[A-Za-z0-9_\.\-]{1,64}$", RegexOptions.Compiled);

    /// <summary>把非法值钳制回合法区间（防御导入的恶意/损坏 JSON）。</summary>
    public void Sanitize()
    {
        Width = Math.Clamp(Width, 240, 2160);
        Height = Math.Clamp(Height, 240, 3840);
        Dpi = Math.Clamp(Dpi, 120, 640);
        CpuCores = Math.Clamp(CpuCores, 1, Environment.ProcessorCount);
        RamMb = Math.Clamp(RamMb, 512, 16384);
        StorageGb = Math.Clamp(StorageGb, 2, 64);
        ApiLevel = Math.Clamp(ApiLevel, 21, 36);
        if (string.IsNullOrWhiteSpace(Name)) Name = "自定义设备";
    }

    /// <summary>校验 AVD 名称是否合法（模拟器只接受 [A-Za-z0-9._-]）。</summary>
    public static bool IsValidAvdName(string name) => AvdNameRegex.IsMatch(name);

    public override string ToString() => $"{Manufacturer} {Model} · {Width}x{Height} · {Dpi}dpi · {CpuCores}C/{RamMb}MB";

    // ---------------- 内置预设库 ----------------

    /// <summary>内置设备预设：键为预设显示名。</summary>
    public static Dictionary<string, DeviceProfile> Presets() => new()
    {
        ["Pixel 5"] = new DeviceProfile
        {
            Name = "Pixel 5", Manufacturer = "Google", Model = "Pixel 5 (redfin)",
            Width = 1080, Height = 2340, Dpi = 440, CpuCores = 4, RamMb = 2048, StorageGb = 8,
        },
        ["Pixel 6"] = new DeviceProfile
        {
            Name = "Pixel 6", Manufacturer = "Google", Model = "Pixel 6 (oriole)",
            Width = 1080, Height = 2400, Dpi = 420, CpuCores = 4, RamMb = 3072, StorageGb = 8,
        },
        ["小米 11"] = new DeviceProfile
        {
            Name = "小米 11", Manufacturer = "Xiaomi", Model = "M2011K2C",
            Width = 1440, Height = 3200, Dpi = 560, CpuCores = 4, RamMb = 4096, StorageGb = 16,
        },
        ["三星 S21"] = new DeviceProfile
        {
            Name = "三星 S21", Manufacturer = "samsung", Model = "SM-G991B",
            Width = 1080, Height = 2400, Dpi = 420, CpuCores = 4, RamMb = 3072, StorageGb = 8,
        },
    };
}

/// <summary>
/// 游戏适配模块：针对不同场景提供"兼容模式"预设，
/// 一键调整分辨率 / DPI / 渲染模式 / 资源配额。
/// </summary>
public static class GameTuning
{
    public sealed class Tuning
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required Action<DeviceProfile> Apply { get; init; }
        public override string ToString() => Name;
    }

    public static readonly List<Tuning> All = new()
    {
        new Tuning
        {
            Name = "高性能模式（大型 3D 游戏）",
            Description = "GPU 直通宿主机(host) + 4 核 + 原生分辨率，追求帧率上限",
            Apply = p =>
            {
                p.GpuMode = "host";
                p.CpuCores = Math.Min(4, Environment.ProcessorCount);
                p.RamMb = Math.Max(p.RamMb, 4096);
                p.NoSnapshot = true;
            },
        },
        new Tuning
        {
            Name = "兼容模式（老旧 2D 游戏）",
            Description = "ANGLE 软渲染兼容 + 降低 DPI 到 240，规避老旧引擎的高分屏适配 bug",
            Apply = p =>
            {
                p.GpuMode = "angle_indirect";
                p.Dpi = 240;
                p.CpuCores = 2;
                p.RamMb = 2048;
            },
        },
        new Tuning
        {
            Name = "降分辨率护航（低配宿主机）",
            Description = "分辨率减半（16:9/18:9 比例不变）+ DPI 降档，显著降低 GPU/CPU 负载",
            Apply = p =>
            {
                p.Width = Math.Max(480, p.Width / 2 / 8 * 8);
                p.Height = Math.Max(854, p.Height / 2 / 8 * 8);
                p.Dpi = Math.Max(160, p.Dpi * 3 / 5);
                p.RamMb = Math.Max(2048, p.RamMb);
            },
        },
        new Tuning
        {
            Name = "极速省电（挂机/脚本）",
            Description = "SwiftShader 纯软件渲染 + 半分辨率 + 2 核，最小化宿主机资源占用",
            Apply = p =>
            {
                p.GpuMode = "swiftshader_indirect";
                p.Width = Math.Max(480, p.Width / 2 / 8 * 8);
                p.Height = Math.Max(854, p.Height / 2 / 8 * 8);
                p.CpuCores = 2;
                p.RamMb = 2048;
                p.Headless = true;
            },
        },
    };
}

/// <summary>
/// AvdWriter —— 把 DeviceProfile 落地为 Android 模拟器能识别的 AVD 文件结构
/// （不依赖 avdmanager / Java / Android Studio，纯手写 INI，极客式自举）：
///
///   &lt;AvdHome&gt;\&lt;名字&gt;.ini          —— AVD 索引文件（avdmanager 目录约定）
///   &lt;AvdHome&gt;\&lt;名字&gt;.avd\config.ini —— 硬件配置
///   &lt;AvdHome&gt;\&lt;名字&gt;.avd\andboxone.profile.json —— AndboxOne 侧的完整配置快照
/// </summary>
public static class AvdWriter
{
    public static string AvdDir(string avdHome, string avdName) => Path.Combine(avdHome, $"{avdName}.avd");

    public static string ProfileSnapshotPath(string avdHome, string avdName) =>
        Path.Combine(AvdDir(avdHome, avdName), "andboxone.profile.json");

    /// <summary>写入完整 AVD 结构。已存在则覆盖配置文件（数据分区不受影响）。</summary>
    public static void WriteAvd(string avdHome, string avdName, DeviceProfile profile)
    {
        profile.Sanitize();
        string dir = AvdDir(avdHome, avdName);
        Directory.CreateDirectory(dir);

        // ---- config.ini：模拟器启动时读取的硬件定义 ----
        var ini = new StringBuilder();
        ini.AppendLine("avd.ini.encoding=UTF-8");
        ini.AppendLine($"AvdId={avdName}");
        ini.AppendLine($"abi={profile.Abi}");
        ini.AppendLine($"hw.cpu.arch={profile.Abi}");
        ini.AppendLine($"hw.cpu.ncore={profile.CpuCores}");
        ini.AppendLine($"hw.ramSize={profile.RamMb}");
        ini.AppendLine($"image.sysdir.1=system-images/android-{profile.ApiLevel}/{profile.TagId}/{profile.Abi}/");
        ini.AppendLine($"tag.id={profile.TagId}");
        ini.AppendLine($"tag.display={profile.ImageTagDisplay}");
        ini.AppendLine($"hw.lcd.width={profile.Width}");
        ini.AppendLine($"hw.lcd.height={profile.Height}");
        ini.AppendLine($"hw.lcd.density={profile.Dpi}");
        // 不写 hw.device.name：纯自定义硬件定义，避免依赖 devices.xml 机型库
        ini.AppendLine("hw.gpu.enabled=yes");
        ini.AppendLine($"hw.gpu.mode={profile.GpuMode}");
        ini.AppendLine("hw.keyboard=yes");
        ini.AppendLine("hw.audioInput=yes");
        ini.AppendLine("hw.audioOutput=yes");
        ini.AppendLine($"disk.dataPartition.size={profile.StorageGb}G");
        ini.AppendLine("disk.cachePartition.size=512M");
        ini.AppendLine("disk.cachePartition=yes");
        ini.AppendLine("runtime.network.latency=none");
        ini.AppendLine("runtime.network.speed=full");
        ini.AppendLine("PlayStore.enabled=false");
        ini.AppendLine("fastboot.forceFastBoot=no");
        File.WriteAllText(Path.Combine(dir, "config.ini"), ini.ToString(), new UTF8Encoding(false));

        // ---- 索引 .ini：avdmanager 目录约定 ----
        var index = new StringBuilder();
        index.AppendLine("avd.ini.encoding=UTF-8");
        index.AppendLine($"path={dir}");
        index.AppendLine($"path.rel=avd/{avdName}.avd");
        index.AppendLine($"target=android-{profile.ApiLevel}");
        File.WriteAllText(Path.Combine(avdHome, $"{avdName}.ini"), index.ToString(), new UTF8Encoding(false));

        // ---- AndboxOne 配置快照（找回 profile 用）----
        File.WriteAllText(ProfileSnapshotPath(avdHome, avdName), profile.ToJson(), new UTF8Encoding(false));

        EventBus.Info("AVD", $"已写入 AVD 配置: {avdName} ({profile})");
    }

    /// <summary>从 AVD 目录恢复 profile：优先读取 AndboxOne 快照，缺失时回退解析 config.ini。</summary>
    public static DeviceProfile? ReadProfile(string avdHome, string avdName)
    {
        string snapshot = ProfileSnapshotPath(avdHome, avdName);
        if (File.Exists(snapshot))
        {
            try { return DeviceProfile.FromJson(File.ReadAllText(snapshot)); }
            catch (Exception ex) { EventBus.Warn("AVD", $"快照损坏({avdName})，回退 config.ini: {ex.Message}"); }
        }

        string config = Path.Combine(AvdDir(avdHome, avdName), "config.ini");
        if (!File.Exists(config)) return null;

        var p = new DeviceProfile { Name = avdName };
        foreach (string line in File.ReadAllLines(config))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string k = line[..eq].Trim(), v = line[(eq + 1)..].Trim();
            switch (k)
            {
                case "hw.lcd.width" when int.TryParse(v, out int w): p.Width = w; break;
                case "hw.lcd.height" when int.TryParse(v, out int h): p.Height = h; break;
                case "hw.lcd.density" when int.TryParse(v, out int d): p.Dpi = d; break;
                case "hw.cpu.ncore" when int.TryParse(v, out int c): p.CpuCores = c; break;
                case "hw.ramSize" when int.TryParse(v, out int r): p.RamMb = r; break;
                case "hw.gpu.mode": p.GpuMode = v; break;
                case "image.sysdir.1":
                    // system-images/android-30/google_apis/x86_64/
                    string[] parts = v.Split('/', '\\');
                    for (int i = 0; i < parts.Length - 1; i++)
                    {
                        if (parts[i].Equals("system-images", StringComparison.OrdinalIgnoreCase) &&
                            i + 2 < parts.Length &&
                            int.TryParse(parts[i + 1].Replace("android-", ""), out int api))
                        {
                            p.ApiLevel = api;
                            p.TagId = parts[i + 2];
                        }
                    }
                    break;
            }
        }
        return p;
    }

    /// <summary>枚举 AVD 主目录中的全部 AVD 名称（含外部创建的）。</summary>
    public static List<string> ListAvds(string avdHome)
    {
        var result = new List<string>();
        if (!Directory.Exists(avdHome)) return result;
        foreach (string ini in Directory.EnumerateFiles(avdHome, "*.ini"))
        {
            string name = Path.GetFileNameWithoutExtension(ini);
            if (name == "avd") continue; // 跳过可能出现的杂项
            if (Directory.Exists(AvdDir(avdHome, name))) result.Add(name);
        }
        return result.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
