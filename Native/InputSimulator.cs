using System.ComponentModel;
using System.Runtime.InteropServices;
using ScheduleApp.Models;

namespace ScheduleApp.Native;

/// <summary>Giả lập chuột và bàn phím qua SendInput.</summary>
internal static class InputSimulator
{
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    private static readonly Dictionary<string, ushort> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B, ["windows"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20,
        ["backspace"] = 0x08, ["back"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["ins"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pgup"] = 0x21, ["pagedown"] = 0x22, ["pgdn"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["capslock"] = 0x14, ["numlock"] = 0x90, ["menu"] = 0x5D, ["apps"] = 0x5D,
        ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE,
        ["volumeup"] = 0xAF, ["volumedown"] = 0xAE, ["volumemute"] = 0xAD,
        ["playpause"] = 0xB3, ["nexttrack"] = 0xB0, ["prevtrack"] = 0xB1
    };

    public static void Click(int x, int y, MouseButtonKind button, bool doubleClick)
    {
        Win32.SetCursorPos(x, y);
        Thread.Sleep(60);
        var (down, up) = button switch
        {
            MouseButtonKind.Right => (0x0008u, 0x0010u),
            MouseButtonKind.Middle => (0x0020u, 0x0040u),
            _ => (0x0002u, 0x0004u)
        };
        int times = doubleClick ? 2 : 1;
        for (int i = 0; i < times; i++)
        {
            Send(Mouse(down));
            Thread.Sleep(30);
            Send(Mouse(up));
            if (i < times - 1) Thread.Sleep(70);
        }
    }

    /// <summary>Cuộn chuột N nấc (dương = lên, âm = xuống) tại vị trí con trỏ hiện tại.</summary>
    public static void Scroll(int notches)
    {
        if (notches == 0) return;
        var input = Mouse(0x0800); // MOUSEEVENTF_WHEEL
        input.U.mi.mouseData = unchecked((uint)(notches * 120));
        Send(input);
    }

    /// <summary>Nhấn giữ nút chuột tại (x1, y1), kéo dần tới (x2, y2) rồi thả.</summary>
    public static void Drag(int x1, int y1, int x2, int y2, MouseButtonKind button, CancellationToken ct)
    {
        var (down, up) = button switch
        {
            MouseButtonKind.Right => (0x0008u, 0x0010u),
            MouseButtonKind.Middle => (0x0020u, 0x0040u),
            _ => (0x0002u, 0x0004u)
        };
        MoveTo(x1, y1);
        Thread.Sleep(80);
        Send(Mouse(down));
        Thread.Sleep(120);
        // Di chuyển từng đoạn nhỏ để ứng dụng nhận ra thao tác kéo (OLE drag cần vài sự kiện di chuột).
        const int steps = 20;
        for (int i = 1; i <= steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            MoveTo(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
            Thread.Sleep(15);
        }
        Thread.Sleep(120);
        Send(Mouse(up));
    }

    /// <summary>Di chuột tới tọa độ màn hình bằng SendInput (sinh sự kiện di chuột thật, khác SetCursorPos).</summary>
    public static void MoveTo(int x, int y)
    {
        var vs = SystemInformation.VirtualScreen;
        var input = Mouse(0x0001 | 0x8000 | 0x4000); // MOVE | ABSOLUTE | VIRTUALDESK
        input.U.mi.dx = (int)Math.Round((x - vs.Left) * 65535.0 / Math.Max(1, vs.Width - 1));
        input.U.mi.dy = (int)Math.Round((y - vs.Top) * 65535.0 / Math.Max(1, vs.Height - 1));
        Send(input);
        Win32.SetCursorPos(x, y); // chỉnh lại cho đúng pixel (làm tròn tọa độ chuẩn hóa có thể lệch 1px)
    }

    /// <summary>Gõ văn bản Unicode (hỗ trợ tiếng Việt có dấu).</summary>
    public static void TypeText(string text, CancellationToken ct)
    {
        foreach (char ch in text.Replace("\r\n", "\n"))
        {
            ct.ThrowIfCancellationRequested();
            if (ch == '\n') KeyTap(0x0D);
            else if (ch == '\t') KeyTap(0x09);
            else Send(Unicode(ch, false), Unicode(ch, true));
            Thread.Sleep(12);
        }
    }

    /// <summary>
    /// Nhấn tổ hợp phím. Cú pháp: "Ctrl+C", "Alt+F4", "Tab*3", nhiều tổ hợp cách nhau bởi dấu phẩy: "Ctrl+A, Delete".
    /// </summary>
    public static void SendKeys(string spec, CancellationToken ct)
    {
        foreach (var (keys, repeat) in Parse(spec))
        {
            for (int r = 0; r < repeat; r++)
            {
                ct.ThrowIfCancellationRequested();
                var inputs = new List<Win32.INPUT>();
                inputs.AddRange(keys.Select(k => Key(k, false)));
                inputs.AddRange(Enumerable.Reverse(keys).Select(k => Key(k, true)));
                Send([.. inputs]);
                Thread.Sleep(60);
            }
        }
    }

    /// <summary>Kiểm tra cú pháp tổ hợp phím, ném FormatException nếu sai.</summary>
    public static void Validate(string spec)
    {
        if (Parse(spec).Count == 0) throw new FormatException("Chưa nhập phím nào.");
    }

    private static List<(List<ushort> Keys, int Repeat)> Parse(string spec)
    {
        var result = new List<(List<ushort>, int)>();
        foreach (var raw in spec.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var combo = raw;
            int repeat = 1;
            int star = combo.LastIndexOf('*');
            if (star > 0 && int.TryParse(combo[(star + 1)..].Trim(), out int n))
            {
                repeat = Math.Clamp(n, 1, 500);
                combo = combo[..star].Trim();
            }
            var keys = combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(ParseKey).ToList();
            if (keys.Count > 0) result.Add((keys, repeat));
        }
        return result;
    }

    internal static ushort ParseKey(string name)
    {
        if (KeyNames.TryGetValue(name, out var vk)) return vk;

        if (name.Length > 1 && (name[0] is 'F' or 'f') && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24)
            return (ushort)(0x70 + f - 1);

        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
            short scan = Win32.VkKeyScan(name[0]);
            if (scan != -1) return (ushort)(scan & 0xFF);
        }

        if (Enum.TryParse<Keys>(name, true, out var key) && (int)key is > 0 and < 256)
            return (ushort)key;

        throw new FormatException($"Không nhận ra phím \"{name}\".");
    }

    private static bool IsExtended(ushort vk) =>
        vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2C or 0x2D or 0x2E
            or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5;

    private static void KeyTap(ushort vk) => Send(Key(vk, false), Key(vk, true));

    private static Win32.INPUT Key(ushort vk, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new Win32.INPUT
        {
            type = INPUT_KEYBOARD,
            U = new Win32.InputUnion
            {
                ki = new Win32.KEYBDINPUT { wVk = vk, wScan = (ushort)Win32.MapVirtualKey(vk, 0), dwFlags = flags }
            }
        };
    }

    private static Win32.INPUT Unicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new Win32.InputUnion
        {
            ki = new Win32.KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) }
        }
    };

    private static Win32.INPUT Mouse(uint flags) => new()
    {
        type = INPUT_MOUSE,
        U = new Win32.InputUnion { mi = new Win32.MOUSEINPUT { dwFlags = flags } }
    };

    private static void Send(params Win32.INPUT[] inputs)
    {
        uint sent = Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
        if (sent != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Không gửi được thao tác chuột/phím (màn hình đang khóa hoặc cửa sổ đích chạy quyền Administrator).");
    }
}
