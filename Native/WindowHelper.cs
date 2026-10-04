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
    /// "hwnd:số" là đúng cửa sổ đã nhớ (bước "Thu nhỏ cửa sổ" lưu vào biến) — kể cả khi đang thu nhỏ hay đổi tiêu đề.
    /// </summary>
    public static IntPtr Find(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return IntPtr.Zero;
        var q = query.Trim();
        if (q.StartsWith(HandlePrefix, StringComparison.OrdinalIgnoreCase))
            return long.TryParse(q[HandlePrefix.Length..].Trim(), out long n) && Win32.IsWindow((IntPtr)n) ? (IntPtr)n : IntPtr.Zero;
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
        if (Win32.IsIconic(h))
        {
            // Cửa sổ của luồng khác: không chờ ứng dụng xử lý (ứng dụng đang treo / bận không làm flow kẹt) — chỉ đợi ngắn cho cửa sổ hiện lại.
            if (Win32.GetWindowThreadProcessId(h, out _) == Win32.GetCurrentThreadId()) Win32.ShowWindow(h, Win32.SW_RESTORE);
            else
            {
                Win32.ShowWindowAsync(h, Win32.SW_RESTORE);
                for (int i = 0; i < 40 && Win32.IsIconic(h) && Win32.IsWindow(h); i++) Thread.Sleep(25);
            }
        }
        // Ứng dụng đang bị hộp thoại khóa (modal) → đưa hộp thoại lên, như khi bấm vào ứng dụng trên thanh tác vụ.
        if (!Win32.IsWindowEnabled(h))
        {
            var popup = Win32.GetLastActivePopup(h);
            if (popup != IntPtr.Zero && popup != h && Win32.IsWindowVisible(popup) && Win32.IsWindowEnabled(popup)) h = popup;
        }
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

    /// <summary>
    /// Phần của <paramref name="area"/> bị các cửa sổ nằm trên <paramref name="root"/> (cửa sổ được click tại <paramref name="p"/>) che:
    /// tooltip, menu cha, thanh ghi thao tác của ScheduleApp, cửa sổ khác chồng lên… Các vùng này lúc chạy lại thường không còn.
    /// Cửa sổ trong suốt phủ cả điểm click (lớp phủ màn hình, click xuyên qua được) không tính.
    /// </summary>
    public static List<Rectangle> CoveringRects(IntPtr root, Point p, Rectangle area)
    {
        var result = new List<Rectangle>();
        Win32.EnumWindows((h, _) =>
        {
            // EnumWindows đi từ trên xuống theo thứ tự chồng — tới cửa sổ được click thì dừng.
            if (h == root) return false;
            if (!Win32.IsWindowVisible(h) || IsCloaked(h)) return true;
            var r = GetRect(h);
            if (r.Width > 0 && r.Height > 0 && r.IntersectsWith(area) && !r.Contains(p)) result.Add(Rectangle.Intersect(r, area));
            return true;
        }, IntPtr.Zero);
        return result;
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

    /// <summary>Tiền tố của cửa sổ đã nhớ theo handle (vd "hwnd:132456").</summary>
    public const string HandlePrefix = "hwnd:";

    public static string HandleRef(IntPtr h) => HandlePrefix + h.ToInt64();

    /// <summary>
    /// Cửa sổ người dùng đang làm việc: cửa sổ đang được chọn — kể cả cửa sổ không có thanh tiêu đề như trình chiếu PowerPoint,
    /// video toàn màn hình — hoặc (khi đó là ScheduleApp / màn hình nền / menu) cửa sổ ứng dụng trên cùng chưa thu nhỏ.
    /// Không có thì trả về <see cref="IntPtr.Zero"/>.
    /// </summary>
    public static IntPtr ActiveUserWindow() =>
        ActiveUserWindowOverride.Value is { } fake ? fake() : ChooseUserWindow(GetOpenWindows(), Win32.GetForegroundWindow());

    /// <summary>Chỉ dùng cho kiểm thử: thay kết quả của <see cref="ActiveUserWindow"/> trong luồng async hiện tại (vd máy không có cửa sổ nào).</summary>
    internal static readonly AsyncLocal<Func<IntPtr>?> ActiveUserWindowOverride = new();

    /// <summary>
    /// Chọn cửa sổ người dùng đang làm việc trong <paramref name="open"/> (thứ tự trên → dưới, như <see cref="GetOpenWindows"/>):
    /// cửa sổ <paramref name="foreground"/> nếu có trong danh sách, không thì cửa sổ chính trên cùng — bỏ cửa sổ đã thu nhỏ và màn hình nền.
    /// Hộp thoại có thanh tiêu đề → cửa sổ ứng dụng chủ của nó (<see cref="AppWindowOf"/>).
    /// </summary>
    internal static IntPtr ChooseUserWindow(IReadOnlyList<WindowInfo> open, IntPtr foreground)
    {
        var windows = open.Where(w => !Win32.IsIconic(w.Handle) && !IsShell(w)).ToList();
        var h = windows.FirstOrDefault(w => w.Handle == foreground && GetClassName(w.Handle) != "#32768")?.Handle
                ?? windows.FirstOrDefault(w => !w.Popup)?.Handle ?? IntPtr.Zero;
        return h == IntPtr.Zero ? h : AppWindowOf(h);
    }

    /// <summary>
    /// Hộp thoại có thanh tiêu đề thuộc cửa sổ khác (vd "Format Cells" của Excel, "Save As" của Word) → cửa sổ ứng dụng chủ:
    /// thu nhỏ cửa sổ chủ thì hộp thoại ẩn / hiện theo, còn chỉ thu nhỏ hộp thoại sẽ để lại ứng dụng bị khóa trên màn hình.
    /// Cửa sổ không có thanh tiêu đề (trình chiếu PowerPoint, video toàn màn hình) và cửa sổ không có chủ giữ nguyên.
    /// </summary>
    internal static IntPtr AppWindowOf(IntPtr h)
    {
        if (IsPopup(h) || Win32.GetWindow(h, Win32.GW_OWNER) == IntPtr.Zero) return h;
        var owner = OwnerWindow(h);
        return owner != IntPtr.Zero && !Win32.IsIconic(owner) && !BelongsToThisApp(owner) ? owner : h;
    }

    /// <summary>Màn hình nền / thanh tác vụ — không phải cửa sổ ứng dụng.</summary>
    private static bool IsShell(WindowInfo w) =>
        GetClassName(w.Handle) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    /// <summary>
    /// Thu nhỏ cửa sổ bằng ShowWindowAsync — không chờ ứng dụng đó xử lý, nên ứng dụng đang treo / bận không làm flow kẹt —
    /// rồi đợi tối đa <paramref name="timeoutMs"/> cho cửa sổ thu nhỏ xong (dừng flow được trong lúc đợi).
    /// Trả về false nếu hết giờ mà cửa sổ chưa thu nhỏ (ứng dụng không phản hồi) hoặc cửa sổ đã đóng.
    /// </summary>
    public static async Task<bool> MinimizeAsync(IntPtr h, CancellationToken ct, int timeoutMs = 2000)
    {
        if (Win32.IsIconic(h)) return true;
        Win32.ShowWindowAsync(h, Win32.SW_MINIMIZE);
        var sw = Stopwatch.StartNew();
        while (!Win32.IsIconic(h))
        {
            if (!Win32.IsWindow(h) || sw.ElapsedMilliseconds >= timeoutMs) return false;
            await Task.Delay(50, ct);
        }
        return true;
    }

    public static bool BelongsToThisApp(IntPtr h)
    {
        Win32.GetWindowThreadProcessId(h, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }
}
