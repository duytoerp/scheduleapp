using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ScheduleApp.Models;
using ScheduleApp.Native;

namespace ScheduleApp.Recording;

/// <summary>
/// Ghi thao tác chuột/bàn phím toàn hệ thống (low-level hook) và chuyển thành các bước flow:
/// click → "Click chuột" (tọa độ tương đối theo cửa sổ), chữ gõ liên tiếp → "Gõ văn bản",
/// phím đặc biệt / tổ hợp → "Nhấn phím". Phải chạy trên luồng UI (cần vòng lặp message).
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
        FlushTyped();
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
            MouseButtonKind? button = (int)wParam switch
            {
                0x201 => MouseButtonKind.Left,
                0x204 => MouseButtonKind.Right,
                0x207 => MouseButtonKind.Middle,
                _ => null
            };
            if (button != null)
            {
                var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                try { OnClick(new Point(info.pt.X, info.pt.Y), button.Value); }
                catch (Exception ex) { Debug.WriteLine(ex); }
            }
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void OnClick(Point p, MouseButtonKind button)
    {
        var root = WindowHelper.RootWindowAt(p);
        if (root == IntPtr.Zero || WindowHelper.BelongsToThisApp(root)) return;

        FlushTyped();
        long now = Environment.TickCount64;
        var target = TargetFor(root);
        int x = p.X, y = p.Y;
        if (target.Length > 0)
        {
            var rect = WindowHelper.GetRect(root);
            x -= rect.Left;
            y -= rect.Top;
        }

        // Hai click liên tiếp cùng chỗ trong thời gian double-click của Windows → gộp thành double-click.
        if (_steps.Count > 0 && _steps[^1] is { Type: StepType.MouseClick, DoubleClick: false } last &&
            last.Button == button && last.Target == target &&
            now - _lastTick <= Win32.GetDoubleClickTime() &&
            Math.Abs(last.X - x) <= 4 && Math.Abs(last.Y - y) <= 4)
        {
            last.DoubleClick = true;
            _lastTick = now;
            Changed?.Invoke();
            return;
        }

        Add(new ActionStep { Type = StepType.MouseClick, Target = target, X = x, Y = y, Button = button }, now, now);
    }

    // ───────────────────────────── Bàn phím ─────────────────────────────

    private IntPtr KeyHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is 0x100 or 0x104) // WM_KEYDOWN, WM_SYSKEYDOWN
        {
            var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            try { OnKey(info); }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void OnKey(Win32.KBDLLHOOKSTRUCT k)
    {
        int vk = (int)k.vkCode;
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

    private void AppendText(string text, IntPtr fg, long now)
    {
        var target = TargetFor(fg);
        if (_typed.Length > 0 && target != _typedTarget) FlushTyped();
        if (_typed.Length == 0)
        {
            _typedTarget = target;
            _typedStart = now;
        }
        _typed.Append(text);
        _typedEnd = now;
        Changed?.Invoke();
    }

    private void FlushTyped()
    {
        if (_typed.Length == 0) return;
        var text = _typed.ToString();
        _typed.Clear();
        Add(new ActionStep { Type = StepType.TypeText, Target = _typedTarget, Text = text }, _typedStart, _typedEnd);
    }

    private void AddKey(string name, IntPtr fg, long now)
    {
        var target = TargetFor(fg);
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
        _steps.Add(step);
        _lastTick = end;
        Changed?.Invoke();
    }

    // ───────────────────────────── Hỗ trợ ─────────────────────────────

    /// <summary>
    /// Cách nhận diện cửa sổ khi phát lại: "exe:tên_tiến_trình" (ổn định khi tiêu đề đổi, vd Chrome),
    /// tiêu đề với app UWP/Explorer; chuỗi rỗng = tọa độ tuyệt đối (menu popup, taskbar…).
    /// </summary>
    private string TargetFor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        var title = WindowHelper.GetTitle(root);
        if (title.Length == 0) return "";

        Win32.GetWindowThreadProcessId(root, out uint pid);
        if (!_processNames.TryGetValue(pid, out var name))
        {
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = ""; }
            _processNames[pid] = name;
        }
        return name.Length == 0 || name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            ? title
            : "exe:" + name;
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
