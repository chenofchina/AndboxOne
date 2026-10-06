using System.Diagnostics;
using System.IO;

namespace AndboxOne.Core;

/// <summary>
/// EnvironmentManager —— 离线 RuntimeSdk 的路径自适应与校验中枢。
///
/// 核心职责（离线打包架构的落地点）：
///   1. 程序启动时通过 Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RuntimeSdk")
///      定位随主程序离线打包发布的全套 Android 虚拟化引擎；
///   2. 自动扫描 RuntimeSdk 目录，验证 emulator.exe、adb.exe、system-images 是否存在；
///   3. 如果缺失，给出明确错误日志："RuntimeSdk 不完整，请重新安装 AndboxOne"；
///   4. 如果完整，自动设置 ANDROID_HOME / ANDROID_SDK_ROOT / ANDROID_AVD_HOME 等环境变量，
///      并把 platform-tools 与 emulator 目录注入本进程 PATH，供所有子进程继承。
///
/// 可写数据目录策略：
///   安装目录可能位于 Program Files（不可写），因此 AVD、日志、图标缓存等
///   可写数据统一放 %LOCALAPPDATA%\AndboxOne；可用环境变量 ANDBOXONE_DATA_HOME
///   覆盖以实现"绿色便携版"模式。
/// </summary>
public sealed class EnvironmentManager
{
    /// <summary>主程序基目录（含 RuntimeSdk 的根）。</summary>
    public string BaseDirectory { get; } = AppDomain.CurrentDomain.BaseDirectory;

    /// <summary>离线 SDK 根目录：&lt;BaseDirectory&gt;\RuntimeSdk。</summary>
    public string SdkRoot { get; }
    public string EmulatorDir { get; }
    public string PlatformToolsDir { get; }
    public string SystemImageDir { get; }

    /// <summary>可写数据根目录（AVD / 日志 / 图标缓存）。</summary>
    public string UserData { get; }
    public string AvdHome { get; }
    public string LogsDir { get; }
    public string IconsDir { get; }
    public string EmulatorHome { get; }
    public string AndroidUserHome { get; }

    /// <summary>关键二进制路径。</summary>
    public string AdbPath { get; private set; } = string.Empty;
    public string EmulatorPath { get; private set; } = string.Empty;
    /// <summary>可选：build-tools 下的 aapt.exe（存在则用于 APK badging，缺失则回退内置 AXML 解析器）。</summary>
    public string? AaptPath { get; private set; }

    /// <summary>校验是否完全通过（emulator + adb + 系统镜像全部就位）。</summary>
    public bool IsValid { get; private set; }
    /// <summary>二进制核心（emulator + adb）是否就位——决定能否执行 ADB 操作。</summary>
    public bool CoreBinariesOk { get; private set; }
    /// <summary>校验发现的所有问题清单（每项一条中文描述）。</summary>
    public List<string> Problems { get; } = new();

    private bool _initialized;

    public EnvironmentManager()
    {
        SdkRoot = Path.Combine(BaseDirectory, "RuntimeSdk");
        EmulatorDir = Path.Combine(SdkRoot, "emulator");
        PlatformToolsDir = Path.Combine(SdkRoot, "platform-tools");
        SystemImageDir = Path.Combine(SdkRoot, "system-images", "android-30", "google_apis", "x86_64");

        string dataRoot = Environment.GetEnvironmentVariable("ANDBOXONE_DATA_HOME")
                          ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                          "AndboxOne");
        UserData = dataRoot;
        AvdHome = Path.Combine(dataRoot, "avd");
        LogsDir = Path.Combine(dataRoot, "logs");
        IconsDir = Path.Combine(dataRoot, "icons");
        EmulatorHome = Path.Combine(dataRoot, "emulator-home");   // 存放模拟器控制台授权 token
        AndroidUserHome = Path.Combine(dataRoot, "android-user"); // 存放 adbkey 等用户级配置
    }

    /// <summary>
    /// 执行目录扫描 + 校验 + 环境变量设置。幂等，可在启动时调用一次。
    /// 校验结果写入 Problems / IsValid，全部过程通过 EventBus 广播。
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        EventBus.Info("ENV", $"AndboxOne 启动 · 基目录: {BaseDirectory}");
        EventBus.Info("ENV", $"离线 SDK 根目录: {SdkRoot}");

        // ---- 1. 创建可写数据目录 ----
        try
        {
            Directory.CreateDirectory(AvdHome);
            Directory.CreateDirectory(LogsDir);
            Directory.CreateDirectory(IconsDir);
            Directory.CreateDirectory(EmulatorHome);
            Directory.CreateDirectory(AndroidUserHome);
        }
        catch (Exception ex)
        {
            Problems.Add($"无法创建数据目录 {UserData}: {ex.Message}");
        }

        // ---- 2. 扫描校验 RuntimeSdk ----
        Problems.Clear();
        CoreBinariesOk = true;

        AdbPath = Path.Combine(PlatformToolsDir, "adb.exe");
        EmulatorPath = Path.Combine(EmulatorDir, "emulator.exe");

        if (File.Exists(AdbPath))
            EventBus.Info("ENV", $"[OK] adb.exe 就位: {AdbPath}");
        else
        {
            CoreBinariesOk = false;
            Problems.Add("platform-tools\\adb.exe 缺失");
            EventBus.Error("ENV", "[缺失] platform-tools\\adb.exe");
        }

        if (File.Exists(EmulatorPath))
        {
            EventBus.Info("ENV", $"[OK] emulator.exe 就位: {EmulatorPath}");
            // 引擎完整性：lib 目录是 QEMU 内核所在地，缺了跑不起来
            if (!Directory.Exists(Path.Combine(EmulatorDir, "lib")))
            {
                CoreBinariesOk = false;
                Problems.Add("emulator\\lib\\ 目录缺失（Emulator 引擎不完整）");
                EventBus.Error("ENV", "[缺失] emulator\\lib\\ 引擎目录");
            }
        }
        else
        {
            CoreBinariesOk = false;
            Problems.Add("emulator\\emulator.exe 缺失");
            EventBus.Error("ENV", "[缺失] emulator\\emulator.exe");
        }

        bool imageOk = Directory.Exists(SystemImageDir) &&
                       Directory.EnumerateFiles(SystemImageDir, "*.img").Any();
        if (imageOk)
            EventBus.Info("ENV", $"[OK] 系统镜像就位: {SystemImageDir}");
        else
        {
            Problems.Add("system-images\\android-30\\google_apis\\x86_64\\ 系统镜像缺失");
            EventBus.Error("ENV", "[缺失] Android 11 (google_apis/x86_64) 系统镜像");
        }

        IsValid = Problems.Count == 0;

        // ---- 3. 依规给出明确错误 ----
        if (!IsValid)
        {
            EventBus.Error("ENV", "RuntimeSdk 不完整，请重新安装 AndboxOne");
            foreach (string p in Problems) EventBus.Error("ENV", $"  → {p}");
        }

        // ---- 4. 定位可选的 aapt（build-tools）----
        AaptPath = FindAapt();
        if (AaptPath is not null)
            EventBus.Info("ENV", $"[OK] 检测到可选 aapt: {AaptPath}");
        else
            EventBus.Debug("ENV", "未检测到 build-tools\\aapt.exe，APK 解析将使用内置 AXML 引擎");

        // ---- 5. 设置环境变量（仅对本进程生效，子进程全部继承）----
        SetVar("ANDROID_HOME", SdkRoot);
        SetVar("ANDROID_SDK_ROOT", SdkRoot);
        SetVar("ANDROID_AVD_HOME", AvdHome);
        SetVar("ANDROID_EMULATOR_HOME", EmulatorHome);
        SetVar("ANDROID_USER_HOME", AndroidUserHome);
        SetVar("ANDROID_PREFS_ROOT", AndroidUserHome);
        SetVar("ANDROID_SDK_HOME", AndroidUserHome);

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string prepend = PlatformToolsDir + Path.PathSeparator + EmulatorDir + Path.PathSeparator;
        if (!path.StartsWith(prepend))
            Environment.SetEnvironmentVariable("PATH", prepend + path, EnvironmentVariableTarget.Process);

        EventBus.Info("ENV", IsValid
            ? "环境校验通过：ANDROID_HOME / ANDROID_SDK_ROOT 已指向离线 RuntimeSdk，进入主界面"
            : $"环境校验完成：{Problems.Count} 项缺失（详见错误日志）");
    }

    private void SetVar(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
        EventBus.Debug("ENV", $"{name} = {value}");
    }

    private string? FindAapt()
    {
        string buildTools = Path.Combine(SdkRoot, "build-tools");
        if (!Directory.Exists(buildTools)) return null;
        return Directory.EnumerateDirectories(buildTools)
            .OrderByDescending(d => d)
            .Select(d => Path.Combine(d, "aapt.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>返回 adb 版本号（如 "43.0.0"），失败返回 null。同时用于探活 adb 二进制。</summary>
    public string? QueryAdbVersion()
    {
        if (!File.Exists(AdbPath)) return null;
        try
        {
            using Process p = Process.Start(new ProcessStartInfo
            {
                FileName = AdbPath,
                Arguments = "version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            })!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            // "Android Debug Bridge version 1.0.41  Version 43.0.0-..."
            foreach (string line in output.Split('\n'))
                if (line.Contains("Version", StringComparison.OrdinalIgnoreCase))
                    return line.Replace("Version", "").Trim().Split('-')[0];
        }
        catch (Exception ex) { EventBus.Warn("ENV", $"查询 adb 版本失败: {ex.Message}"); }
        return null;
    }
}
