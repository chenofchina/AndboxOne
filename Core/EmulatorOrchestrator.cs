using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace AndboxOne.Core;

/// <summary>一条 ADB 命令的执行结果。</summary>
public sealed record AdbResult(int ExitCode, string StdOut, string StdErr, bool TimedOut = false)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
}

/// <summary>ADB 队列任务：High 通道用于状态轮询（引导探测等），Normal 用于业务命令。</summary>
internal sealed record AdbJob(string? Serial, string Args, int TimeoutMs,
    TaskCompletionSource<AdbResult> Tcs, bool High, CancellationToken Ct);

/// <summary>
/// EmulatorOrchestrator —— 虚拟机编排引擎（AndboxOne 的心脏）。
///
/// 职责清单：
///   1. 调用 emulator.exe -avd &lt;名字&gt; -writable-system 启动虚拟机，实时收集
///      标准输出 / 错误输出并广播到 EventBus；
///   2. 维护 ADB 命令队列：多条 adb 命令严格按顺序执行（两级优先级通道，
///      High 通道供引导探测/资源轮询插队，业务命令始终按入队顺序执行）；
///   3. 批量操作：选中多个虚拟机，一键全部启动 / 全部关闭 / 全部 Root；
///   4. AVD 生命周期：创建（纯手写 INI，不依赖 avdmanager/Java）、删除、枚举、
///      附加外部已运行实例；
///   5. APK 安装流水线：adb install -r → dumpsys package → du 占用统计；
///   6. 每 2 秒采样各运行实例的 CPU / 内存，回填到 VmInstance 供 UI 实时渲染。
/// </summary>
public sealed class EmulatorOrchestrator : IDisposable
{
    private readonly EnvironmentManager _env;
    private readonly CancellationTokenSource _cts = new();

    // ---- ADB 命令队列（保证多条命令按顺序执行）----
    private readonly Channel<AdbJob> _highQueue = Channel.CreateUnbounded<AdbJob>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<AdbJob> _normalQueue = Channel.CreateUnbounded<AdbJob>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly object _portLock = new();
    private readonly HashSet<int> _reservedPorts = new();

    private readonly System.Timers.Timer _sampler;
    private Task? _worker;

    public List<VmInstance> Vms { get; } = new();

    public EmulatorOrchestrator(EnvironmentManager env)
    {
        _env = env;
        _sampler = new System.Timers.Timer(2000) { AutoReset = true };
        _sampler.Elapsed += (_, _) => SampleAll();
    }

    // ================================================================
    //  ADB 命令队列
    // ================================================================

    private Task StartWorker()
    {
        if (_worker is not null) return _worker;
        _worker = Task.Run(QueueLoop);
        return _worker;
    }

    private async Task QueueLoop()
    {
        CancellationToken ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            // 先排空高优先级通道
            while (_highQueue.Reader.TryRead(out AdbJob? h))
                await RunJob(h);

            // 双通道等待：任一通道有任务即唤醒（高优先级优先消费）
            Task<bool> hw = _highQueue.Reader.WaitToReadAsync(ct).AsTask();
            Task<bool> nw = _normalQueue.Reader.WaitToReadAsync(ct).AsTask();
            Task done = await Task.WhenAny(hw, nw);

            bool woke;
            try { woke = done == hw ? hw.Result : nw.Result; }
            catch (OperationCanceledException) { break; }
            if (!woke) break;

            while (_highQueue.Reader.TryRead(out AdbJob? h))
                await RunJob(h);
            if (_normalQueue.Reader.TryRead(out AdbJob? n))
                await RunJob(n);
        }
    }

    private async Task RunJob(AdbJob job)
    {
        if (job.Ct.IsCancellationRequested)
        {
            job.Tcs.TrySetCanceled(job.Ct);
            return;
        }
        try
        {
            job.Tcs.SetResult(await ExecAdbCore(job.Serial, job.Args, job.TimeoutMs, job.Ct));
        }
        catch (OperationCanceledException)
        {
            job.Tcs.TrySetCanceled(job.Ct);
        }
        catch (Exception ex)
        {
            EventBus.Error("ADB", $"命令异常: {job.Args} → {ex.Message}");
            job.Tcs.TrySetResult(new AdbResult(-1, "", ex.Message));
        }
    }

    private async Task<AdbResult> ExecAdbCore(string? serial, string args, int timeoutMs, CancellationToken ct)
    {
        if (!File.Exists(_env.AdbPath))
            return new AdbResult(-1, "", "adb.exe 不存在：RuntimeSdk 不完整，请重新安装 AndboxOne");

        string fullArgs = string.IsNullOrWhiteSpace(serial) ? args : $"-s {serial} {args}";
        EventBus.Debug("ADB", $"> adb {fullArgs}");

        var psi = new ProcessStartInfo
        {
            FileName = _env.AdbPath,
            Arguments = fullArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var p = new Process { StartInfo = psi };
        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.Enqueue(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.Enqueue(e.Data); };

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bool timedOut = true, cancelled = false;
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token);
            timedOut = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (OperationCanceledException)
        {
            // 超时路径
        }

        if (timedOut || cancelled)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            string why = cancelled ? "被取消" : $"超时({timeoutMs}ms)";
            EventBus.Warn("ADB", $"adb {fullArgs} {why}");
            return new AdbResult(-1, string.Join('\n', stdout), string.Join('\n', stderr), TimedOut: true);
        }

        return new AdbResult(p.ExitCode, string.Join('\n', stdout), string.Join('\n', stderr));
    }

    /// <summary>入队一条 ADB 命令（顺序执行）。high=true 时走优先通道（状态轮询专用）。</summary>
    public Task<AdbResult> AdbAsync(string? serial, string args, int timeoutMs = 30000,
        bool high = false, CancellationToken ct = default)
    {
        StartWorker();
        var tcs = new TaskCompletionSource<AdbResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        AdbJob job = new(serial, args, timeoutMs, tcs, high, ct);
        if (high) _highQueue.Writer.TryWrite(job);
        else _normalQueue.Writer.TryWrite(job);
        return tcs.Task;
    }

    /// <summary>列出 ADB 在线设备（serial \t state）。</summary>
    public async Task<List<(string Serial, string State)>> DevicesAsync()
    {
        AdbResult r = await AdbAsync(null, "devices", timeoutMs: 10000);
        var list = new List<(string, string)>();
        foreach (string line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            string[] parts = line.Trim().Split('\t');
            if (parts.Length >= 2) list.Add((parts[0].Trim(), parts[1].Trim()));
        }
        return list;
    }

    // ================================================================
    //  AVD 生命周期
    // ================================================================

    /// <summary>从 AVD 主目录枚举全部虚拟机，恢复为离线卡片列表。</summary>
    public void LoadVms()
    {
        Vms.Clear();
        foreach (string name in AvdWriter.ListAvds(_env.AvdHome))
        {
            DeviceProfile? profile = AvdWriter.ReadProfile(_env.AvdHome, name);
            if (profile is null)
            {
                EventBus.Warn("AVD", $"跳过损坏的 AVD: {name}");
                continue;
            }
            Vms.Add(new VmInstance { AvdName = name, Profile = profile });
        }
        EventBus.Info("AVD", $"已加载 {Vms.Count} 个虚拟机实例");
    }

    /// <summary>创建新虚拟机（纯手写 AVD 文件，不依赖 avdmanager / Java）。</summary>
    public VmInstance CreateAvd(string avdName, DeviceProfile profile)
    {
        avdName = avdName.Trim();
        if (!DeviceProfile.IsValidAvdName(avdName))
            throw new ArgumentException("AVD 名称只允许字母、数字、点、下划线、连字符（≤64 字符）");
        if (AvdWriter.ListAvds(_env.AvdHome).Contains(avdName))
            throw new InvalidOperationException($"虚拟机 {avdName} 已存在");
        if (!_env.IsValid)
            throw new InvalidOperationException("RuntimeSdk 不完整（缺系统镜像/引擎），无法创建虚拟机");

        AvdWriter.WriteAvd(_env.AvdHome, avdName, profile);
        var vm = new VmInstance { AvdName = avdName, Profile = profile };
        Vms.Add(vm);
        EventBus.Info("AVD", $"创建虚拟机成功: {avdName}");
        return vm;
    }

    /// <summary>删除虚拟机（拒绝删除运行中的实例）。</summary>
    public void DeleteAvd(VmInstance vm)
    {
        if (vm.IsRunning) throw new InvalidOperationException("请先停止该虚拟机再删除");
        string dir = AvdWriter.AvdDir(_env.AvdHome, vm.AvdName);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        string ini = Path.Combine(_env.AvdHome, $"{vm.AvdName}.ini");
        if (File.Exists(ini)) File.Delete(ini);
        Vms.Remove(vm);
        EventBus.Warn("AVD", $"已删除虚拟机: {vm.AvdName}");
    }

    /// <summary>把 config.ini 重写为新的 profile（要求虚拟机处于停止状态）。</summary>
    public void UpdateAvdConfig(VmInstance vm, DeviceProfile newProfile)
    {
        if (vm.IsRunning) throw new InvalidOperationException("虚拟机运行中，配置将在下次冷启动时生效——请先停止再修改");
        AvdWriter.WriteAvd(_env.AvdHome, vm.AvdName, newProfile);
        vm.Profile = newProfile;
    }

    /// <summary>附加已在运行的外部模拟器实例（上次会话遗留 / 手动启动的）。</summary>
    public async Task AttachExternalVmsAsync()
    {
        if (!_env.CoreBinariesOk) return;
        try
        {
            List<(string Serial, string State)> devices = await DevicesAsync();
            foreach ((string serial, string state) in devices.Where(d => d.Serial.StartsWith("emulator-")))
            {
                VmInstance? vm = Vms.FirstOrDefault(v => serial == $"emulator-{v.AdbPort - 1}");
                if (vm is null)
                {
                    // 尝试用内核参数找出 AVD 名
                    AdbResult name = await AdbAsync(serial, "shell getprop ro.kernel.qemu.avd_name", timeoutMs: 8000);
                    string avdName = name.StdOut.Trim();
                    if (avdName.Length == 0)
                    {
                        AdbResult emuName = await AdbAsync(serial, $"emu avd name", timeoutMs: 8000);
                        string first = emuName.StdOut.Split('\n').FirstOrDefault() ?? "";
                        avdName = first.StartsWith("name:") ? first[5..].Trim() : "";
                    }
                    if (avdName.Length == 0) continue;
                    vm = Vms.FirstOrDefault(v => string.Equals(v.AvdName, avdName, StringComparison.OrdinalIgnoreCase));
                }
                if (vm is null || vm.IsRunning) continue;

                vm.Serial = serial;
                vm.AdbPort = int.Parse(serial["emulator-".Length..]) + 1;
                vm.State = state == "device" ? VmState.Online : VmState.Error;
                AdbResult uid = await AdbAsync(serial, "shell id -u", timeoutMs: 8000);
                vm.Rooted = uid.Ok && uid.StdOut.Trim() == "0";
                EventBus.Info("ORCH", $"已附加外部实例 {vm.AvdName} ({serial})");
            }
        }
        catch (Exception ex)
        {
            EventBus.Warn("ORCH", $"附加外部实例失败: {ex.Message}");
        }
    }

    // ================================================================
    //  启动 / 关闭 / Root
    // ================================================================

    private int AllocatePort()
    {
        lock (_portLock)
        {
            for (int port = 5554; port <= 5594; port += 2)
            {
                if (_reservedPorts.Contains(port)) continue;
                if (!CanBind(port) || !CanBind(port + 1)) continue;
                _reservedPorts.Add(port);
                return port;
            }
        }
        throw new InvalidOperationException("无可用模拟器端口（5554-5594 已被占满，最多约 21 开）");
    }

    private static bool CanBind(int port)
    {
        try
        {
            using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch { return false; }
    }

    /// <summary>启动一台虚拟机（同步拉起进程，后台等待引导完成）。</summary>
    public async Task StartVmAsync(VmInstance vm, NetworkManager net, CancellationToken ct = default)
    {
        if (vm.IsRunning) { EventBus.Warn("ORCH", $"{vm.AvdName} 已在运行，忽略重复启动"); return; }
        if (!_env.CoreBinariesOk)
            throw new InvalidOperationException("RuntimeSdk 不完整，请重新安装 AndboxOne（emulator.exe / adb.exe 缺失）");

        int consolePort = AllocatePort();
        try
        {
            string args =
                $"-avd \"{vm.AvdName}\" -port {consolePort} -writable-system " +
                $"{net.BuildLaunchArgs()} -gpu {vm.Profile.GpuMode}" +
                (vm.Profile.NoSnapshot ? " -no-snapshot" : "") +
                (vm.Profile.Headless ? " -no-window" : "");

            EventBus.Info("ORCH", $"启动虚拟机 {vm.AvdName}: emulator {args}");
            var psi = new ProcessStartInfo
            {
                FileName = _env.EmulatorPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _env.EmulatorDir,
                // 环境变量继承本进程（EnvironmentManager 已设置 ANDROID_HOME 等）
            };

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e?.Data is not null) EventBus.Debug("EMU", $"[{vm.AvdName}] {e.Data}"); };
            p.ErrorDataReceived += (_, e) => { if (e?.Data is not null) EventBus.Debug("EMU", $"[{vm.AvdName}] {e.Data}"); };

            vm.EmuProcess = null;
            vm.Serial = $"emulator-{consolePort}";
            vm.AdbPort = consolePort + 1;
            vm.Rooted = false;
            vm.State = VmState.Starting;

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            vm.EmuProcess = p;

            p.Exited += (_, _) => OnEmulatorExited(vm, p);

            // 窗口盯守：emulator 常按历史坐标放窗口，把标题栏丢到屏幕外。
            // 后台等待窗口出现，越界即自动归位（层叠偏移），详见 WindowPlacer。
            StartWindowPlacer(vm);

            vm.State = VmState.Booting;
            EventBus.Info("ORCH", $"[{vm.AvdName}] 进程已拉起 (控制台端口 {consolePort})，等待 Android 引导…");

            bool booted = await WaitBootAsync(vm.Serial, timeoutSec: 240, ct: ct);
            if (booted)
            {
                vm.State = VmState.Online;
                EventBus.Info("ORCH", $"[{vm.AvdName}] 引导完成 → 在线 (serial={vm.Serial})");
            }
            else if (!vm.IsRunning)
            {
                // Exited 事件已处理状态
            }
            else
            {
                vm.State = VmState.Error;
                EventBus.Error("ORCH", $"[{vm.AvdName}] 引导超时(240s)，进程仍在运行——可点击【关闭】终止，或稍后重试 Root");
            }
        }
        catch (Exception)
        {
            vm.State = VmState.Error;
            lock (_portLock) { _reservedPorts.Remove(consolePort); }
            throw;
        }
    }

    private void OnEmulatorExited(VmInstance vm, Process p)
    {
        lock (_portLock) { _reservedPorts.Remove(vm.AdbPort - 1); }
        int code = -1;
        try { if (p.HasExited) code = p.ExitCode; } catch { /* 句柄失效 */ }

        bool graceful = vm.State == VmState.Stopping;
        vm.EmuProcess = null;
        vm.Serial = string.Empty;
        vm.Rooted = false;
        vm.CpuPercent = 0;
        vm.MemMb = 0;
        vm.State = VmState.Stopped;

        if (graceful)
            EventBus.Info("ORCH", $"[{vm.AvdName}] 已正常关闭 (exit={code})");
        else
            EventBus.Warn("ORCH", $"[{vm.AvdName}] 模拟器进程退出 (exit={code})——若为崩溃请查看上方 EMU 日志");
    }

    /// <summary>
    /// 后台盯守模拟器窗口（最长 90 秒，500ms 轮询）：出现后检测是否越出屏幕，
    /// 越界则归位到层叠位置。不阻塞启动流程，日志说明动作。
    /// </summary>
    private void StartWindowPlacer(VmInstance vm)
    {
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 180; i++)
            {
                if (_cts.IsCancellationRequested) return;
                if (vm.EmuProcess is null || vm.EmuProcess.HasExited) return;

                IntPtr? hwnd = WindowPlacer.FindEmulatorWindow(vm.AvdName);
                if (hwnd is not null)
                {
                    int index = Math.Max(0, Vms.Count(v => v.IsRunning) - 1);
                    bool moved = WindowPlacer.EnsureOnScreen(hwnd.Value, index);
                    if (moved)
                        EventBus.Info("ORCH", $"[{vm.AvdName}] 模拟器窗口越出屏幕，已自动归位（层叠偏移 ×{index}）");
                    else
                        EventBus.Debug("ORCH", $"[{vm.AvdName}] 模拟器窗口位置正常，未做调整");
                    return;
                }
                await Task.Delay(500);
            }
            EventBus.Debug("ORCH", $"[{vm.AvdName}] 90s 内未捕获模拟器窗口（可能为无窗口模式），跳过归位");
        });
    }

    /// <summary>「窗口归位」：把所有运行中实例的模拟器窗口强制拉回屏幕内并层叠排布。</summary>
    public void RepositionAllWindows()
    {
        int index = 0;
        foreach (VmInstance vm in Vms.Where(v => v.IsRunning))
        {
            IntPtr? hwnd = WindowPlacer.FindEmulatorWindow(vm.AvdName);
            if (hwnd is null)
            {
                EventBus.Debug("ORCH", $"[{vm.AvdName}] 未找到窗口（可能最小化或无窗口模式）");
                continue;
            }
            WindowPlacer.RestoreIfMinimized(hwnd.Value);
            bool moved = WindowPlacer.EnsureOnScreen(hwnd.Value, index++, force: true);
            EventBus.Info("ORCH", $"[{vm.AvdName}] {(moved ? "窗口已归位" : "窗口已在屏幕内（强制刷新）")}");
        }
    }

    /// <summary>轮询 sys.boot_completed 直到引导完成。</summary>
    public async Task<bool> WaitBootAsync(string serial, int timeoutSec = 240, CancellationToken ct = default)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline && !_cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            AdbResult r = await AdbAsync(serial, "shell getprop sys.boot_completed", timeoutMs: 8000, high: true, ct: ct);
            if (r.Ok && r.StdOut.Trim() == "1") return true;
            await Task.Delay(2000, CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token).Token)
                .ContinueWith(_ => { });
        }
        return false;
    }

    /// <summary>优雅关闭：emu kill → 兜底整树强杀。</summary>
    public async Task StopVmAsync(VmInstance vm)
    {
        if (!vm.IsRunning) return;
        vm.State = VmState.Stopping;
        EventBus.Info("ORCH", $"关闭虚拟机 {vm.AvdName} ({vm.Serial})…");

        if (vm.Serial.Length > 0)
        {
            AdbResult kill = await AdbAsync(vm.Serial, "emu kill", timeoutMs: 8000);
            if (!kill.Ok) EventBus.Warn("ORCH", $"[{vm.AvdName}] emu kill 失败（{kill.StdErr.Trim()}），将强制结束进程");
        }

        Process? p = vm.EmuProcess;
        if (p is not null)
        {
            for (int i = 0; i < 10 && !p.HasExited; i++) await p.WaitForExitAsync(new CancellationTokenSource(1000).Token)
                .ContinueWith(_ => { });
            if (!p.HasExited)
            {
                EventBus.Warn("ORCH", $"[{vm.AvdName}] 优雅关闭超时，强制结束进程树");
                try { p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            }
        }
        // Exited 事件负责最终状态切换
    }

    /// <summary>一键 Root：adb root → 等待 adbd 重启 → 验证 uid==0 → remount。</summary>
    public async Task<bool> RootVmAsync(VmInstance vm)
    {
        if (vm.State != VmState.Online)
        {
            EventBus.Warn("ORCH", $"[{vm.AvdName}] 尚未在线，无法 Root");
            return false;
        }
        EventBus.Info("ORCH", $"[{vm.AvdName}] 执行 adb root（google_apis 镜像支持）…");
        AdbResult r = await AdbAsync(vm.Serial, "root", timeoutMs: 15000);
        EventBus.Info("ADB", $"[{vm.AvdName}] root → {r.StdOut.Trim()} {r.StdErr.Trim()}");

        await Task.Delay(1500); // adbd 重启窗口

        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && vm.IsRunning)
        {
            AdbResult uid = await AdbAsync(vm.Serial, "shell id -u", timeoutMs: 8000, high: true);
            if (uid.Ok && uid.StdOut.Trim() == "0")
            {
                vm.Rooted = true;
                EventBus.Info("ORCH", $"[{vm.AvdName}] Root 完成：adbd 已以 uid=0 运行");
                AdbResult remount = await AdbAsync(vm.Serial, "remount", timeoutMs: 30000);
                if (remount.Ok)
                    EventBus.Info("ORCH", $"[{vm.AvdName}] remount 成功（-writable-system 生效，系统分区可写）");
                return true;
            }
            await Task.Delay(1000);
        }
        EventBus.Error("ORCH", $"[{vm.AvdName}] Root 失败：镜像可能不支持（须为 google_apis 而非 playstore）");
        return false;
    }

    // ================================================================
    //  批量操作（选中多个虚拟机，一键全部启动 / 全部关闭 / 全部 Root）
    // ================================================================

    public Task StartManyAsync(IEnumerable<VmInstance> vms, NetworkManager net, CancellationToken ct = default) =>
        Task.WhenAll(vms.Select(v => SafeStart(v, net, ct)));

    public Task StopManyAsync(IEnumerable<VmInstance> vms) =>
        Task.WhenAll(vms.Select(async v => { try { await StopVmAsync(v); } catch (Exception ex) { EventBus.Error("ORCH", $"[{v.AvdName}] 关闭失败: {ex.Message}"); } }));

    public Task RootManyAsync(IEnumerable<VmInstance> vms) =>
        Task.WhenAll(vms.Select(async v => { try { await RootVmAsync(v); } catch (Exception ex) { EventBus.Error("ORCH", $"[{v.AvdName}] Root 失败: {ex.Message}"); } }));

    private async Task SafeStart(VmInstance v, NetworkManager net, CancellationToken ct)
    {
        try { await StartVmAsync(v, net, ct); }
        catch (Exception ex) { EventBus.Error("ORCH", $"[{v.AvdName}] 启动失败: {ex.Message}"); }
    }

    // ================================================================
    //  资源采样（多开管理器的实时 CPU / 内存）
    // ================================================================

    public void StartSampling() => _sampler.Start();

    private void SampleAll()
    {
        foreach (VmInstance vm in Vms.Where(v => v.IsManaged && v.IsRunning))
            vm.Sample();
    }

    // ================================================================
    //  APK 安装流水线
    // ================================================================

    /// <summary>
    /// 安装 APK 并产出报告数据：
    ///   adb install -r → 包名兜底解析（pm list 差集）→ dumpsys package 提取版本/权限 → du 统计占用。
    /// </summary>
    public async Task<ApkInstallInfo?> InstallApkAsync(VmInstance vm, string apkPath, CancellationToken ct = default)
    {
        if (vm.State != VmState.Online)
        {
            EventBus.Error("INSTALL", $"[{vm.AvdName}] 虚拟机不在线，无法安装 APK");
            return null;
        }
        if (!File.Exists(apkPath))
        {
            EventBus.Error("INSTALL", $"APK 不存在: {apkPath}");
            return null;
        }

        EventBus.Info("INSTALL", $"[{vm.AvdName}] 安装 APK: {Path.GetFileName(apkPath)} ({new FileInfo(apkPath).Length / 1024.0 / 1024.0:0.#} MB)");

        // 1) 离线解析 APK（内置 AXML 引擎，可选 aapt 加持）
        ApkInfo? info = ApkInspector.Inspect(apkPath);
        if (info is not null)
            EventBus.Info("INSTALL", $"[{vm.AvdName}] AXML 解析: package={info.Package} v{info.VersionName}({info.VersionCode})");

        // 2) pm list 差集兜底：安装前快照
        HashSet<string> before = await ListPackagesAsync(vm.Serial);

        // 3) 真正安装（大 APK 可能耗时数分钟）
        AdbResult inst = await AdbAsync(vm.Serial, $"install -r \"{apkPath}\"", timeoutMs: 600000, ct: ct);
        string tail = (inst.StdOut + inst.StdErr).Trim();
        if (!inst.Ok || !tail.Contains("Success"))
        {
            EventBus.Error("INSTALL", $"[{vm.AvdName}] 安装失败: {tail}");
            return null;
        }
        EventBus.Info("INSTALL", $"[{vm.AvdName}] adb install → Success");

        // 4) 包名兜底：安装后差集
        string? pkg = info?.Package;
        if (string.IsNullOrEmpty(pkg))
        {
            HashSet<string> after = await ListPackagesAsync(vm.Serial);
            pkg = after.Except(before).FirstOrDefault();
            if (pkg is not null) EventBus.Info("INSTALL", $"[{vm.AvdName}] 通过 pm list 差集锁定包名: {pkg}");
        }
        if (string.IsNullOrEmpty(pkg))
        {
            EventBus.Error("INSTALL", $"[{vm.AvdName}] 无法确定包名（AXML 解析失败且差集为空——可能是覆盖安装）");
            return null;
        }

        // 5) dumpsys package：版本 / 路径 / 权限
        var result = new ApkInstallInfo
        {
            Package = pkg,
            VersionName = info?.VersionName ?? "",
            VersionCode = info?.VersionCode ?? 0,
            Label = info?.Label ?? "",
            LauncherActivity = info?.LauncherActivity ?? "",
            IconPng = info?.IconPng,
            ApkPath = apkPath,
            ApkSizeBytes = new FileInfo(apkPath).Length,
            InstallTime = DateTime.Now,
            TargetDevice = $"{vm.AvdName} ({vm.Serial})",
            Permissions = info?.Permissions ?? new List<string>(),
        };

        AdbResult dump = await AdbAsync(vm.Serial, $"shell dumpsys package {pkg}", timeoutMs: 60000, ct: ct);
        ParseDumpsys(dump.StdOut, result);

        // 6) 安装后占用空间：du -sk 应用目录
        if (result.CodePath.Length > 0)
        {
            string dir = Path.GetDirectoryName(result.CodePath.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/') ?? "";
            if (dir.Length > 0)
            {
                AdbResult du = await AdbAsync(vm.Serial, $"shell du -sk {dir}", timeoutMs: 30000, ct: ct);
                Match m = Regex.Match(du.StdOut, @"^(\d+)\s");
                if (m.Success) result.InstalledSizeBytes = long.Parse(m.Groups[1].Value) * 1024;
            }
        }

        EventBus.Info("INSTALL", $"[{vm.AvdName}] 报告数据就绪: {result.Package} v{result.VersionName} · 权限 {result.Permissions.Count} 项 · 占用 {result.InstalledSizeBytes / 1024.0 / 1024.0:0.#} MB");
        return result;
    }

    private async Task<HashSet<string>> ListPackagesAsync(string serial)
    {
        AdbResult r = await AdbAsync(serial, "shell pm list packages", timeoutMs: 30000);
        return r.StdOut.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("package:", StringComparison.Ordinal))
            .Select(l => l["package:".Length..])
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void ParseDumpsys(string dump, ApkInstallInfo info)
    {
        bool inPerms = false;
        foreach (string raw in dump.Split('\n'))
        {
            string line = raw.TrimEnd();
            string t = line.Trim();

            if (t.StartsWith("versionName=", StringComparison.Ordinal))
                info.VersionName = t["versionName=".Length..].Trim();
            else if (t.StartsWith("versionCode=", StringComparison.Ordinal))
            {
                Match m = Regex.Match(t, @"versionCode=(\d+)");
                if (m.Success && info.VersionCode == 0) info.VersionCode = int.Parse(m.Groups[1].Value);
            }
            else if (t.StartsWith("codePath=", StringComparison.Ordinal))
                info.CodePath = t["codePath=".Length..].Trim();
            else if (t.StartsWith("timeStamp=", StringComparison.Ordinal) && info.FirstInstallTime is null)
                info.FirstInstallTime = t["timeStamp=".Length..].Trim();
            else if (t.StartsWith("requested permissions:", StringComparison.Ordinal))
                inPerms = true;
            else if (inPerms)
            {
                if (t.StartsWith("android.permission.", StringComparison.Ordinal) ||
                    t.StartsWith("com.android.", StringComparison.Ordinal) ||
                    (t.EndsWith("PERMISSION", StringComparison.Ordinal) && t.Contains('.')))
                {
                    info.Permissions.Add(t);
                }
                else if (t.Length == 0 || t.Contains("install permissions:") || !t.Contains('.'))
                    inPerms = false;
            }
        }
        info.Permissions = info.Permissions.Distinct().OrderBy(x => x).ToList();
    }

    public void Dispose()
    {
        _sampler.Stop();
        _sampler.Dispose();
        _cts.Cancel();
        try { _highQueue.Writer.Complete(); _normalQueue.Writer.Complete(); } catch { }
    }
}
