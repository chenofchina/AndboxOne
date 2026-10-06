using System.Diagnostics;
using System.IO;
using System.Windows;
using AndboxOne.Core;

namespace AndboxOne;

/// <summary>
/// 应用入口 —— 支持四种启动模式：
///
///   1. 常规启动（无参数）            → 三栏主界面
///   2. --selftest                   → 逻辑自检套件，退出码 = 失败用例数（CI/构建验证用）
///   3. --uitest                     → 启动主界面渲染截图到 .uitest\ 后退出（布局验证用）
///   4. --launch-app --avd X --package Y --activity Z
///                                   → 桌面快捷方式协议：自动启动对应虚拟机并运行 App，
///                                     完成后本进程退出（桌面级集成的核心链路）
/// </summary>
public partial class App : Application
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    protected override void OnStartup(StartupEventArgs e)
    {
        string[] args = e.Args ?? Array.Empty<string>();

        // ---- 自检模式：不加载 WPF 窗口 ----
        if (args.Contains("--selftest"))
        {
            int rc = SelfTest.RunAll();
            Shutdown(rc);
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += (_, ex) =>
        {
            EventBus.Error("APP", $"UI 线程未处理异常: {ex.Exception.GetType().Name}: {ex.Exception.Message}");
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            EventBus.Error("APP", $"致命异常: {ex.ExceptionObject}");

        // ---- 桌面快捷方式协议 ----
        if (args.Contains("--launch-app"))
        {
            _ = RunLauncherCoreAsync(args);
            return;
        }

        // ---- UI 自检 / 常规 ----
        // 注意：必须先设 UiTestMode 再创建窗口——Show() 会同步触发 Loaded 初始化流程
        if (args.Contains("--uitest"))
            AndboxOne.MainWindow.UiTestMode = true;

        var mw = new MainWindow();
        MainWindow = mw;
        mw.Show();

        if (args.Contains("--uitest"))
            _ = RunUiTestFlowAsync(mw);
    }

    // ------------------------------------------------------------------
    //  桌面快捷方式协议：启动虚拟机 → 等待引导 → am start → 退出
    // ------------------------------------------------------------------

    private async Task RunLauncherCoreAsync(string[] args)
    {
        string? avd = ArgValue(args, "--avd");
        string? package = ArgValue(args, "--package");
        string? activity = ArgValue(args, "--activity");

        try
        {
            try { if (AttachConsole(-1)) Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }); }
            catch { /* 无父控制台 */ }

            var env = new EnvironmentManager();
            Directory.CreateDirectory(env.LogsDir);
            using var sink = new LogFileSink(Path.Combine(env.LogsDir, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log"));

            EventBus.Info("LAUNCH", $"快捷启动协议触发: avd={avd} package={package} activity={activity}");
            Console.WriteLine($"[AndboxOne] 快捷启动: {package} @ {avd}");

            env.Initialize();
            if (!env.CoreBinariesOk)
            {
                EventBus.Error("LAUNCH", "RuntimeSdk 不完整，请重新安装 AndboxOne");
                Console.WriteLine("[AndboxOne] RuntimeSdk 不完整，无法启动");
                Shutdown(2);
                return;
            }

            using var orch = new EmulatorOrchestrator(env);
            var net = new NetworkManager();
            orch.LoadVms();

            VmInstance? vm = orch.Vms.FirstOrDefault(v => string.Equals(v.AvdName, avd, StringComparison.OrdinalIgnoreCase));
            if (vm is null)
            {
                EventBus.Error("LAUNCH", $"虚拟机 {avd} 不存在（可能已被删除）");
                Console.WriteLine($"[AndboxOne] 虚拟机 {avd} 不存在");
                Shutdown(3);
                return;
            }

            // 附加可能已在运行的同名实例
            if (!vm.IsRunning)
            {
                await orch.AttachExternalVmsAsync();
                vm = orch.Vms.First(v => string.Equals(v.AvdName, avd, StringComparison.OrdinalIgnoreCase));
            }

            if (!vm.IsRunning)
            {
                EventBus.Info("LAUNCH", $"启动虚拟机 {vm.AvdName}…");
                Console.WriteLine("[AndboxOne] 启动虚拟机（首次冷启动可能需要 1-3 分钟）…");
                await orch.StartVmAsync(vm, net);
            }
            else if (vm.State != VmState.Online)
            {
                await orch.WaitBootAsync(vm.Serial);
            }

            vm = orch.Vms.First(v => string.Equals(v.AvdName, avd, StringComparison.OrdinalIgnoreCase));
            if (vm.State != VmState.Online)
            {
                EventBus.Error("LAUNCH", "虚拟机未能在线，启动 App 失败");
                Console.WriteLine("[AndboxOne] 虚拟机引导失败");
                Shutdown(4);
                return;
            }

            string target = string.IsNullOrEmpty(activity) ? package! : $"{package}/{activity.TrimStart('.')}";
            EventBus.Info("LAUNCH", $"am start -n {target}");
            AdbResult r = await orch.AdbAsync(vm.Serial, $"shell am start -n \"{target}\"", timeoutMs: 60000);
            if (r.Ok)
            {
                EventBus.Info("LAUNCH", $"App 已拉起: {package} @ {vm.AvdName}");
                Console.WriteLine("[AndboxOne] App 已启动 ✔");
                Shutdown(0);
            }
            else
            {
                EventBus.Error("LAUNCH", $"am start 失败: {r.StdErr.Trim()}（activity 可能为空或类名错误）");
                Console.WriteLine("[AndboxOne] am start 失败，详见日志");
                Shutdown(5);
            }
        }
        catch (Exception ex)
        {
            EventBus.Error("LAUNCH", $"快捷启动异常: {ex}");
            Console.WriteLine($"[AndboxOne] 异常: {ex.Message}");
            try { Shutdown(6); } catch { /* 已关闭 */ }
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // ------------------------------------------------------------------
    //  UI 自检：渲染主窗口截图后退出
    // ------------------------------------------------------------------

    private async Task RunUiTestFlowAsync(MainWindow mw)
    {
        try
        {
            string dir = Path.Combine(Environment.CurrentDirectory, ".uitest");
            await mw.RunUiSelfTestAsync(dir);
            await Task.Delay(500);
            EventBus.Info("UITEST", "完成，正常退出");
            Shutdown(0);
        }
        catch (Exception ex)
        {
            EventBus.Error("UITEST", $"失败: {ex}");
            try { Shutdown(3); } catch { /* 已关闭 */ }
        }
    }
}
