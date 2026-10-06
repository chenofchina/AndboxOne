using System.ComponentModel;
using System.Diagnostics;

namespace AndboxOne.Core;

/// <summary>虚拟机生命周期状态。</summary>
public enum VmState
{
    Stopped,   // 已停止
    Starting,  // emulator.exe 进程已拉起，QEMU 初始化中
    Booting,   // Android 系统引导中（等待 sys.boot_completed）
    Online,    // 系统就绪，ADB 可用
    Stopping,  // 正在优雅关闭
    Error,     // 启动失败 / 异常退出
}

/// <summary>
/// VmInstance —— 多开管理器中的一个虚拟机实例（左侧卡片的数据模型）。
/// 持有 DeviceProfile 与运行时状态；CPU / 内存占用由编排引擎的采样器定时回填，
/// 通过 PropertyChanged 实时刷新到 UI（WPF 跨线程属性通知自动调度）。
/// </summary>
public sealed class VmInstance : INotifyPropertyChanged
{
    private VmState _state = VmState.Stopped;
    private double _cpuPercent;
    private long _memMb;
    private bool _rooted;
    private bool _isChecked;
    private string _serial = string.Empty;
    private TimeSpan _lastCpuTime = TimeSpan.Zero;
    private DateTime _lastSampleUtc = DateTime.UtcNow;

    /// <summary>AVD 名称（全局唯一 ID）。</summary>
    public required string AvdName { get; init; }
    public required DeviceProfile Profile { get; set; }

    /// <summary>模拟器进程句柄（本应用拉起时非空；附加的外部实例为 null）。</summary>
    public Process? EmuProcess { get; set; }

    /// <summary>是否为本应用拉起的实例（可采样 CPU/内存）；外部附加实例为 false。</summary>
    public bool IsManaged => EmuProcess is not null && !EmuProcess.HasExited;

    /// <summary>ADB 序列号，形如 emulator-5554。Stopped 时为空。</summary>
    public string Serial { get => _serial; set { _serial = value; Raise(nameof(Serial)); Raise(nameof(IsRunning)); } }

    public int AdbPort { get; set; }

    public VmState State
    {
        get => _state;
        set
        {
            _state = value;
            Raise(nameof(State));
            Raise(nameof(StateText));
            Raise(nameof(IsRunning));
            Raise(nameof(IsBusy));
        }
    }

    /// <summary>是否已 Root（adbd 以 root 运行，可 remount / 修改系统分区）。</summary>
    public bool Rooted { get => _rooted; set { _rooted = value; Raise(nameof(Rooted)); Raise(nameof(StateText)); } }

    /// <summary>批量操作的选择状态（左侧卡片复选框）。</summary>
    public bool IsChecked { get => _isChecked; set { _isChecked = value; Raise(nameof(IsChecked)); } }

    /// <summary>CPU 占用百分比（0-100，对全体逻辑核心归一）。</summary>
    public double CpuPercent
    {
        get => _cpuPercent;
        set { _cpuPercent = value; Raise(nameof(CpuPercent)); Raise(nameof(CpuText)); }
    }

    /// <summary>物理内存占用（MB，工作集）。</summary>
    public long MemMb
    {
        get => _memMb;
        set { _memMb = value; Raise(nameof(MemMb)); Raise(nameof(MemText)); }
    }

    public bool IsRunning => State is VmState.Starting or VmState.Booting or VmState.Online or VmState.Stopping;
    public bool IsBusy => State is VmState.Starting or VmState.Booting or VmState.Stopping;

    public string StateText
    {
        get
        {
            string root = Rooted ? " · ROOT" : string.Empty;
            return State switch
            {
                VmState.Stopped => $"已停止{root}",
                VmState.Starting => "启动中…",
                VmState.Booting => $"系统引导中…{root}",
                VmState.Online => $"在线{root}",
                VmState.Stopping => "正在关闭…",
                VmState.Error => "异常",
                _ => "?",
            };
        }
    }

    public string CpuText => IsManaged && IsRunning ? $"{CpuPercent:0.0}%" : "—";
    public string MemText => IsManaged && IsRunning ? $"{MemMb} MB" : "—";

    /// <summary>资源采样：由编排引擎每 2 秒调用一次（工作线程）。</summary>
    public void Sample()
    {
        Process? p = EmuProcess;
        if (p is null || p.HasExited) return;

        try
        {
            p.Refresh();
            DateTime nowUtc = DateTime.UtcNow;
            TimeSpan cpuNow = p.TotalProcessorTime;

            double wallSec = (nowUtc - _lastSampleUtc).TotalSeconds;
            if (wallSec > 0.5 && _lastSampleUtc != default)
            {
                double cpuSec = (cpuNow - _lastCpuTime).TotalSeconds;
                CpuPercent = Math.Clamp(cpuSec / (wallSec * Environment.ProcessorCount) * 100.0, 0, 100);
            }
            _lastCpuTime = cpuNow;
            _lastSampleUtc = nowUtc;

            MemMb = p.WorkingSet64 / (1024 * 1024);
        }
        catch
        {
            // 进程恰好在采样瞬间退出——静默忽略，下一轮由 Exited 事件收尾。
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
