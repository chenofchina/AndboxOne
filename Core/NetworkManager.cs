using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AndboxOne.Core;

/// <summary>网络策略：NAT（默认）或桥接（独立 IP，需 TAP 适配器，实验性）。</summary>
public enum NetworkMode
{
    Nat,
    Bridge,
}

/// <summary>一次网络诊断的结果。</summary>
public sealed class NetworkDiagnosis
{
    public string Serial { get; init; } = "";
    public int Sent { get; init; }
    public int Received { get; init; }
    public double LossPercent { get; init; }
    public double AvgLatencyMs { get; init; }
    public bool Success => Received > 0;
    public string RawOutput { get; init; } = "";
    public string FallbackNote { get; init; } = "";
}

/// <summary>
/// NetworkManager —— 网络策略调度模块。
///
/// · NAT 模式（默认）：启动参数附加 -netdelay none -netspeed full，
///   虚拟机通过宿主机 NAT 上网，零配置、最稳。
/// · 桥接模式（实验性）：通过 -qemu 透传 tap 网卡参数实现独立 IP。
///   前提：宿主机已安装 OpenVPN TAP 适配器（如 TAP-Windows Adapter V9），
///   并将适配器名填入 BridgeAdapterName。模拟器官方未正式支持桥接，
///   故标记为实验性并在日志中明示。
/// · 网络诊断：adb shell ping -c 4 8.8.8.8，解析丢包率与平均延迟；
///   当 8.8.8.8 全丢时自动补测 114.114.114.114 以区分"墙"与"断网"。
/// </summary>
public sealed class NetworkManager
{
    /// <summary>桥接模式使用的 TAP 适配器名。</summary>
    public string BridgeAdapterName { get; set; } = "tap0";

    public NetworkMode Mode { get; set; } = NetworkMode.Nat;

    /// <summary>构建启动参数片段（供编排引擎拼接到 emulator.exe 命令行）。</summary>
    public string BuildLaunchArgs()
    {
        // NAT：全速、无延迟（规范要求的默认参数）
        string nat = "-netdelay none -netspeed full";
        if (Mode == NetworkMode.Nat) return nat;

        // 桥接：-qemu 之后的参数全部透传给底层 QEMU。
        // 使用 legacy 语法 -net tap,ifname=... 挂接宿主机 TAP 适配器。
        EventBus.Warn("NET", "桥接模式为实验特性：需要宿主机已安装 TAP 适配器，官方模拟器未正式支持");
        return $"{nat} -qemu -net nic,model=virtio-net-pci -net tap,ifname={BridgeAdapterName},script=no,downscript=no";
    }

    /// <summary>对指定设备执行 ping 诊断（走 ADB 命令队列）。</summary>
    public async Task<NetworkDiagnosis> DiagnoseAsync(EmulatorOrchestrator orch, string serial, CancellationToken ct = default)
    {
        EventBus.Info("NET", $"[{serial}] 开始网络诊断: ping -c 4 8.8.8.8");
        AdbResult ping = await orch.AdbAsync(serial, "shell ping -c 4 -W 2 8.8.8.8", timeoutMs: 20000, ct: ct);

        string output = ping.StdOut;
        if (string.IsNullOrWhiteSpace(output) && !string.IsNullOrWhiteSpace(ping.StdErr))
            output = ping.StdErr;

        NetworkDiagnosis d = Parse(serial, output);
        if (d.Success)
        {
            EventBus.Info("NET", $"[{serial}] 诊断通过: 收发 {d.Received}/{d.Sent} · 丢包 {d.LossPercent:0}% · 平均延迟 {d.AvgLatencyMs:0.#} ms");
            return d;
        }

        // 8.8.8.8 全丢：补测国内 DNS 区分"被墙"与"真断网"
        EventBus.Warn("NET", $"[{serial}] 8.8.8.8 不可达，补测 114.114.114.114 以区分断网原因…");
        AdbResult alt = await orch.AdbAsync(serial, "shell ping -c 4 -W 2 114.114.114.114", timeoutMs: 20000, ct: ct);
        NetworkDiagnosis d2 = Parse(serial, alt.StdOut);
        string note = d2.Success
            ? "（8.8.8.8 不可达但 114.114.114.114 可达：典型国际出口受限，非断网）"
            : "（两个目标均不可达：虚拟机网络断开，请检查模拟器网络/宿主机防火墙）";
        EventBus.Warn("NET", $"[{serial}] {note}");

        return new NetworkDiagnosis
        {
            Serial = serial,
            Sent = d.Sent, Received = d.Received, LossPercent = d.LossPercent,
            AvgLatencyMs = d.AvgLatencyMs,
            RawOutput = $"{output}\n---- 补测 114.114.114.114 ----\n{alt.StdOut}",
            FallbackNote = note,
        };
    }

    private static NetworkDiagnosis Parse(string serial, string output)
    {
        int sent = 0, received = 0;
        double loss = 100, avg = 0;

        Match m = Regex.Match(output, @"(\d+)\s+packets?\s+transmitted,\s+(\d+)\s+received,\s+([\d.]+)%\s+packet loss");
        if (m.Success)
        {
            sent = int.Parse(m.Groups[1].Value);
            received = int.Parse(m.Groups[2].Value);
            loss = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        Match rtt = Regex.Match(output, @"=\s*[\d.]+/([\d.]+)/[\d.]+/[\d.]+\s*ms");
        if (rtt.Success)
            avg = double.Parse(rtt.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        return new NetworkDiagnosis
        {
            Serial = serial,
            Sent = sent, Received = received, LossPercent = loss, AvgLatencyMs = avg,
            RawOutput = output,
        };
    }
}
