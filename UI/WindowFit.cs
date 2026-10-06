using ScheduleApp.Models;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>
/// Cửa sổ tự vừa màn hình của từng máy: cửa sổ lớn chiếm cùng một tỉ lệ vùng làm việc của màn hình (máy nào nhìn cũng giống nhau,
/// màn nhỏ không bị tràn, màn lớn không bị bé tí), cửa sổ nào cũng được thu cho vừa khi màn hình nhỏ hơn thiết kế, và vị trí / kích thước
/// nhớ theo tỉ lệ màn hình (không theo pixel) — chuyển sang máy có độ phân giải / độ phóng khác vẫn đúng.
/// </summary>
internal static class WindowFit
{
    /// <summary>Khoảng chừa quanh cửa sổ khi thu cho vừa (pixel thiết bị).</summary>
    private const int Margin = 8;

    /// <summary>
    /// Vị trí + kích thước cửa sổ trong vùng làm việc <paramref name="area"/>: <paramref name="share"/> &gt; 0 → chiếm tỉ lệ đó của vùng làm việc
    /// (không nhỏ hơn <paramref name="min"/>); không thì giữ <paramref name="size"/>. Luôn thu cho vừa vùng làm việc, đặt giữa
    /// <paramref name="center"/> (cửa sổ cha / vùng làm việc) rồi đẩy vào trong màn hình.
    /// </summary>
    internal static Rectangle Compute(Size size, Size min, Rectangle area, SizeF share, Rectangle center)
    {
        int w = share.Width > 0 ? Math.Max(min.Width, (int)Math.Round(area.Width * share.Width)) : size.Width;
        int h = share.Height > 0 ? Math.Max(min.Height, (int)Math.Round(area.Height * share.Height)) : size.Height;
        w = Math.Min(w, Math.Max(1, area.Width - 2 * Margin));
        h = Math.Min(h, Math.Max(1, area.Height - 2 * Margin));
        var r = new Rectangle(center.X + (center.Width - w) / 2, center.Y + (center.Height - h) / 2, w, h);
        return Inside(r, area);
    }

    /// <summary>Đẩy <paramref name="r"/> vào trong <paramref name="area"/> (thu lại nếu lớn hơn).</summary>
    internal static Rectangle Inside(Rectangle r, Rectangle area)
    {
        int w = Math.Min(r.Width, area.Width), h = Math.Min(r.Height, area.Height);
        int x = Math.Clamp(r.X, area.Left, area.Right - w), y = Math.Clamp(r.Y, area.Top, area.Bottom - h);
        return new Rectangle(x, y, w, h);
    }

    /// <summary>Kích thước tối thiểu không lớn hơn vùng làm việc (để cửa sổ thu được cho vừa màn hình nhỏ).</summary>
    internal static Size ClampMinimum(Size min, Rectangle area) =>
        new(Math.Min(min.Width, Math.Max(0, area.Width - 2 * Margin)), Math.Min(min.Height, Math.Max(0, area.Height - 2 * Margin)));

    // ───────────────────────────── Nhớ vị trí theo tỉ lệ màn hình ─────────────────────────────

    /// <summary>Vị trí đã nhớ → khung trên màn hình hiện có (cùng tên màn hình nếu còn cắm, không thì màn hình chính); null nếu chưa nhớ.</summary>
    internal static (Rectangle Bounds, bool Maximized)? Restore(WindowLayout? saved, IReadOnlyList<(string Name, Rectangle Area)> screens, Size min)
    {
        if (saved == null || screens.Count == 0 || saved.W <= 0 || saved.H <= 0) return null;
        var area = screens.FirstOrDefault(s => s.Name == saved.Screen).Area;
        if (area.IsEmpty) area = screens[0].Area;
        min = ClampMinimum(min, area);
        var r = new Rectangle(
            area.X + (int)Math.Round(saved.X * area.Width), area.Y + (int)Math.Round(saved.Y * area.Height),
            Math.Max(min.Width, (int)Math.Round(saved.W * area.Width)), Math.Max(min.Height, (int)Math.Round(saved.H * area.Height)));
        return (Inside(r, area), saved.Maximized);
    }

    /// <summary>Khung cửa sổ (trạng thái bình thường) → tỉ lệ so với vùng làm việc của màn hình chứa tâm cửa sổ.</summary>
    internal static WindowLayout Capture(Rectangle bounds, bool maximized, string screen, Rectangle area) => new()
    {
        Screen = screen,
        X = (bounds.X - area.X) / (double)area.Width,
        Y = (bounds.Y - area.Y) / (double)area.Height,
        W = bounds.Width / (double)area.Width,
        H = bounds.Height / (double)area.Height,
        Maximized = maximized
    };

    private static List<(string Name, Rectangle Area)> Screens() =>
        [.. Screen.AllScreens.OrderByDescending(s => s.Primary).Select(s => (s.DeviceName, s.WorkingArea))];

    /// <summary>Gọi trong OnLoad của form (sau khi đã scale theo DPI): đặt vị trí / kích thước cho vừa màn hình.</summary>
    internal static void Apply(Form form, SizeF share, string? layoutKey)
    {
        // Form tự đặt vị trí (thanh công cụ, cửa sổ ngoài màn hình khi kiểm thử…) → để nguyên.
        if (form.StartPosition == FormStartPosition.Manual) return;
        if (layoutKey != null && Restore(Saved(layoutKey), Screens(), form.MinimumSize) is var (bounds, maximized))
        {
            var screenArea = Screen.FromRectangle(bounds).WorkingArea;
            form.MinimumSize = ClampMinimum(form.MinimumSize, screenArea);
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = bounds;
            if (maximized) form.WindowState = FormWindowState.Maximized;
            return;
        }
        var owner = form.Owner is { Visible: true, WindowState: not FormWindowState.Minimized } o ? o : null;
        var screen = owner != null ? Screen.FromControl(owner) : Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;
        form.MinimumSize = ClampMinimum(form.MinimumSize, area);
        var center = form.StartPosition == FormStartPosition.CenterParent && owner != null ? owner.Bounds : area;
        var r = Compute(form.Size, form.MinimumSize, area, share, center);
        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = r;
    }

    /// <summary>Gọi khi form đóng: nhớ vị trí / kích thước theo tỉ lệ màn hình.</summary>
    internal static void Remember(Form form, string layoutKey)
    {
        if (form.WindowState == FormWindowState.Minimized || !form.IsHandleCreated) return;
        var normal = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
        if (normal.Width <= 0 || normal.Height <= 0) return;
        var screen = Screen.FromRectangle(normal);
        var layout = Capture(normal, form.WindowState == FormWindowState.Maximized, screen.DeviceName, screen.WorkingArea);
        SettingsStore.Current.Windows[layoutKey] = layout;
        try { SettingsStore.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Không lưu được vị trí cửa sổ: " + ex.Message); }
    }

    private static WindowLayout? Saved(string key) => SettingsStore.Current.Windows.GetValueOrDefault(key);
}
