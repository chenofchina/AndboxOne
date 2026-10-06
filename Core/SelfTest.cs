using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AndboxOne.Core;

/// <summary>
/// SelfTest —— 编译验证用的逻辑自检套件（--selftest 触发，无 UI）。
/// 覆盖：EventBus / DeviceProfile JSON 往返 / 网络参数 / AVD 文件读写 /
/// 安装报告 / ICO 封装 / 内置 AXML 引擎（合成二进制回灌）。
/// 退出码 = 失败用例数，供 CI / 构建脚本断言。
/// </summary>
public static class SelfTest
{
    private static int _pass, _fail;
    private static readonly List<string> _lines = new();

    public static int RunAll()
    {
        ConsoleAttach();
        Banner("ANDBOXONE SELFTEST");
        EventBus.LogEmitted += (line, _) => Say("  [bus] " + line);

        T1_EventBus();
        T2_DeviceProfileJson();
        T3_NetworkArgs();
        T4_AvdWriterRoundtrip();
        T5_InstallReport();
        T6_IcoWrapper();
        T7_AxmlEngine();
        T8_EnvironmentProbe();
        T9_WindowPlacer();

        Banner($"RESULT: {_pass} PASS / {_fail} FAIL");

        try
        {
            string dir = Path.Combine(new EnvironmentManager().LogsDir);
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "selftest.log"), _lines);
        }
        catch { /* 尽力落盘 */ }

        return _fail;
    }

    // ------------------------------------------------------------------

    private static void T1_EventBus()
    {
        Case("EventBus 发布/订阅格式", () =>
        {
            string? got = null; LogLevel gotLevel = LogLevel.Debug;
            void Handler(string line, LogLevel lv) { got = line; gotLevel = lv; }
            EventBus.LogEmitted += Handler;
            EventBus.Publish("SELFTEST", "hello 世界", LogLevel.Warn);
            EventBus.LogEmitted -= Handler;
            Check(got is not null && got!.Contains("[SELFTEST]") && got.Contains("[WARN]") && got.Contains("hello 世界"),
                $"行格式: {got}");
            Check(gotLevel == LogLevel.Warn, "级别透传");
        });
    }

    private static void T2_DeviceProfileJson()
    {
        Case("DeviceProfile 预设与 JSON 往返", () =>
        {
            var presets = DeviceProfile.Presets();
            Check(presets.Count == 4 && presets.ContainsKey("Pixel 5") && presets.ContainsKey("小米 11"),
                $"预设: {string.Join(", ", presets.Keys)}");

            DeviceProfile p = presets["Pixel 5"].Clone();
            p.Manufacturer = "Xiaomi-测试";
            string json = p.ToJson();
            DeviceProfile back = DeviceProfile.FromJson(json);
            Check(back.Width == p.Width && back.Height == p.Height && back.Dpi == p.Dpi &&
                  back.CpuCores == p.CpuCores && back.RamMb == p.RamMb &&
                  back.Manufacturer == "Xiaomi-测试" && back.JsonEquals(p), "往返字段一致");
            Check(DeviceProfile.IsValidAvdName("Andbox_Pixel5-01") && !DeviceProfile.IsValidAvdName("坏 名 字"),
                "AVD 名称校验");
        });
    }

    private static void T3_NetworkArgs()
    {
        Case("NetworkManager 启动参数", () =>
        {
            var net = new NetworkManager();
            string nat = net.BuildLaunchArgs();
            Check(nat.Contains("-netdelay none") && nat.Contains("-netspeed full"), $"NAT: {nat}");

            net.Mode = NetworkMode.Bridge;
            net.BridgeAdapterName = "tap-andbox";
            string bridge = net.BuildLaunchArgs();
            Check(bridge.Contains("-qemu") && bridge.Contains("ifname=tap-andbox"), $"桥接: {bridge}");
        });
    }

    private static void T4_AvdWriterRoundtrip()
    {
        Case("AvdWriter 写入/读回（纯手写 INI 自举）", () =>
        {
            string tmp = Path.Combine(Path.GetTempPath(), "andboxone-selftest-avd");
            try
            {
                Directory.CreateDirectory(tmp);
                var p = DeviceProfile.Presets()["Pixel 6"].Clone();
                p.Width = 1152; p.Height = 2496; p.Dpi = 420; p.CpuCores = 6; p.RamMb = 4096;
                AvdWriter.WriteAvd(tmp, "SelfTest_AVD", p);

                string config = File.ReadAllText(Path.Combine(tmp, "SelfTest_AVD.avd", "config.ini"));
                Check(config.Contains("hw.lcd.width=1152") && config.Contains("image.sysdir.1=system-images/android-30/google_apis/x86_64/"),
                    "config.ini 关键项");
                Check(File.Exists(Path.Combine(tmp, "SelfTest_AVD.ini")), "索引 ini 存在");

                DeviceProfile? back = AvdWriter.ReadProfile(tmp, "SelfTest_AVD");
                Check(back is not null && back.Width == 1152 && back.CpuCores == 6 && back.ApiLevel == 30,
                    $"快照读回: {back}");
                Check(AvdWriter.ListAvds(tmp).Single() == "SelfTest_AVD", "AVD 枚举");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        });
    }

    private static void T5_InstallReport()
    {
        Case("安装报告（HTML/文本/权限映射）", () =>
        {
            var info = new ApkInstallInfo
            {
                Package = "com.example.geekapp",
                VersionName = "1.2.3",
                VersionCode = 123,
                Label = "极客应用",
                ApkSizeBytes = 52_000_000,
                InstalledSizeBytes = 104_857_600,
                InstallTime = new DateTime(2026, 10, 6, 12, 0, 0),
                TargetDevice = "Andbox_Pixel5 (emulator-5554)",
                Permissions = new List<string>
                {
                    "android.permission.INTERNET",
                    "android.permission.READ_CONTACTS",
                    "android.permission.CAMERA",
                },
            };
            string html = InstallReportGenerator.GenerateHtml(info);
            Check(html.Contains("com.example.geekapp") && html.Contains("极客应用") && html.Contains("读取通讯录") &&
                  html.Contains("49.59 MB") && html.Contains("敏感"), "HTML 关键内容");

            string text = InstallReportGenerator.ToPlainText(info);
            Check(text.Contains("[敏感] 使用摄像头") && text.Contains("访问网络"), "文本报告 + 权限映射");
            Check(InstallReportGenerator.Describe("android.permission.NOT_A_REAL_ONE") == "（未收录释义）", "未知权限兜底");
        });
    }

    private static void T6_IcoWrapper()
    {
        Case("图标：默认图标生成 + ICO 封装", () =>
        {
            byte[] png = ApkInspector.GenerateDefaultIconPng();
            Check(png.Length > 100 && png[0] == 0x89 && png[1] == (byte)'P', $"PNG 签名 + {png.Length}B");

            byte[] ico = ApkInspector.WrapPngAsIco(png, 96, 96);
            Check(ico[0] == 0 && ico[1] == 0 && ico[2] == 1 && ico[3] == 0 && ico[4] == 1 && ico[5] == 0,
                "ICONDIR 头 (type=1, count=1)");
            Check(BitConverter.ToUInt32(ico, 14) == (uint)png.Length && BitConverter.ToUInt32(ico, 18) == 22u,
                "目录项尺寸/偏移");
        });
    }

    private static void T7_AxmlEngine()
    {
        Case("内置 AXML 引擎（合成二进制回灌解析）", () =>
        {
            byte[] axml = BuildTestAxml();
            var info = new ApkInfo();
            ApkInspector.ParseAxml(axml, info);
            Check(info.Package == "com.andboxone.selftest", $"package = {info.Package}");
            Check(info.VersionName == "9.9.9" && info.VersionCode == 99, $"version = {info.VersionName}({info.VersionCode})");
            Check(info.Permissions.Contains("android.permission.INTERNET"), $"permissions = [{string.Join(", ", info.Permissions)}]");
        });
    }

    private static void T8_EnvironmentProbe()
    {
        Case("EnvironmentManager 校验流程", () =>
        {
            var env = new EnvironmentManager();
            env.Initialize();
            Say($"  [env] IsValid={env.IsValid} Problems=[{string.Join(" | ", env.Problems)}]");
            Check(env.Problems.Count >= 0 && env.SdkRoot.EndsWith("RuntimeSdk"), "目录定位");
            Check(Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") == env.SdkRoot, "环境变量已设置");
        });
    }

    private static void T9_WindowPlacer()
    {
        Case("WindowPlacer 窗口归位（构造越界场景）", () =>
        {
            // 用 Win32 STATIC 类创建测试窗口，放到标题栏远在屏幕上方的位置（用户遇到的场景）
            IntPtr hwnd = CreateWindowExW(0, "STATIC", "AndboxSelfTestWindow", 0x00CF0000 /*WS_OVERLAPPEDWINDOW*/,
                -100, -900, 800, 600, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            Check(hwnd != IntPtr.Zero, "测试窗口创建");

            GetWindowRect(hwnd, out RECT r);
            Check(r.Top < 0, $"预置越界: Top={r.Top}");

            bool moved = WindowPlacer.EnsureOnScreen(hwnd, 0);
            Check(moved, "检测越界并触发归位");

            GetWindowRect(hwnd, out RECT r2);
            int scrW = GetSystemMetrics(0), scrH = GetSystemMetrics(1);
            bool onScreen = r2.Top >= 0 && r2.Left >= 0 && r2.Right <= scrW && r2.Bottom <= scrH;
            Check(onScreen, $"归位后 ({r2.Left},{r2.Top})-({r2.Right},{r2.Bottom}) 在屏内 ({scrW}x{scrH})");

            bool movedAgain = WindowPlacer.EnsureOnScreen(hwnd, 0);
            Check(!movedAgain, "已屏内窗口不被误移动");
            DestroyWindow(hwnd);
        });
    }

    // ---- Win32（T9 专用）----

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(int exStyle, string cls, string title, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? name);

    // ------------------------------------------------------------------
    //  合成最小 AXML：manifest(package/versionName) + uses-permission
    // ------------------------------------------------------------------

    private static byte[] BuildTestAxml()
    {
        var strings = new List<string>
        {
            "manifest",                                // 0
            "package",                                 // 1
            "com.andboxone.selftest",                  // 2
            "uses-permission",                         // 3
            "name",                                    // 4
            "android.permission.INTERNET",             // 5
            "http://schemas.android.com/apk/res/android", // 6
            "versionName",                             // 7
            "9.9.9",                                   // 8
            "versionCode",                             // 9
        };

        // ---- StringPool (UTF-16, flags=0) ----
        var pool = new MemoryStream();
        var encoded = new List<byte[]>();
        var offsets = new List<uint>();
        uint acc = 0;
        foreach (string s in strings)
        {
            byte[] body = Encoding.Unicode.GetBytes(s);
            var buf = new byte[2 + body.Length + 2];
            BitConverter.GetBytes((ushort)s.Length).CopyTo(buf, 0);
            body.CopyTo(buf, 2);
            encoded.Add(buf);
            offsets.Add(acc);
            acc += (uint)buf.Length;
        }
        int stringsSize = encoded.Sum(b => b.Length);
        int pad = (4 - stringsSize % 4) % 4;
        int stringsStart = 28 + strings.Count * 4;
        int poolSize = stringsStart + stringsSize + pad;

        var bw = new BinaryWriter(pool);
        bw.Write((ushort)0x0001); bw.Write((ushort)28); bw.Write((uint)poolSize);
        bw.Write((uint)strings.Count); bw.Write(0u); bw.Write(0u);          // count / styleCount / flags
        bw.Write((uint)stringsStart); bw.Write(0u);                          // stringsStart / stylesStart
        foreach (uint off in offsets) bw.Write(off);
        foreach (byte[] b in encoded) bw.Write(b);
        for (int i = 0; i < pad; i++) bw.Write((byte)0);
        byte[] poolBytes = pool.ToArray();

        // ---- 元素块 ----
        byte[] startManifest = StartElement(0, 1, new (int ns, int name, int raw, byte dt, uint data)[]
        {
            (-1, 1, 2, 0x03, 2),      // package (no ns)
            (6, 9, -1, 0x10, 99),     // android:versionCode (int)
            (6, 7, 8, 0x03, 8),       // android:versionName (string)
        });
        byte[] startPerm = StartElement(3, 4, new (int ns, int name, int raw, byte dt, uint data)[]
        {
            (6, 4, 5, 0x03, 5),       // android:name = android.permission.INTERNET
        });
        byte[] endPerm = EndElement(3);
        byte[] endManifest = EndElement(0);

        int chunksSize = poolBytes.Length + startManifest.Length + startPerm.Length + endPerm.Length + endManifest.Length;
        int fileSize = 8 + chunksSize;

        var all = new MemoryStream();
        var head = new BinaryWriter(all);
        head.Write((ushort)0x0003); head.Write((ushort)8); head.Write((uint)fileSize);
        head.Write(poolBytes); head.Write(startManifest); head.Write(startPerm);
        head.Write(endPerm); head.Write(endManifest);
        return all.ToArray();
    }

    private static byte[] StartElement(int nameIdx, int _, (int ns, int name, int raw, byte dt, uint data)[] attrs)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int size = 16 + 20 + attrs.Length * 20;
        w.Write((ushort)0x0102); w.Write((ushort)16); w.Write((uint)size);
        w.Write(1); w.Write(-1);                 // lineNumber / comment
        w.Write(-1); w.Write(nameIdx);           // ns / name
        w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)attrs.Length);
        w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); // id/class/style index
        foreach (var a in attrs)
        {
            w.Write(a.ns); w.Write(a.name); w.Write(a.raw);
            w.Write((ushort)8); w.Write((byte)0); w.Write(a.dt); w.Write(a.data);
        }
        return ms.ToArray();
    }

    private static byte[] EndElement(int nameIdx)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)0x0103); w.Write((ushort)16); w.Write(24);
        w.Write(1); w.Write(-1);
        w.Write(-1); w.Write(nameIdx);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------
    //  输出工具
    // ------------------------------------------------------------------

    private static void Banner(string title)
    {
        Say("");
        Say("══════════════════════════════════════════");
        Say($"  {title}   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Say("══════════════════════════════════════════");
    }

    private static void Case(string name, Action body)
    {
        int before = _fail;
        Say($"\n▶ {name}");
        try { body(); }
        catch (Exception ex) { Check(false, $"异常: {ex.Message}"); }
        Say(before == _fail ? "  ✔ PASS" : "  ✘ FAIL");
    }

    private static void Check(bool ok, string what)
    {
        if (ok) { _pass++; Say($"    ✓ {what}"); }
        else { _fail++; Say($"    ✗ {what}"); }
    }

    private static void Say(string s)
    {
        _lines.Add(s);
        try { Console.WriteLine(s); } catch { /* 无控制台时静默 */ }
    }

    private static void ConsoleAttach()
    {
        try
        {
            if (AttachConsole(-1))
            {
                var stdout = Console.OpenStandardOutput();
                Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
            }
        }
        catch { /* 双击启动无父控制台，忽略 */ }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
