using System.Collections.Concurrent;
using System.IO;

namespace AndboxOne.Core;

/// <summary>日志级别。</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// 全局事件总线（EventBus）—— AndboxOne 的实时日志与状态中枢。
///
/// 设计约束（核心哲学之一）：
///   所有模块（编排引擎、网络调度、ADB 队列、安装报告、UI）的日志
///   统一通过 Action&lt;string, LogLevel&gt; 发布，UI 层订阅事件总线实时渲染。
///   日志精确到毫秒，格式固定为：
///       [yyyy-MM-dd HH:mm:ss.fff] [模块名] [日志级别] 内容
///
///   发布方不感知订阅方，订阅方异常不会影响发布方（隔离 + 吞掉）。
///   全部内存操作，绝不落盘上报——离线工具不做任何遥测。
/// </summary>
public static class EventBus
{
    private static readonly object Gate = new();
    private static Action<string, LogLevel>? _logEmitted;

    /// <summary>日志事件。参数一：已格式化的整行日志；参数二：级别（供 UI 着色）。</summary>
    public static event Action<string, LogLevel> LogEmitted
    {
        add { lock (Gate) { _logEmitted += value; } }
        remove { lock (Gate) { _logEmitted -= value; } }
    }

    /// <summary>发布一条日志。时间戳在发布瞬间取本机时间，精确到毫秒。</summary>
    public static void Publish(string module, string message, LogLevel level = LogLevel.Info)
    {
        string line =
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{module.ToUpperInvariant()}] [{level.ToString().ToUpperInvariant()}] {message}";

        Action<string, LogLevel>? handlers;
        lock (Gate) { handlers = _logEmitted; }
        if (handlers is null) return;

        foreach (Action<string, LogLevel> handler in handlers.GetInvocationList().Cast<Action<string, LogLevel>>())
        {
            try { handler(line, level); }
            catch
            {
                // 订阅方（UI）异常不得拖垮后台编排线程——极客工具必须稳。
            }
        }
    }

    public static void Debug(string module, string message) => Publish(module, message, LogLevel.Debug);
    public static void Info(string module, string message) => Publish(module, message, LogLevel.Info);
    public static void Warn(string module, string message) => Publish(module, message, LogLevel.Warn);
    public static void Error(string module, string message) => Publish(module, message, LogLevel.Error);
}

/// <summary>
/// 日志文本文件写入器：可选地将总线日志镜像到 UserData\logs 下的滚动文件。
/// 仅供故障排查，绝不联网上传。
/// </summary>
public sealed class LogFileSink : IDisposable
{
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 4096);
    private readonly StreamWriter _writer;
    private readonly Thread _flushThread;

    public LogFileSink(string logFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
        _writer = new StreamWriter(logFilePath, append: true) { AutoFlush = false };
        _flushThread = new Thread(FlushLoop) { IsBackground = true, Name = "AndboxOne-LogSink" };
        _flushThread.Start();
        EventBus.LogEmitted += OnLog;
    }

    private void OnLog(string line, LogLevel _) => _queue.TryAdd(line);

    private void FlushLoop()
    {
        foreach (string line in _queue.GetConsumingEnumerable())
        {
            _writer.WriteLine(line);
            if (_queue.Count == 0) _writer.Flush();
        }
        _writer.Flush();
        _writer.Dispose();
    }

    public void Dispose()
    {
        EventBus.LogEmitted -= OnLog;
        _queue.CompleteAdding();
        _flushThread.Join(2000);
    }
}
