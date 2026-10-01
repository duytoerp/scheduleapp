using System.Runtime.InteropServices;

namespace ScheduleApp.Native;

/// <summary>
/// Chế độ an toàn: phát hiện người dùng bấm chuột / gõ phím thật (không phải do ScheduleApp giả lập) trong lúc flow chạy,
/// để tạm dừng flow và hỏi có chạy tiếp không. Bỏ qua thao tác trên cửa sổ của chính ScheduleApp.
/// Phải Start/Stop trên luồng UI (hook cần vòng lặp message).
/// </summary>
internal sealed class UserInputGuard : IDisposable
{
    private const uint LLMHF_INJECTED = 0x1, LLKHF_INJECTED = 0x10;

    private readonly Win32.LowLevelProc _mouseProc;
    private readonly Win32.LowLevelProc _keyProc;
    private IntPtr _mouseHook, _keyHook;
    private volatile bool _triggered;

    public UserInputGuard()
    {
        _mouseProc = MouseHook;
        _keyProc = KeyHook;
    }

    public bool IsActive => _mouseHook != IntPtr.Zero;

    /// <summary>Người dùng đã can thiệp kể từ lần <see cref="Reset"/> gần nhất.</summary>
    public bool Triggered => _triggered;

    public void Reset() => _triggered = false;

    public void Start()
    {
        if (IsActive) return;
        _triggered = false;
        var module = Win32.GetModuleHandle(null);
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, module, 0);
        _keyHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyProc, module, 0);
    }

    public void Stop()
    {
        if (_mouseHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_mouseHook);
        if (_keyHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_keyHook);
        _mouseHook = _keyHook = IntPtr.Zero;
    }

    public void Dispose() => Stop();

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Chỉ tính click / cuộn (di chuột đơn thuần không tính — dễ chạm nhầm).
        if (nCode >= 0 && (int)wParam is 0x201 or 0x204 or 0x207 or 0x20A)
        {
            var info = Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLMHF_INJECTED) == 0)
            {
                var root = WindowHelper.RootWindowAt(new Point(info.pt.X, info.pt.Y));
                if (root == IntPtr.Zero || !WindowHelper.BelongsToThisApp(root)) _triggered = true;
            }
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr KeyHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam is 0x100 or 0x104)
        {
            var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)info.vkCode;
            bool modifier = vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);
            bool stopHotkey = vk == 'Q' && (Win32.GetAsyncKeyState(0x11) & 0x8000) != 0 && (Win32.GetAsyncKeyState(0x10) & 0x8000) != 0;
            if ((info.flags & LLKHF_INJECTED) == 0 && !modifier && !stopHotkey && !WindowHelper.BelongsToThisApp(Win32.GetForegroundWindow()))
                _triggered = true;
        }
        return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
