using ScheduleApp.Models;

namespace ScheduleApp.Vision;

/// <summary>
/// Hình mẫu kèm theo click khi ghi thao tác: chụp quanh điểm click ngay lúc nhấn chuột, chọn vùng đủ đặc trưng
/// (đúng khung nút / ô nếu nhận diện được, nếu không thì vùng lớn dần quanh điểm click) để khi chạy tìm lại chỗ đó
/// theo hình ảnh thay vì click mù theo tọa độ.
/// </summary>
internal static class ClickAnchor
{
    /// <summary>Độ khớp mặc định — thấp hơn bước "Click vào hình ảnh" (85%) vì lúc ghi chỗ được click đang được rê chuột (hover).</summary>
    public const int DefaultConfidence = 80;

    /// <summary>Thời gian chờ hình mẫu xuất hiện (ms) trước khi click theo tọa độ lúc ghi.</summary>
    public const int DefaultTimeoutMs = 5_000;

    /// <summary>Chỗ khác trong ảnh chụp giống hình mẫu tới mức này thì coi là không phân biệt được.</summary>
    internal const double AmbiguousScore = 0.8;

    /// <summary>Các chỗ khớp chênh nhau không quá mức này coi như giống nhau → chọn chỗ gần vị trí lúc ghi nhất.</summary>
    internal const double TieScore = 0.03;

    // Vùng quanh điểm click (px ở scale 100%), thử từ nhỏ tới lớn.
    private static readonly Size[] Patches = [new(56, 28), new(88, 36), new(128, 48), new(184, 64), new(260, 96)];

    // Khung phần tử chỉ dùng làm hình mẫu khi không quá nhỏ (dễ trùng) hay quá lớn (ô nhập dài, cả vùng nội dung).
    private static readonly Size MinElement = new(16, 12), MaxElement = new(360, 120);

    /// <summary>Vùng chụp lúc nhấn chuột: đủ rộng để chọn hình mẫu và kiểm tra trùng lặp xung quanh điểm click.</summary>
    public static Rectangle CaptureArea(Point p, double scale)
    {
        int hw = Px(360, scale), hh = Px(240, scale);
        return Rectangle.Intersect(new Rectangle(p.X - hw, p.Y - hh, 2 * hw, 2 * hh), ScreenCapture.VirtualScreen);
    }

    /// <param name="Bounds">Vùng hình mẫu trong ảnh chụp.</param>
    /// <param name="Offset">Điểm click lệch so với tâm hình mẫu.</param>
    /// <param name="FromElement">Hình mẫu là đúng khung phần tử UI được click.</param>
    public sealed record Choice(Rectangle Bounds, Point Offset, bool FromElement);

    /// <summary>
    /// Chọn vùng làm hình mẫu: khung phần tử UI (nếu có) rồi các vùng lớn dần quanh điểm click; lấy vùng đầu tiên đủ chi tiết,
    /// không bị cửa sổ khác che (<paramref name="excluded"/>) và không lẫn với chỗ khác trong ảnh chụp.
    /// null = không vùng nào đủ tin cậy (click giữ theo tọa độ).
    /// </summary>
    /// <param name="shot">Ảnh chụp lúc nhấn chuột; mọi tọa độ khác tính theo ảnh này.</param>
    public static Choice? Choose(Bitmap shot, Point click, Rectangle? element, double scale, IReadOnlyCollection<Rectangle> excluded)
    {
        var all = new Rectangle(0, 0, shot.Width, shot.Height);
        if (!all.Contains(click)) return null;

        var candidates = new List<(Rectangle Rect, bool FromElement)>();
        if (element is { } e && e.Width >= Px(MinElement.Width, scale) && e.Height >= Px(MinElement.Height, scale)
            && e.Width <= Px(MaxElement.Width, scale) && e.Height <= Px(MaxElement.Height, scale))
        {
            // Lấy thêm viền ngoài một chút để có đường bao của nút.
            var r = Rectangle.Intersect(Rectangle.Inflate(e, Px(2, scale), Px(2, scale)), all);
            if (r.Contains(click)) candidates.Add((r, true));
        }
        foreach (var size in Patches)
        {
            int w = Math.Min(Px(size.Width, scale), all.Width), h = Math.Min(Px(size.Height, scale), all.Height);
            // Sát mép ảnh chụp: dịch vùng vào trong, điểm click không còn ở tâm (đã có độ lệch).
            var r = new Rectangle(Math.Clamp(click.X - w / 2, 0, all.Width - w), Math.Clamp(click.Y - h / 2, 0, all.Height - h), w, h);
            if (!candidates.Exists(c => c.Rect == r)) candidates.Add((r, false));
        }

        foreach (var (r, fromElement) in candidates)
        {
            if (r.Width < 8 || r.Height < 8 || excluded.Any(x => x.IntersectsWith(r))) continue;
            using var template = ScreenCapture.Crop(shot, r);
            if (ImageMatcher.IsLowDetail(template) || !IsDistinct(shot, template, r)) continue;
            return new Choice(r, new Point(click.X - (r.X + r.Width / 2), click.Y - (r.Y + r.Height / 2)), fromElement);
        }
        return null;
    }

    /// <summary>Hình mẫu chỉ khớp đúng chỗ của nó trong ảnh chụp — mọi chỗ khác đều kém hẳn.</summary>
    private static bool IsDistinct(Bitmap shot, Bitmap template, Rectangle own) =>
        ImageMatcher.FindAll(shot, template).Where(m => ImageMatcher.Overlap(m.Bounds, own) < 0.5).All(m => m.Score < AmbiguousScore);

    /// <summary>Lưu hình mẫu đã chọn vào bước click.</summary>
    public static void Apply(ActionStep step, Bitmap shot, Choice choice, double scale)
    {
        using var template = ScreenCapture.Crop(shot, choice.Bounds);
        step.ImageData = ScreenCapture.ToBase64Png(template);
        step.ImageWidth = template.Width;
        step.ImageHeight = template.Height;
        step.ImageScale = scale;
        step.ImageOffsetX = choice.Offset.X;
        step.ImageOffsetY = choice.Offset.Y;
        step.Confidence = DefaultConfidence;
    }

    /// <summary>
    /// Chọn chỗ khớp đạt ngưỡng tốt nhất; nhiều chỗ khớp gần như nhau (nút lặp lại trong danh sách, thanh công cụ…)
    /// thì lấy chỗ gần <paramref name="expectedCenter"/> (tâm hình mẫu theo vị trí lúc ghi) nhất. null = không chỗ nào đạt.
    /// </summary>
    public static MatchResult? Pick(IReadOnlyList<MatchResult> matches, double threshold, Point? expectedCenter)
    {
        var ok = matches.Where(m => m.Score >= threshold).ToList();
        if (ok.Count == 0) return null;
        double top = ok.Max(m => m.Score);
        var best = ok.Where(m => m.Score >= top - TieScore);
        return expectedCenter is { } c ? best.MinBy(m => Distance(Center(m.Bounds), c)) : best.MaxBy(m => m.Score);
    }

    public static Point Center(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    public static double Distance(Point a, Point b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    private static int Px(int logical, double scale) => (int)Math.Round(logical * scale);
}
