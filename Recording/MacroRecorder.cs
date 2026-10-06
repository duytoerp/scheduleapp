using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Vision;

namespace ScheduleApp.Recording;

/// <summary>
/// Ghi thao tác chuột/bàn phím toàn hệ thống (low-level hook) và chuyển thành các bước flow:
/// click → "Click phần tử UI" nếu nhận diện được, ngược lại "Click chuột" (tọa độ tương đối theo cửa sổ), kèm hình mẫu chụp quanh
/// điểm click để khi chạy tìm lại theo hình ảnh; chữ gõ liên tiếp → "Gõ văn bản", phím đặc biệt / tổ hợp → "Nhấn phím".
/// Phải chạy trên luồng UI (cần vòng lặp message).
/// </summary>
internal sealed class MacroRecorder : IDisposable
{
    private const uint LLKHF_INJECTED = 0x10;
    private const int VK_PACKET = 0xE7, VK_BACK = 0x08;
    private const int ImeWindowMs = 120;

    private readonly Win32.LowLevelProc _mouseProc;
    private readonly Win32.LowLevelProc _keyProc;
    private IntPtr _mouseHook, _keyHook;

    private readonly List<ActionStep> _steps = [];
    private readonly StringBuilder _typed = new();
    private readonly Dictionary<uint, string> _processNames = [];
    private string _typedTarget = "";
    private long _typedStart, _typedEnd, _lastTick;

    // Theo dõi phím vật lý vừa gõ để xử lý bộ gõ tiếng Việt (Unikey/EVKey chặn phím rồi tự gửi lại ký tự có dấu).
    private long _lastPhysicalCharTick = -1;
    private bool _physicalConsumed = true;

    public event Action? Changed;
    public event Action? StopRequested;

    public MacroRecorder()
    {
        _mouseProc = MouseHook;
        _keyProc = KeyHook;
    }

    public int StepCount => _steps.Count;
    public bool IsTyping => _typed.Length > 0;

    /// <summary>
    /// Click vào nút / ô nhập / mục menu có tên hoặc AutomationId → ghi thành "Click phần tử UI" (không phụ thuộc tọa độ, độ phân giải).
    /// Phần tử không nhận diện được vẫn ghi bằng tọa độ.
    /// </summary>
    public bool RecordElements { get; set; } = true;

    /// <summary>
    /// Chụp vùng quanh mỗi click làm hình mẫu: khi chạy tìm lại chỗ đó theo hình ảnh (cửa sổ di chuyển, đổi kích thước,
    /// bố cục xê dịch vẫn click đúng), không thấy mới click theo tọa độ lúc ghi.
    /// </summary>
    public bool RecordImages { get; set; } = true;

    /// <summary>Số click đã được ghi thành bước "Click phần tử UI".</summary>
    public int ElementClicks => _steps.Count(s => s.Type == StepType.ClickElement);

    private readonly List<Task> _conversions = [];
    private volatile bool _stopped;

    public void Start()
    {
        var module = Win32.GetModuleHandle(null);
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyProc, module, 0);
        if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Unhook();
            throw new Win32Exception(err, "Không cài được hook chuột/bàn phím.");
        }
        _lastTick = Environment.TickCount64;
    }

    /// <summary>Dừng ghi và trả về các bước đã ghi.</summary>
    public List<ActionStep> Stop()
    {
        Unhook();
        _pressed?.Shot?.Dispose();
        _pressed = null;
        // Chữ đang gõ dở thành bước ngay (ô mật khẩu quyết định theo kết quả đã có lúc này, không chờ thêm).
        FlushTyped();
        // Chờ các lần nhận diện phần tử / chọn hình mẫu còn dở (chạy nền, không cần luồng UI).
        try { Task.WaitAll([.. _conversions], 5000); } catch (AggregateException) { }
        _stopped = true;
        // Lần nhận diện nào đang ghi dở vào bước thì chờ nó ghi xong (các lần sau đó bị bỏ qua).
        foreach (var step in _steps) lock (step) { }
        if (_steps.Count > 0) _steps[^1].DelayAfterMs = 500;
        return [.. _steps];
    }

    private void Unhook()
    {
        if (_mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }

    public void Dispose() => Unhook();

    // ───────────────────────────── Chuột ─────────────────────────────

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg is 0x201 or 0x202 or 0x204 or 0x205 or 0x207 or 0x208 or 0x20A)
            {
                var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                var p = new Point(info.pt.X, info.pt.Y);
                try
                {
                    switch (msg)
                    {
                        case 0x201: OnButtonDown(p, MouseButtonKind.Left); break;
                        case 0x204: OnButtonDown(p, MouseButtonKind.Right); break;
                        case 0x207: OnButtonDown(p, MouseButtonKind.Middle); break;
                        case 0x202 or 0x205 or 0x208: OnButtonUp(p); break;
                        case 0x20A: OnWheel(p, (short)(info.mouseData >> 16)); break;
                    }
                }
                catch (Exception ex) { Debug.WriteLine(ex); }
            }
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    internal sealed record Press(Point Point, MouseButtonKind Button, IntPtr Root, IntPtr Window, string Target, long Tick,
        Task<Automation.UiElementFinder.CapturedElement?>? Element, Snapshot? Shot);

    /// <summary>Ảnh chụp quanh điểm nhấn chuột; <paramref name="Covered"/> = phần bị cửa sổ khác che (tọa độ trong ảnh).</summary>
    internal sealed record Snapshot(Bitmap Image, Rectangle Area, double Scale, List<Rectangle> Covered) : IDisposable
    {
        public void Dispose() => Image.Dispose();
    }

    private Press? _pressed;

    private void OnButtonDown(Point p, MouseButtonKind button)
    {
        _winAlone = false;
        _pressed?.Shot?.Dispose();
        _pressed = null;
        var root = WindowHelper.RootWindowAt(p);
        if (root == IntPtr.Zero || WindowHelper.BelongsToThisApp(root)) return;
        // Chụp ngay trong hook: ứng dụng chưa nhận click nên nút chưa lõm xuống, menu chưa mở/đóng.
        var shot = RecordImages ? Snap(p, root) : null;
        // Xác định cửa sổ đích ngay lúc nhấn: popup (menu, gợi ý…) thường đóng ngay sau click.
        var (window, target) = ResolveTarget(root);
        // Đọc phần tử dưới chuột ngay lúc nhấn (trước khi click làm giao diện thay đổi), chạy nền để hook trả về ngay.
        var element = RecordElements && target.Length > 0 ? Task.Run(() => CaptureElement(p)) : null;
        _pressed = new Press(p, button, root, window, target, Environment.TickCount64, element, shot);
    }

    private static Snapshot? Snap(Point p, IntPtr root)
    {
        try
        {
            double scale = PowerHelper.ScaleAt(p);
            var area = ClickAnchor.CaptureArea(p, scale);
            if (area.Width < 16 || area.Height < 16) return null;
            var covered = WindowHelper.CoveringRects(root, p, area).Select(r => ToShot(r, area)).ToList();
            return new Snapshot(ScreenCapture.Capture(area), area, scale, covered);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return null;
        }
    }

    private static Rectangle ToShot(Rectangle r, Rectangle area) => new(r.X - area.X, r.Y - area.Y, r.Width, r.Height);

    private static Automation.UiElementFinder.CapturedElement? CaptureElement(Point p)
    {
        try { return Automation.UiElementFinder.Capture(p); }
        catch (Exception ex) { Debug.WriteLine(ex); return null; }
    }

    /// <summary>Phần tử đủ rõ ràng để click lại bằng UI Automation (có tên/id, không phải vùng lớn như cửa sổ, trang web, khung vẽ).</summary>
    private static bool IsGoodElement(Automation.UiElementFinder.CapturedElement? e, IntPtr root) =>
        e != null && e.Window == root &&
        (e.Selector.Contains("AutomationId=") || e.Selector.Contains("Name=")) &&
        !System.Text.RegularExpressions.Regex.IsMatch(e.Selector, @"ControlType=(Pane|Window|Document|Custom|Group|TitleBar|ScrollBar|Thumb|Table|DataGrid|List|Tree|Image)\b") &&
        // Tên quá dài thường là nội dung thay đổi theo thời gian (gợi ý tìm kiếm, tiêu đề email, tên tài liệu…) → khó tìm lại.
        !System.Text.RegularExpressions.Regex.IsMatch(e.Selector, @"Name=[^;]{61,}");

    /// <summary>
    /// Nhận diện chạy nền sau mỗi click (giữ tọa độ lúc ghi làm dự phòng):
    /// phần tử UI rõ ràng → đổi thành "Click phần tử UI"; ảnh chụp lúc nhấn → hình mẫu để khi chạy tìm lại theo hình ảnh.
    /// </summary>
    internal void Recognize(ActionStep step, Press down)
    {
        if (down.Element == null && down.Shot == null) return;
        _conversions.Add(Task.Run(async () =>
        {
            using var shot = down.Shot;
            Automation.UiElementFinder.CapturedElement? e = null;
            if (down.Element != null)
            {
                try { e = await down.Element; }
                catch (Exception ex) { Debug.WriteLine(ex); }
            }

            // Click phần tử chỉ tìm được trong cửa sổ đích có tên (không áp dụng cho taskbar / Start ghi bằng tọa độ màn hình).
            if (down.Target.Length > 0 && IsGoodElement(e, down.Root))
            {
                lock (step)
                {
                    if (_stopped || step.Type != StepType.MouseClick) return;
                    step.Type = StepType.ClickElement;
                    step.Text = e!.Selector;
                    // Không thấy phần tử sau 5 giây → tìm theo hình mẫu / click theo tọa độ lúc ghi.
                    step.DelayMs = 5_000;
                }
            }

            if (shot == null) return;
            ClickAnchor.Choice? choice;
            try
            {
                Rectangle? bounds = e != null && e.Window == down.Root ? ToShot(e.Bounds, shot.Area) : null;
                choice = ClickAnchor.Choose(shot.Image, new Point(down.Point.X - shot.Area.X, down.Point.Y - shot.Area.Y), bounds, shot.Scale, shot.Covered);
                if (choice == null) return;
                lock (step)
                {
                    if (_stopped || !ActionStep.CanHaveImageAnchor(step.Type)) return;
                    ClickAnchor.Apply(step, shot.Image, choice, shot.Scale);
                    // Chờ hình mẫu xuất hiện tối đa 5 giây rồi mới click theo tọa độ.
                    if (step.Type == StepType.MouseClick) step.DelayMs = ClickAnchor.DefaultTimeoutMs;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }));
    }

    /// <summary>Thả chuột: di chuyển xa điểm nhấn → kéo thả, ngược lại → click.</summary>
    private void OnButtonUp(Point p)
    {
        if (_pressed is not { } down) return;
        _pressed = null;

        FlushTyped();
        long now = down.Tick;
        var target = down.Target;
        var origin = target.Length > 0 ? WindowHelper.GetRect(down.Window).Location : Point.Empty;
        int x = down.Point.X - origin.X, y = down.Point.Y - origin.Y;

        if (Math.Abs(p.X - down.Point.X) > 8 || Math.Abs(p.Y - down.Point.Y) > 8)
        {
            down.Shot?.Dispose();
            Add(new ActionStep
            {
                Type = StepType.MouseDrag, Target = target, X = x, Y = y,
                X2 = p.X - origin.X, Y2 = p.Y - origin.Y, Button = down.Button
            }, now, Environment.TickCount64);
            return;
        }

        // Hai click liên tiếp cùng chỗ trong thời gian double-click của Windows → gộp thành double-click.
        if (_steps.Count > 0 && _steps[^1] is { Type: StepType.MouseClick or StepType.ClickElement, DoubleClick: false } last &&
            last.Button == down.Button && last.Target == target &&
            now - _lastTick <= Win32.GetDoubleClickTime() &&
            Math.Abs(last.X - x) <= 4 && Math.Abs(last.Y - y) <= 4)
        {
            down.Shot?.Dispose();
            lock (last) last.DoubleClick = true;
            _lastTick = now;
            Changed?.Invoke();
            return;
        }

        var click = new ActionStep { Type = StepType.MouseClick, Target = target, X = x, Y = y, Button = down.Button };
        Add(click, now, now);
        Recognize(click, down);
    }

    /// <summary>Cuộn chuột: các lần cuộn liên tiếp trong cùng cửa sổ được gộp thành một bước.</summary>
    private void OnWheel(Point p, short delta)
    {
        var root = WindowHelper.RootWindowAt(p);
        if (root == IntPtr.Zero || WindowHelper.BelongsToThisApp(root)) return;
        FlushTyped();
        long now = Environment.TickCount64;
        var (window, target) = ResolveTarget(root);
        int notches = delta / 120;
        if (notches == 0) notches = Math.Sign(delta);

        if (_steps.Count > 0 && _steps[^1] is { Type: StepType.MouseScroll } last && last.Target == target &&
            now - _lastTick < 1000 && Math.Sign(last.Count) == Math.Sign(notches))
        {
            last.Count += notches;
            _lastTick = now;
            Changed?.Invoke();
            return;
        }
        var origin = target.Length > 0 ? WindowHelper.GetRect(window).Location : Point.Empty;
        Add(new ActionStep { Type = StepType.MouseScroll, Target = target, X = p.X - origin.X, Y = p.Y - origin.Y, Count = notches, DelayAfterMs = 300 }, now, now);
    }

    // ───────────────────────────── Bàn phím ─────────────────────────────

    private IntPtr KeyHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is 0x100 or 0x104 or 0x101 or 0x105) // WM_KEYDOWN, WM_SYSKEYDOWN, WM_KEYUP, WM_SYSKEYUP
        {
            var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            try
            {
                if ((int)wParam is 0x100 or 0x104) OnKey(info);
                else OnKeyUp(info);
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    // Phím Win nhấn rồi thả mà không kèm phím nào khác → mở Start, ghi thành "Nhấn Win".
    private bool _winDown, _winAlone;

    internal void OnKeyUp(Win32.KBDLLHOOKSTRUCT k)
    {
        if (k.vkCode is not (0x5B or 0x5C)) return;
        bool alone = _winAlone;
        _winDown = _winAlone = false;
        if (!alone || (k.flags & LLKHF_INJECTED) != 0) return;
        FlushTyped();
        AddKey("Win", IntPtr.Zero, Environment.TickCount64);
    }

    internal void OnKey(Win32.KBDLLHOOKSTRUCT k)
    {
        int vk = (int)k.vkCode;
        if (vk is 0x5B or 0x5C)
        {
            // Bỏ qua lặp phím khi giữ Win (nếu không, Win+E rồi giữ Win sẽ bị ghi thêm "Win").
            if (!_winDown) _winDown = _winAlone = true;
            return;
        }
        _winAlone = false;
        if (IsModifier(vk)) return;

        var fg = Win32.GetForegroundWindow();
        if (fg != IntPtr.Zero && WindowHelper.BelongsToThisApp(fg)) return;

        long now = Environment.TickCount64;
        bool injected = (k.flags & LLKHF_INJECTED) != 0;

        if (injected)
        {
            // Sự kiện do bộ gõ gửi ngay sau một phím vật lý → phím vật lý đó đã bị bộ gõ "nuốt", bỏ nó khỏi bộ đệm.
            if (!_physicalConsumed && _lastPhysicalCharTick >= 0 && now - _lastPhysicalCharTick <= ImeWindowMs && _typed.Length > 0)
                _typed.Length--;
            _physicalConsumed = true;
        }
        else
        {
            _lastPhysicalCharTick = -1;
            _physicalConsumed = true;
        }

        if (vk == VK_PACKET)
        {
            AppendText(((char)k.scanCode).ToString(), fg, now);
            return;
        }

        bool ctrl = Down(0x11), alt = Down(0x12), shift = Down(0x10), win = Down(0x5B) || Down(0x5C);

        if (ctrl || alt || win)
        {
            if (ctrl && shift && !alt && !win && vk == (int)Keys.Q)
            {
                StopRequested?.Invoke();
                return;
            }
            FlushTyped();
            var parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            if (win) parts.Add("Win");
            parts.Add(KeyName(vk));
            AddKey(string.Join("+", parts), fg, now);
            return;
        }

        if (vk == VK_BACK)
        {
            if (_typed.Length > 0)
            {
                _typed.Length--;
                _typedEnd = now;
                Changed?.Invoke();
            }
            else
            {
                AddKey("Backspace", fg, now);
            }
            return;
        }

        var special = SpecialKeyName(vk);
        if (special != null)
        {
            FlushTyped();
            AddKey(shift ? "Shift+" + special : special, fg, now);
            return;
        }

        var text = ToText(k, fg, shift);
        if (string.IsNullOrEmpty(text)) return;
        AppendText(text, fg, now);
        if (!injected)
        {
            _lastPhysicalCharTick = now;
            _physicalConsumed = false;
        }
    }

    internal void AppendText(string text, IntPtr fg, long now)
    {
        var target = ResolveTarget(fg).Target;
        // Biết ô đang nhập có phải ô mật khẩu không trước khi giữ ký tự; đổi loại ô (kể cả sang ô chưa rõ như ô mật khẩu WPF / trang web)
        // → bước gõ mới, để chữ gõ vào ô chưa rõ không bị gộp vào bước chữ thường đang ghi dở.
        var field = FocusedField(fg);
        // Ô chưa rõ: các lần hỏi UI Automation đã xong thấy hai ô khác nhau (trang web tự nhảy sang ô kế tiếp…) → cũng là đổi ô.
        if (_typed.Length > 0 && (target != _typedTarget || field != _typedField || FocusMovedWhileTyping())) FlushTyped();
        if (_typed.Length == 0)
        {
            _typedTarget = target;
            _typedStart = now;
            _typedField = field;
            // Chưa rõ (trình duyệt, WPF, UWP…) → hỏi UI Automation ở nền; chữ chỉ nằm trong bộ nhớ tới khi kết thúc đoạn gõ.
            if (field == FieldKind.Unknown) StartProbe(now);
        }
        else if (field == FieldKind.Unknown && now - _lastProbeTick >= ProbeIntervalMs && _typedProbes.All(p => p.IsCompleted))
        {
            // Gõ lâu trong ô chưa rõ → hỏi lại để biết tiêu điểm vẫn ở đúng ô đó.
            StartProbe(now);
        }
        _typed.Append(text);
        _typedEnd = now;
        Changed?.Invoke();
    }

    // ───────────────────────────── Ô mật khẩu ─────────────────────────────

    internal enum FieldKind { Unknown, Text, Password }

    /// <summary>Chữ gõ vào ô mật khẩu được lưu thành bí mật này (người dùng thêm giá trị trong 🔑 Bí mật), không bao giờ lưu chữ thật.</summary>
    internal const string PasswordPlaceholder = "{{secret:MatKhau}}";

    private const long ES_PASSWORD = 0x20;

    /// <summary>Gõ liên tục trong ô chưa rõ → cứ chừng này mili giây hỏi lại UI Automation một lần.</summary>
    private const int ProbeIntervalMs = 300;

    /// <summary>Chờ trước khi hỏi UI Automation: ứng dụng (nhất là trình duyệt) cập nhật tiêu điểm sau Tab / click chậm hơn phím tới hook.</summary>
    private const int ProbeDelayMs = 100;

    /// <summary>Kết quả hỏi UI Automation: phần tử có tiêu điểm có phải ô mật khẩu, kèm mã nhận diện (RuntimeId) của chính phần tử đó.</summary>
    internal sealed record FieldProbe(bool IsPassword, string ElementId);

    private FieldKind _typedField;
    private readonly List<Task<FieldProbe?>> _typedProbes = [];
    private long _lastProbeTick;

    /// <summary>Các lần hỏi của đoạn gõ trước nếu đó là ô chưa rõ (null = đoạn trước là ô Edit đã biết loại / chưa có đoạn nào).</summary>
    private List<Task<FieldProbe?>>? _previousProbes;

    /// <summary>Kiểm tra ngay (đồng bộ, trong hook) ô đang có tiêu điểm của cửa sổ <c>fg</c>.</summary>
    internal Func<IntPtr, FieldKind> FocusedField { get; set; } = FocusedFieldOf;

    /// <summary>Kiểm tra nền bằng UI Automation phần tử đang có tiêu điểm có phải ô mật khẩu (null = không xác định được).</summary>
    internal Func<Task<FieldProbe?>> PasswordProbe { get; set; } = ProbeFocusedElement;

    /// <summary>
    /// Ô Edit của Win32 / WinForms / Delphi (kể cả RichEdit): có kiểu ES_PASSWORD → mật khẩu, không có → chữ thường;
    /// cửa sổ khác (trang web, WPF, UWP…) không có kiểu này → chưa rõ.
    /// </summary>
    internal static FieldKind Classify(string className, long style) =>
        !className.Contains("edit", StringComparison.OrdinalIgnoreCase) ? FieldKind.Unknown
        : (style & ES_PASSWORD) != 0 ? FieldKind.Password : FieldKind.Text;

    internal static FieldKind FieldOf(IntPtr hwnd) =>
        hwnd == IntPtr.Zero ? FieldKind.Unknown : Classify(WindowHelper.GetClassName(hwnd), (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE));

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public Win32.RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    /// <summary>Ô có tiêu điểm bàn phím trong luồng của cửa sổ <paramref name="foreground"/> (GetGUIThreadInfo — không gửi message, không chờ ứng dụng).</summary>
    private static FieldKind FocusedFieldOf(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero) return FieldKind.Unknown;
        uint thread = Win32.GetWindowThreadProcessId(foreground, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) ? FieldOf(info.hwndFocus) : FieldKind.Unknown;
    }

    private static Task<FieldProbe?> ProbeFocusedElement() => Task.Run<FieldProbe?>(async () =>
    {
        await Task.Delay(ProbeDelayMs);
        try
        {
            var element = System.Windows.Automation.AutomationElement.FocusedElement;
            var id = element?.GetRuntimeId();
            return element != null && id is { Length: > 0 } ? new FieldProbe(element.Current.IsPassword, string.Join(".", id)) : null;
        }
        catch (Exception ex) { Debug.WriteLine(ex); return null; }
    });

    private void StartProbe(long now)
    {
        _lastProbeTick = now;
        Task<FieldProbe?> probe;
        try { probe = PasswordProbe(); }
        catch (Exception ex) { Debug.WriteLine(ex); probe = Task.FromResult<FieldProbe?>(null); }
        _typedProbes.Add(probe);
    }

    /// <summary>Các lần hỏi đã xong trong đoạn gõ thấy hai phần tử khác nhau → tiêu điểm đã rời ô đang gõ.</summary>
    private bool FocusMovedWhileTyping() =>
        _typedField == FieldKind.Unknown &&
        _typedProbes.Where(p => p.IsCompletedSuccessfully && p.Result != null).Select(p => p.Result!.ElementId).Distinct().Skip(1).Any();

    internal void FlushTyped()
    {
        if (_typed.Length == 0) return;
        var text = _typed.ToString();
        _typed.Clear();
        var field = _typedField;
        List<Task<FieldProbe?>> probes = [.. _typedProbes];
        _typedField = FieldKind.Unknown;
        _typedProbes.Clear();
        // Ô chưa rõ: chỉ dùng kết quả đã có ngay lúc này — kết quả tới sau (người dùng đã Tab / click sang ô khác) có thể là của ô khác.
        bool plain = field == FieldKind.Text || field == FieldKind.Unknown && PlainTextElement(probes, _previousProbes) != null;
        _previousProbes = field == FieldKind.Unknown ? probes : null;
        // Ô mật khẩu — hoặc chưa biết chắc — không lưu chữ thật vào jobs.json, dùng bí mật mã hóa thay thế.
        Add(new ActionStep { Type = StepType.TypeText, Target = _typedTarget, Text = plain ? text : PasswordPlaceholder }, _typedStart, _typedEnd);
    }

    /// <summary>
    /// Ô chưa rõ chỉ được coi là ô chữ thường (trả về mã phần tử) khi: có ít nhất một lần hỏi đã xong trước lúc kết thúc đoạn gõ,
    /// mọi lần đã xong đều trả lời "không phải mật khẩu" về cùng một phần tử, và phần tử đó không phải phần tử mà các lần hỏi của
    /// đoạn gõ ô chưa rõ ngay trước (<paramref name="previous"/>, kể cả lần xong muộn) đã thấy — UI Automation chưa kịp cập nhật tiêu điểm
    /// sau Tab / click sẽ còn trả lời về ô cũ; đoạn trước không có kết quả nào → không biết ô cũ là ô nào, giữ bí mật.
    /// Lần hỏi chưa xong bị bỏ qua: kết quả tới sau không bao giờ làm lộ chữ đã thay bằng {{secret:MatKhau}}.
    /// </summary>
    internal static string? PlainTextElement(IReadOnlyList<Task<FieldProbe?>> probes, IReadOnlyList<Task<FieldProbe?>>? previous)
    {
        string? id = null;
        foreach (var probe in probes)
        {
            if (!probe.IsCompleted) continue;
            if (!probe.IsCompletedSuccessfully || probe.Result is not { IsPassword: false } r || r.ElementId.Length == 0) return null;
            if (id != null && id != r.ElementId) return null;
            id = r.ElementId;
        }
        if (id == null || previous == null) return id;
        var seenBefore = previous.Where(p => p.IsCompletedSuccessfully && p.Result != null).Select(p => p.Result!.ElementId).ToList();
        return seenBefore.Count > 0 && !seenBefore.Contains(id) ? id : null;
    }

    private void AddKey(string name, IntPtr fg, long now)
    {
        var target = ResolveTarget(fg).Target;
        // Nhấn lặp cùng một phím → gộp thành "Phím*N".
        if (_steps.Count > 0 && _steps[^1] is { Type: StepType.KeyPress } last && last.Target == target && now - _lastTick < 1500)
        {
            var (baseName, count) = SplitRepeat(last.Text);
            if (baseName == name)
            {
                last.Text = $"{name}*{count + 1}";
                _lastTick = now;
                Changed?.Invoke();
                return;
            }
        }
        Add(new ActionStep { Type = StepType.KeyPress, Target = target, Text = name }, now, now);
    }

    private static (string Name, int Count) SplitRepeat(string text)
    {
        int star = text.LastIndexOf('*');
        return star > 0 && int.TryParse(text[(star + 1)..], out int n) ? (text[..star], n) : (text, 1);
    }

    /// <summary>Thêm bước; khoảng nghỉ của bước trước = thời gian thực tế giữa hai thao tác (giới hạn 0,15–5 giây).</summary>
    private void Add(ActionStep step, long start, long end)
    {
        if (_steps.Count > 0) _steps[^1].DelayAfterMs = (int)Math.Clamp(start - _lastTick, 150, 5000);

        // Chuyển sang cửa sổ khác (thường là cửa sổ vừa mở) → chờ cửa sổ đó xuất hiện trước khi thao tác.
        var previousTarget = _steps.LastOrDefault(s => s.Type != StepType.WaitForWindow)?.Target;
        if (step.Target.Length > 0 && previousTarget != null && previousTarget.Length > 0 && previousTarget != step.Target)
            _steps.Add(new ActionStep { Type = StepType.WaitForWindow, Target = step.Target, DelayMs = 15_000, DelayAfterMs = 300 });

        _steps.Add(step);
        _lastTick = end;
        Changed?.Invoke();
    }

    // ───────────────────────────── Hỗ trợ ─────────────────────────────

    /// <summary>Giao diện của Windows (taskbar, Start, jump list, khay hệ thống, desktop…): luôn ghi theo tọa độ màn hình.</summary>
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "SearchUI", "ShellHost", "TextInputHost", "LockApp"
    };

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "XamlExplorerHostIslandWindow", "TaskListThumbnailWnd", "Progman", "WorkerW"
    };

    private bool IsShellSurface(IntPtr root) =>
        ShellClasses.Contains(WindowHelper.GetClassName(root)) || ShellProcesses.Contains(ProcessNameOf(root));

    /// <summary>
    /// Cửa sổ dùng làm mốc khi phát lại cho thao tác trên <paramref name="root"/>:
    /// popup (menu chuột phải, gợi ý thanh địa chỉ, ô thả xuống…) → cửa sổ chính sở hữu nó, vì popup chỉ tồn tại thoáng qua
    /// và có vị trí khác mỗi lần; giao diện Windows → không có cửa sổ (tọa độ màn hình).
    /// </summary>
    internal (IntPtr Window, string Target) ResolveTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return (IntPtr.Zero, "");
        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        if (IsShellSurface(root)) return (IntPtr.Zero, "");

        var window = root;
        if (WindowHelper.IsPopup(root) || WindowHelper.GetTitle(root).Length == 0)
        {
            var owner = WindowHelper.OwnerWindow(root);
            if (owner != IntPtr.Zero)
            {
                window = owner;
            }
            else
            {
                // Menu (#32768) không có chủ sở hữu: lấy cửa sổ đang được chọn nếu cùng ứng dụng.
                var fg = Win32.GetForegroundWindow();
                fg = fg == IntPtr.Zero ? fg : Win32.GetAncestor(fg, Win32.GA_ROOT);
                if (fg != IntPtr.Zero && fg != root && WindowHelper.SameProcess(fg, root) && !IsShellSurface(fg)) window = fg;
            }
        }
        var target = TargetFor(window);
        return (target.Length > 0 ? window : IntPtr.Zero, target);
    }

    /// <summary>
    /// Cách nhận diện cửa sổ khi phát lại: "exe:tên_tiến_trình" (ổn định khi tiêu đề đổi, vd Chrome),
    /// tiêu đề với app UWP/Explorer; chuỗi rỗng = tọa độ tuyệt đối (taskbar, desktop…).
    /// </summary>
    private string TargetFor(IntPtr root)
    {
        var title = WindowHelper.GetTitle(root);
        if (title.Length == 0) return "";
        var name = ProcessNameOf(root);
        return name.Length == 0 || name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            ? title
            : "exe:" + name;
    }

    private string ProcessNameOf(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (!_processNames.TryGetValue(pid, out var name))
        {
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = ""; }
            _processNames[pid] = name;
        }
        return name;
    }

    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsModifier(int vk) =>
        vk is 0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5) or 0x90 or 0x91;

    private static string? SpecialKeyName(int vk) => vk switch
    {
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2C => "PrintScreen",
        0x5D => "Apps",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        _ => null
    };

    private static string KeyName(int vk) =>
        SpecialKeyName(vk) ?? vk switch
        {
            0x08 => "Backspace",
            0x20 => "Space",
            >= 'A' and <= 'Z' or >= '0' and <= '9' => ((char)vk).ToString(),
            _ => ((Keys)vk).ToString()
        };

    private static string ToText(Win32.KBDLLHOOKSTRUCT k, IntPtr fg, bool shift)
    {
        var state = new byte[256];
        if (shift) state[0x10] = state[0xA0] = 0x80;
        if ((Win32.GetKeyState(0x14) & 1) != 0) state[0x14] = 0x01;
        var layout = Win32.GetKeyboardLayout(Win32.GetWindowThreadProcessId(fg, out _));
        var sb = new StringBuilder(8);
        // Cờ 0x4: không làm thay đổi trạng thái bàn phím (tránh phá dead-key của ứng dụng đang gõ).
        int n = Win32.ToUnicodeEx(k.vkCode, k.scanCode, state, sb, sb.Capacity, 0x4, layout);
        if (n <= 0) return "";
        var text = sb.ToString(0, n);
        return text.All(c => !char.IsControl(c)) ? text : "";
    }
}
