using System.Runtime.InteropServices;
using System.Text;

namespace AndboxOne.Core;

/// <summary>
/// WindowPlacer —— 模拟器窗口位置管理。
///
/// 解决的经典问题：Android Emulator 按"上次坐标/多开级联坐标"放置窗口，
/// 常常把标题栏丢到屏幕外（尤其顶部），用户拖无可拖。
///
/// 能力：
///   1. 按窗口标题（emulator 主窗口标题 = AVD 名）定位模拟器主窗口；
///   2. 检测窗口是否越出最近显示器的有效工作区（标题栏不可达 / 可见面积过小）；
///   3. 越界则归位到工作区中心 + 按实例序号层叠偏移（多开不互相完全遮盖）；
///   4. 全部 Win32 调用在线程内临时切换为 Per-Monitor DPI Aware，
///      保证高 DPI 缩放下坐标计算与物理像素一致，且不影响 WPF UI 的 DPI 语义。
/// </summary>
public static class WindowPlacer
{
    // ------------------------------------------------------------------
    //  Win32
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd); // GW_OWNER = 4
    [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags); // TONEAREST = 2
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index); // SM_CXSCREEN=0 / SM_CYSCREEN=1
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after,
        int x, int y, int cx, int cy, uint flags); // NOSIZE=0x1 NOZORDER=0x4 SHOWWINDOW=0x40
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd); // 最小化判断
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd); // SW_RESTORE = 9

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

    private static readonly IntPtr DPI_AWARE_PER_MONITOR_V2 = new(-4);

    /// <summary>线程级 DPI 感知切换包装：确保坐标按物理像素计算。</summary>
    private static T WithDpiAware<T>(Func<T> body)
    {
        IntPtr old = IntPtr.Zero;
        try
        {
            old = SetThreadDpiAwarenessContext(DPI_AWARE_PER_MONITOR_V2);
            return body();
        }
        finally
        {
            if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old);
        }
    }

    // ------------------------------------------------------------------
    //  对外 API
    // ------------------------------------------------------------------

    /// <summary>
    /// 查找模拟器主窗口：可见、无属主、标题包含 AVD 名的面积最大者。
    /// （Android Emulator 主窗口标题即 AVD 名称；Qt 引擎可能附带扩展控件窗口，取最大者为主窗体。）
    /// </summary>
    public static IntPtr? FindEmulatorWindow(string avdName) => WithDpiAware(() =>
    {
        IntPtr? best = null;
        long bestArea = -1;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || GetWindow(hWnd, 4) != IntPtr.Zero || IsIconic(hWnd))
                return true;

            int len = GetWindowTextLength(hWnd);
            if (len == 0) return true;

            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (!sb.ToString().Contains(avdName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!GetWindowRect(hWnd, out RECT r)) return true;
            long area = (long)Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = hWnd; }
            return true;
        }, IntPtr.Zero);

        return bestArea > 10000 ? best : null; // 过滤 100x100 以下的杂项小窗
    });

    /// <summary>
    /// 确保窗口在屏幕内：越界（标题栏不可达 / 可见面积过小）时移动到工作区中心 + 层叠偏移。
    /// force=true 时即使不越界也强制归位（「窗口归位」按钮）。
    /// 返回是否发生了移动。
    /// </summary>
    public static bool EnsureOnScreen(IntPtr hwnd, int cascadeIndex, bool force = false) => WithDpiAware(() =>
    {
        if (!GetWindowRect(hwnd, out RECT r)) return false;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return false;

        // 最近显示器的工作区；取不到时退回主屏
        RECT work = default;
        IntPtr mon = MonitorFromRect(ref r, 2);
        if (mon != IntPtr.Zero)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi)) work = mi.rcWork;
        }
        if (work.Right <= work.Left)
        {
            work.Left = 0; work.Top = 0;
            work.Right = GetSystemMetrics(0);
            work.Bottom = GetSystemMetrics(1);
        }

        int visW = Math.Min(r.Right, work.Right) - Math.Max(r.Left, work.Left);
        int visH = Math.Min(r.Bottom, work.Bottom) - Math.Max(r.Top, work.Top);
        bool offscreen = visW < 240 || visH < 240 || r.Top < work.Top;

        if (!offscreen && !force) return false;

        int x = work.Left + Math.Max(8, (work.Right - work.Left - w) / 2) + Math.Max(0, cascadeIndex) * 36;
        int y = work.Top + Math.Max(8, (work.Bottom - work.Top - h) / 2) + Math.Max(0, cascadeIndex) * 36;
        x = Math.Min(x, work.Right - Math.Min(w, 320));
        y = Math.Min(y, work.Bottom - Math.Min(h, 240));

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x1 | 0x4 | 0x40);
        return true;
    });

    /// <summary>窗口若被最小化则还原（归位前调用可救回最小化到任务栏找不着的实例）。</summary>
    public static void RestoreIfMinimized(IntPtr hwnd) => WithDpiAware(() =>
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE
        return true;
    });
}
