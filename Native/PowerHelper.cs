using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ScheduleApp.Native;

/// <summary>Giữ máy thức khi flow chạy, đánh thức máy khỏi chế độ ngủ, đo thời gian rảnh, DPI màn hình.</summary>
internal static class PowerHelper
{
    // ───────────────────────────── Giữ máy thức ─────────────────────────────

    /// <summary>Không cho máy ngủ / tắt màn hình cho tới khi Dispose (dùng power request, không gắn với luồng).</summary>
    public static IDisposable KeepAwake(string reason)
    {
        var context = new REASON_CONTEXT
        {
            Version = 0,
            Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
            SimpleReasonString = reason
        };
        var handle = PowerCreateRequest(ref context);
        if (handle.IsInvalid) return new Releaser(null);
        PowerSetRequest(handle, PowerRequestType.SystemRequired);
        PowerSetRequest(handle, PowerRequestType.DisplayRequired);
        return new Releaser(handle);
    }

    private sealed class Releaser(SafeFileHandle? handle) : IDisposable
    {
        public void Dispose()
        {
            if (handle == null || handle.IsClosed) return;
            PowerClearRequest(handle, PowerRequestType.SystemRequired);
            PowerClearRequest(handle, PowerRequestType.DisplayRequired);
            handle.Dispose();
        }
    }

    // ───────────────────────────── Đánh thức máy ─────────────────────────────

    private static readonly object WakeSync = new();
    private static SafeWaitHandle? _wakeTimer;

    /// <summary>
    /// Hẹn giờ đánh thức máy khỏi chế độ ngủ (Sleep) vào thời điểm <paramref name="at"/>; null = hủy.
    /// Cần bật "Allow wake timers" trong Power Options. Không đánh thức được khi máy tắt hẳn/ngủ đông sâu.
    /// </summary>
    public static bool SetWakeTimer(DateTime? at)
    {
        lock (WakeSync)
        {
            _wakeTimer ??= CreateWaitableTimer(IntPtr.Zero, true, "ScheduleApp_WakeTimer_7F3A1C");
            if (_wakeTimer.IsInvalid) return false;
            if (at is not DateTime t || t <= DateTime.Now)
                return CancelWaitableTimer(_wakeTimer);
            long due = t.ToFileTime(); // giá trị dương = thời điểm tuyệt đối (UTC FILETIME)
            return SetWaitableTimer(_wakeTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, true);
        }
    }

    /// <summary>Báo cho Windows là hệ thống đang được dùng (sau khi bị đánh thức, tránh ngủ lại ngay).</summary>
    public static void PokeSystem() => SetThreadExecutionState(ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);

    // ───────────────────────────── Rảnh / DPI ─────────────────────────────

    /// <summary>Thời gian từ lần cuối người dùng dùng chuột/bàn phím.</summary>
    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    /// <summary>Hệ số scale (DPI / 96) của màn hình chứa điểm <paramref name="p"/>.</summary>
    public static double ScaleAt(Point p)
    {
        try
        {
            var monitor = MonitorFromPoint(new Win32.POINT { X = p.X, Y = p.Y }, 2 /*MONITOR_DEFAULTTONEAREST*/);
            if (GetDpiForMonitor(monitor, 0 /*MDT_EFFECTIVE_DPI*/, out uint dpiX, out _) == 0 && dpiX > 0) return dpiX / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1.0;
    }

    // ───────────────────────────── P/Invoke ─────────────────────────────

    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;
    private const uint ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    private enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle PowerCreateRequest(ref REASON_CONTEXT context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerSetRequest(SafeFileHandle handle, PowerRequestType type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerClearRequest(SafeFileHandle handle, PowerRequestType type);
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimer(IntPtr attributes, bool manualReset, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Win32.POINT pt, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
