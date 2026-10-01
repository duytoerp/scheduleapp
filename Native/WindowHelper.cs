using System.Diagnostics;
using System.Text;

namespace ScheduleApp.Native;

/// <param name="Popup">Cửa sổ phụ (menu, danh sách gợi ý, thả xuống…) — khi tìm theo tên, cửa sổ chính được ưu tiên.</param>
internal sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName, bool Popup = false)
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
            if (IsCloaked(h)) return true;

            Win32.GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return true;

            var sb = new StringBuilder(len + 1);
            Win32.GetWindowText(h, sb, sb.Capacity);

            if (!processNames.TryGetValue(pid, out var name))
            {
                try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = "?"; }
                processNames[pid] = name;
            }
            list.Add(new WindowInfo(h, sb.ToString(), name, IsPopup(h)));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>
    /// Các cửa sổ đang hiển thị khác của cùng tiến trình với <paramref name="main"/> (kể cả không có tiêu đề) theo thứ tự trên → dưới:
    /// danh sách gợi ý của thanh địa chỉ, menu chuột phải, ô thả xuống… thường là cửa sổ riêng, không nằm trong cây của cửa sổ chính.
    /// </summary>
    public static List<IntPtr> ProcessPopups(IntPtr main)
    {
        var list = new List<IntPtr>();
        if (main == IntPtr.Zero) return list;
        Win32.GetWindowThreadProcessId(main, out uint pid);
        Win32.EnumWindows((h, _) =>
        {
            if (h == main || !Win32.IsWindowVisible(h) || IsCloaked(h)) return true;
            Win32.GetWindowThreadProcessId(h, out uint p);
            if (p != pid) return true;
            var r = GetRect(h);
            if (r.Width > 4 && r.Height > 4 && (IsPopup(h) || GetTitle(h).Length == 0)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static bool IsCloaked(IntPtr h) =>
        Win32.DwmGetWindowAttribute(h, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>Cửa sổ phụ: menu (#32768), hoặc cửa sổ popup / có chủ sở hữu mà không có thanh tiêu đề.</summary>
    public static bool IsPopup(IntPtr h)
    {
        if (GetClassName(h) == "#32768") return true;
        long style = (long)Win32.GetWindowLongPtr(h, Win32.GWL_STYLE);
        if ((style & Win32.WS_CAPTION) == Win32.WS_CAPTION) return false;
        return (style & Win32.WS_POPUP) != 0 || Win32.GetWindow(h, Win32.GW_OWNER) != IntPtr.Zero;
    }

    public static string GetClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        int n = Win32.GetClassName(h, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : "";
    }

    /// <summary>
    /// Cửa sổ chính sở hữu popup <paramref name="h"/>: đi theo chuỗi chủ sở hữu tới cửa sổ đầu tiên đang hiển thị, có tiêu đề, không phải popup
    /// (không dùng GA_ROOTOWNER vì nhiều ứng dụng có cửa sổ ẩn làm chủ sở hữu cao nhất). Không có → IntPtr.Zero.
    /// </summary>
    public static IntPtr OwnerWindow(IntPtr h)
    {
        int guard = 0;
        for (var o = Win32.GetWindow(h, Win32.GW_OWNER); o != IntPtr.Zero && guard++ < 16; o = Win32.GetWindow(o, Win32.GW_OWNER))
        {
            if (Win32.IsWindowVisible(o) && GetTitle(o).Length > 0 && !IsPopup(o)) return o;
        }
        return IntPtr.Zero;
    }

    public static bool SameProcess(IntPtr a, IntPtr b)
    {
        Win32.GetWindowThreadProcessId(a, out uint pa);
        Win32.GetWindowThreadProcessId(b, out uint pb);
        return pa == pb;
    }

    /// <summary>
    /// Kích hoạt cửa sổ, trừ khi nó (hoặc một popup của chính nó — menu, gợi ý…) đang ở trên cùng:
    /// chuyển focus lúc đó sẽ làm popup đóng lại trước khi kịp click.
    /// </summary>
    public static void FocusKeepingPopups(IntPtr h)
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == h) return;
        if (fg != IntPtr.Zero && (OwnerWindow(fg) == h || (IsPopup(fg) && SameProcess(fg, h)))) return;
        Focus(h);
    }

    /// <summary>
    /// Tìm cửa sổ theo một phần tiêu đề (không phân biệt hoa thường), nếu không có thì theo tên tiến trình.
    /// Tiền tố "exe:" (vd "exe:chrome") chỉ so theo tên tiến trình — ổn định khi tiêu đề thay đổi liên tục.
    /// </summary>
    public static IntPtr Find(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return IntPtr.Zero;
        var q = query.Trim();
        // Cửa sổ chính trước, popup sau (vẫn giữ thứ tự trên → dưới trong mỗi nhóm).
        var windows = GetOpenWindows().OrderBy(w => w.Popup).ToList();

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
