using System.Text.RegularExpressions;

namespace ScheduleApp.Native;

/// <param name="Number">Số màn hình như trong Cài đặt Windows → Màn hình (DISPLAY1 = 1…).</param>
/// <param name="Bounds">Toàn bộ màn hình (pixel thật).</param>
/// <param name="WorkingArea">Phần không bị thanh tác vụ che.</param>
internal sealed record Display(int Number, Rectangle Bounds, Rectangle WorkingArea, bool Primary);

/// <summary>
/// Chọn màn hình khi máy có nhiều màn hình (vd phát video ở màn hình chiếu / TV, máy chính vẫn làm việc bình thường).
/// Lựa chọn lưu dạng số: 0 = màn hình chính, -1 = màn hình đang có chuột, N ≥ 1 = màn hình số N.
/// </summary>
internal static partial class Displays
{
    public const int Primary = 0;
    public const int UnderMouse = -1;

    [GeneratedRegex(@"(\d+)\s*$")]
    private static partial Regex TrailingNumber();

    /// <summary>Các màn hình đang cắm, theo số của Windows.</summary>
    public static List<Display> All()
    {
        var screens = Screen.AllScreens;
        var list = new List<Display>();
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            var m = TrailingNumber().Match(s.DeviceName);
            int number = m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > 0 ? n : i + 1;
            // Hai màn hình trùng số (hiếm) → số kế tiếp chưa dùng.
            while (list.Any(d => d.Number == number)) number++;
            list.Add(new Display(number, s.Bounds, s.WorkingArea, s.Primary));
        }
        return [.. list.OrderBy(d => d.Number)];
    }

    /// <summary>
    /// Màn hình ứng với lựa chọn <paramref name="choice"/>. Màn hình số N không còn cắm (vd rút máy chiếu) → màn hình chính,
    /// kèm cảnh báo để ghi nhật ký.
    /// </summary>
    public static (Display Display, string? Warning) Pick(int choice, IReadOnlyList<Display> displays, Point mouse)
    {
        if (displays.Count == 0) throw new InvalidOperationException("Không tìm thấy màn hình nào.");
        var primary = displays.FirstOrDefault(d => d.Primary) ?? displays[0];
        if (choice == UnderMouse)
            return (displays.FirstOrDefault(d => d.Bounds.Contains(mouse)) ?? primary, null);
        if (choice <= Primary) return (primary, null);
        var match = displays.FirstOrDefault(d => d.Number == choice);
        return match != null
            ? (match, null)
            : (primary, $"không thấy màn hình {choice} (máy đang có {displays.Count} màn hình) — phát ở màn hình chính");
    }

    public static (Display Display, string? Warning) Pick(int choice) => Pick(choice, All(), Cursor.Position);

    /// <summary>Tên lựa chọn để hiện trong mô tả bước.</summary>
    public static string ChoiceName(int choice) => choice switch
    {
        UnderMouse => "màn hình đang có chuột",
        <= Primary => "màn hình chính",
        _ => $"màn hình {choice}"
    };

    /// <summary>"Màn hình 2 — 1920×1080, bên phải" (vị trí so với màn hình chính).</summary>
    public static string Label(Display d, IReadOnlyList<Display> all)
    {
        var text = $"Màn hình {d.Number} — {d.Bounds.Width}×{d.Bounds.Height}";
        if (d.Primary) return text + " (chính)";
        var primary = all.FirstOrDefault(x => x.Primary);
        if (primary == null) return text;
        string? side = d.Bounds.Left >= primary.Bounds.Right ? "bên phải"
            : d.Bounds.Right <= primary.Bounds.Left ? "bên trái"
            : d.Bounds.Bottom <= primary.Bounds.Top ? "phía trên"
            : d.Bounds.Top >= primary.Bounds.Bottom ? "phía dưới"
            : null;
        return side != null ? $"{text}, {side}" : text;
    }

    /// <summary>Khung cửa sổ phát video trên màn hình <paramref name="d"/>: toàn màn hình = cả màn hình; cửa sổ thường = 2/3 vùng làm việc, ở giữa.</summary>
    public static Rectangle PlayerRect(Display d, bool fullscreen)
    {
        if (fullscreen) return d.Bounds;
        var area = d.WorkingArea;
        int w = Math.Max(320, area.Width * 2 / 3), h = Math.Max(240, area.Height * 2 / 3);
        w = Math.Min(w, area.Width);
        h = Math.Min(h, area.Height);
        return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }
}
