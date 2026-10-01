using System.Drawing.Drawing2D;
using System.Drawing.Text;
using ScheduleApp.Models;

namespace ScheduleApp.UI;

/// <summary>Màu, icon và nhóm của từng loại bước — dùng chung cho hộp công cụ và khung thiết kế flow.</summary>
internal static class StepVisuals
{
    /// <summary>Định dạng kéo thả: một loại bước mới từ hộp công cụ.</summary>
    public const string StepTypeFormat = "ScheduleApp.StepType";

    /// <summary>Định dạng kéo thả: vị trí của bước đang được sắp xếp lại.</summary>
    public const string StepIndexFormat = "ScheduleApp.StepIndex";

    public static readonly (string Name, StepType[] Types)[] Categories =
    [
        ("Ứng dụng", [StepType.LaunchApp, StepType.CloseApp, StepType.RunCommand]),
        ("Cửa sổ", [StepType.WaitForWindow, StepType.FocusWindow]),
        ("Chuột & bàn phím", [StepType.MouseClick, StepType.TypeText, StepType.KeyPress, StepType.MouseScroll, StepType.MouseDrag]),
        ("Phần tử UI (ổn định nhất)", [StepType.ClickElement, StepType.SetElementText, StepType.WaitForElement]),
        ("Trình duyệt web", [StepType.Browser]),
        ("Nhận dạng màn hình", [StepType.ClickImage, StepType.WaitForImage, StepType.ClickText, StepType.WaitForText]),
        ("Biến & dữ liệu", [StepType.SetVariable, StepType.WriteData, StepType.LogMessage]),
        ("Tích hợp", [StepType.HttpRequest, StepType.AskAi, StepType.Notify]),
        ("Kiểm thử & Dynamics 365", [StepType.Dynamics, StepType.Assert]),
        ("Điều kiện & lặp", [StepType.If, StepType.Else, StepType.Loop, StepType.BreakLoop, StepType.ContinueLoop, StepType.Label, StepType.Goto]),
        ("Điều khiển luồng", [StepType.Wait, StepType.Reminder, StepType.CallJob, StepType.StopFlow])
    ];

    public static Color Accent(StepType t) => t switch
    {
        StepType.LaunchApp or StepType.CloseApp or StepType.RunCommand => Color.FromArgb(0, 120, 212),
        StepType.WaitForWindow or StepType.FocusWindow => Color.FromArgb(136, 84, 208),
        StepType.MouseClick or StepType.TypeText or StepType.KeyPress or StepType.MouseScroll or StepType.MouseDrag => Color.FromArgb(202, 80, 16),
        StepType.Reminder => Color.FromArgb(186, 132, 0),
        StepType.ClickImage or StepType.WaitForImage or StepType.ClickText or StepType.WaitForText => Color.FromArgb(0, 137, 123),
        StepType.ClickElement or StepType.SetElementText or StepType.WaitForElement => Color.FromArgb(180, 50, 160),
        StepType.Browser => Color.FromArgb(0, 90, 158),
        StepType.SetVariable or StepType.LogMessage or StepType.WriteData => Color.FromArgb(92, 92, 190),
        StepType.HttpRequest or StepType.AskAi or StepType.Notify => Color.FromArgb(0, 128, 96),
        StepType.Dynamics => Color.FromArgb(116, 39, 116),
        StepType.Assert => Color.FromArgb(0, 118, 140),
        _ => Color.FromArgb(16, 124, 16)
    };

    /// <summary>Thẻ đánh dấu cuối/giữa khối (Không thì, Hết Nếu, Hết lặp) — vẽ gọn hơn.</summary>
    public static bool IsMarker(StepType t) => t is StepType.Else or StepType.EndIf or StepType.EndLoop;

    /// <summary>Mã ký tự trong font Segoe Fluent Icons / Segoe MDL2 Assets.</summary>
    private static string Glyph(StepType t) => t switch
    {
        StepType.LaunchApp => "",
        StepType.CloseApp => "",
        StepType.RunCommand => "",
        StepType.WaitForWindow => "",
        StepType.FocusWindow => "",
        StepType.MouseClick => "",
        StepType.TypeText => "",
        StepType.KeyPress => "",
        StepType.Wait => "",
        StepType.Reminder => "",
        StepType.ClickImage => "\uE8B9",
        StepType.WaitForImage => "\uE890",
        StepType.ClickText => "\uE8D2",
        StepType.WaitForText => "\uE7C3",
        StepType.SetVariable => "\uE943",
        StepType.LogMessage => "\uE70B",
        StepType.If or StepType.Else => "\uE8AB",
        StepType.EndIf => "\uE73E",
        StepType.Loop => "\uE8EE",
        StepType.EndLoop => "\uE72C",
        StepType.BreakLoop => "\uE711",
        StepType.Label => "\uE8EC",
        StepType.Goto => "\uE72A",
        StepType.StopFlow => "\uE71A",
        StepType.CallJob => "\uE8A7",
        StepType.ClickElement => "\uE7C9",
        StepType.SetElementText => "\uEDA2",
        StepType.WaitForElement => "\uECA5",
        StepType.Browser => "\uE774",
        StepType.MouseScroll => "\uE74B",
        StepType.MouseDrag => "\uE7C2",
        StepType.ContinueLoop => "\uE893",
        StepType.WriteData => "\uE74E",
        StepType.HttpRequest => "\uE71B",
        StepType.AskAi => "\uE82F",
        StepType.Notify => "\uE715",
        StepType.Dynamics => "\uE8A5",
        StepType.Assert => "\uE9D5",
        _ => ""
    };

    private static readonly Lazy<string?> IconFamilyLazy = new(() =>
    {
        using var fonts = new InstalledFontCollection();
        var names = fonts.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons"
             : names.Contains("Segoe MDL2 Assets") ? "Segoe MDL2 Assets"
             : null;
    });

    public static Font CreateIconFont(Font baseFont, float extraSize) =>
        new(IconFamilyLazy.Value ?? baseFont.FontFamily.Name, baseFont.Size + extraSize);

    /// <summary>Pha màu với trắng (amount = 0 → giữ nguyên, 1 → trắng).</summary>
    public static Color Tint(Color c, float amount) => Color.FromArgb(
        (int)(c.R + (255 - c.R) * amount),
        (int)(c.G + (255 - c.G) * amount),
        (int)(c.B + (255 - c.B) * amount));

    public static void DrawIcon(Graphics g, StepType type, Rectangle circle, Font iconFont, bool enabled, TextFormatFlags extraFlags = 0)
    {
        var accent = enabled ? Accent(type) : Color.FromArgb(150, 150, 150);
        using (var bg = new SolidBrush(Tint(accent, 0.86f))) g.FillEllipse(bg, circle);
        var text = IconFamilyLazy.Value != null ? Glyph(type) : ActionStep.TypeNames[type][..1];
        TextRenderer.DrawText(g, text, iconFont, circle, accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | extraFlags);
    }

    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
