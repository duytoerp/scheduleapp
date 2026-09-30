using System.Diagnostics;
using System.Text;

namespace ScheduleApp.Native;

internal sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName)
{
    public override string ToString() => $"{Title}   [{ProcessName}]";
}

/// <summary>Tìm, chờ và kích hoạt cửa sổ của ứng dụng khác.</summary>
internal static class WindowHelper
{
    /// <summary>Các cửa sổ top-level đang hiển thị (không gồm cửa sổ của chính ScheduleApp).</summary>
    public static List<WindowInfo> GetOpenWindows()
    {
        var list = new List<WindowInfo>();
        var processNames = new Dictionary<uint, string>();
        uint self = (uint)Environment.ProcessId;

        Win32.EnumWindows((h, _) =>
        {
            if (!Win32.IsWindowVisible(h)) return true;
            int len = Win32.GetWindowTextLength(h);
            if (len == 0) return true;
            if (Win32.DwmGetWindowAttribute(h, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            Win32.GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;

            var sb = new StringBuilder(len + 1);
            Win32.GetWindowText(h, sb, sb.Capacity);

            if (!processNames.TryGetValue(pid, out var name))
            {
                try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = "?"; }
                processNames[pid] = name;
            }
            list.Add(new WindowInfo(h, sb.ToString(), name));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>
    /// Tìm cửa sổ theo một phần tiêu đề (không phân biệt hoa thường), nếu không có thì theo tên tiến trình.
    /// Tiền tố "exe:" (vd "exe:chrome") chỉ so theo tên tiến trình — ổn định khi tiêu đề thay đổi liên tục.
    /// </summary>
    public static IntPtr Find(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return IntPtr.Zero;
        var q = query.Trim();
        var windows = GetOpenWindows();

        if (q.StartsWith("exe:", StringComparison.OrdinalIgnoreCase))
        {
            var name = q[4..].Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            return windows.FirstOrDefault(w => w.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Handle ?? IntPtr.Zero;
        }

        var proc = q.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? q[..^4] : q;
        var match = windows.FirstOrDefault(w => w.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
                    ?? windows.FirstOrDefault(w => w.ProcessName.Equals(proc, StringComparison.OrdinalIgnoreCase));
        return match?.Handle ?? IntPtr.Zero;
    }

    public static async Task<IntPtr> WaitForAsync(string query, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new InvalidOperationException("Chưa nhập tiêu đề cửa sổ.");
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var h = Find(query);
            if (h != IntPtr.Zero) return h;
            if (sw.ElapsedMilliseconds >= timeoutMs)
                throw new TimeoutException($"Không tìm thấy cửa sổ \"{query}\" sau {timeoutMs / 1000.0:0.#} giây.");
            await Task.Delay(250, ct);
        }
    }

    public static void Focus(IntPtr h)
    {
        if (Win32.IsIconic(h)) Win32.ShowWindow(h, Win32.SW_RESTORE);
        if (Win32.GetForegroundWindow() == h) return;

        Win32.SetForegroundWindow(h);

        if (Win32.GetForegroundWindow() != h)
        {
            // Gắn tạm vào luồng input của cửa sổ đang foreground để được phép chuyển focus.
            var fg = Win32.GetForegroundWindow();
            uint fgThread = Win32.GetWindowThreadProcessId(fg, out _);
            uint me = Win32.GetCurrentThreadId();
            Win32.AttachThreadInput(me, fgThread, true);
            Win32.BringWindowToTop(h);
            Win32.SetForegroundWindow(h);
            Win32.AttachThreadInput(me, fgThread, false);
        }

        if (Win32.GetForegroundWindow() != h)
        {
            // Mẹo cuối: giả lập phím ALT để Windows cho phép SetForegroundWindow.
            Win32.keybd_event(0x12, 0, 0, UIntPtr.Zero);
            Win32.keybd_event(0x12, 0, 2, UIntPtr.Zero);
            Win32.SetForegroundWindow(h);
        }

        Thread.Sleep(150);
    }

    public static Rectangle GetRect(IntPtr h)
    {
        Win32.GetWindowRect(h, out var r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>Cửa sổ top-level nằm dưới một điểm trên màn hình.</summary>
    public static IntPtr RootWindowAt(Point p)
    {
        var h = Win32.WindowFromPoint(new Win32.POINT { X = p.X, Y = p.Y });
        return h == IntPtr.Zero ? IntPtr.Zero : Win32.GetAncestor(h, Win32.GA_ROOT);
    }

    public static string GetTitle(IntPtr h)
    {
        int len = Win32.GetWindowTextLength(h);
        if (len == 0) return "";
        var sb = new StringBuilder(len + 1);
        Win32.GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool BelongsToThisApp(IntPtr h)
    {
        Win32.GetWindowThreadProcessId(h, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }
}
