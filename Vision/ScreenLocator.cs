using System.Diagnostics;
using ScheduleApp.Models;
using ScheduleApp.Native;

namespace ScheduleApp.Vision;

/// <param name="Found">Tìm thấy và đạt ngưỡng.</param>
/// <param name="Bounds">Vùng tìm thấy (tọa độ màn hình).</param>
/// <param name="ClickPoint">Tâm vùng tìm thấy cộng độ lệch X/Y của bước.</param>
/// <param name="Detail">Mô tả cho log (độ khớp, số vị trí, chữ đọc được…).</param>
/// <param name="Score">Độ khớp của hình mẫu (0..1) ở vị trí tìm được.</param>
internal sealed record LocateResult(bool Found, Rectangle Bounds, Point ClickPoint, string Detail, double Score = 0);

/// <summary>Tìm hình mẫu hoặc chữ trên màn hình cho các bước nhận dạng.</summary>
internal static class ScreenLocator
{
    private const int PollIntervalMs = 400;

    /// <summary>Chờ hiệu ứng rê chuột (hover) của nút hiện ra sau khi đưa chuột tới.</summary>
    private const int HoverMs = 150;

    /// <summary>Tìm lặp lại cho tới khi thấy hoặc hết timeout của bước.</summary>
    public static async Task<LocateResult> WaitAsync(ActionStep s, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        IntPtr window = IntPtr.Zero;
        if (!string.IsNullOrWhiteSpace(s.Target))
        {
            window = await WindowHelper.WaitForAsync(s.Target, Math.Max(1000, s.DelayMs), ct);
            WindowHelper.Focus(window);
        }
        using var template = LoadTemplate(s, AreaOf(window));

        while (true)
        {
            var result = await LocateOnceAsync(s, AreaOf(window), template);
            if (result.Found || sw.ElapsedMilliseconds >= s.DelayMs) return result;
            await Task.Delay(PollIntervalMs, ct);
        }
    }

    /// <summary>Tìm một lần trên vùng cho trước (dùng cho nút "Thử tìm").</summary>
    /// <param name="recorded">Click đã ghi kèm hình mẫu: điểm click lúc ghi (tọa độ màn hình) — nhiều chỗ giống nhau thì chọn chỗ gần nó nhất.</param>
    public static async Task<LocateResult> LocateOnceAsync(ActionStep s, Rectangle area, Bitmap? template, Point? recorded = null)
    {
        using var shot = ScreenCapture.Capture(area);

        if (s.IsImageStep || s.HasImageAnchor)
        {
            if (template == null) throw new InvalidOperationException("Chưa chụp hình mẫu.");
            return await Task.Run(() => MatchImage(s, shot, area.Location, template, recorded));
        }

        if (string.IsNullOrWhiteSpace(s.Text)) throw new InvalidOperationException("Chưa nhập chữ cần tìm.");
        var (matches, allText) = await ScreenOcr.FindAsync(shot, s.Text);
        int index = Math.Max(1, s.MatchIndex) - 1;
        if (matches.Count <= index)
        {
            var read = allText.Replace("\r", " ").Replace("\n", " ");
            if (read.Length > 160) read = read[..160] + "…";
            var detail = matches.Count == 0
                ? $"OCR {ScreenOcr.LanguageName} đọc được: \"{read}\""
                : $"chỉ có {matches.Count} vị trí khớp, cần vị trí thứ {index + 1}";
            return new LocateResult(false, Rectangle.Empty, Point.Empty, detail);
        }
        var found = Offset(matches[index].Bounds, area.Location);
        return new LocateResult(true, found, ClickPoint(found, s),
            $"{matches.Count} vị trí khớp, dòng \"{matches[index].LineText}\"");
    }

    /// <summary>Tìm hình mẫu trong ảnh chụp vùng bắt đầu tại <paramref name="origin"/> (tọa độ màn hình); kết quả theo tọa độ màn hình.</summary>
    internal static LocateResult MatchImage(ActionStep s, Bitmap shot, Point origin, Bitmap template, Point? recorded)
    {
        var hits = ImageMatcher.FindAll(shot, template);
        if (hits.Count == 0) return new LocateResult(false, Rectangle.Empty, Point.Empty, "hình mẫu lớn hơn vùng tìm");

        // Hình mẫu đã co giãn theo scale màn hình → độ lệch của điểm click co giãn theo.
        double ratio = s.ImageWidth > 0 ? template.Width / (double)s.ImageWidth : 1;
        var offset = s.HasImageAnchor
            ? new Size((int)Math.Round(s.ImageOffsetX * ratio), (int)Math.Round(s.ImageOffsetY * ratio))
            : new Size(s.X, s.Y);
        Point? expected = recorded is { } r ? new Point(r.X - offset.Width - origin.X, r.Y - offset.Height - origin.Y) : null;

        double threshold = s.Confidence / 100.0;
        var picked = ClickAnchor.Pick(hits, threshold, expected);
        var m = picked ?? hits[0];
        var bounds = Offset(m.Bounds, origin);
        int similar = picked == null ? 0 : hits.Count(x => x.Score >= threshold && x.Score >= hits[0].Score - ClickAnchor.TieScore);
        var detail = $"độ khớp {m.Score * 100:0}% (ngưỡng {s.Confidence}%)" +
                     (similar > 1 && expected != null ? $", {similar} chỗ giống nhau → chọn chỗ gần vị trí lúc ghi nhất" : "");
        return new LocateResult(picked != null, bounds, ClickAnchor.Center(bounds) + offset, detail, m.Score);
    }

    /// <summary>
    /// Hình mẫu của bước; nếu màn hình tìm kiếm có mức scale khác lúc chụp (vd chụp ở 100%, chạy ở 125%)
    /// thì hình mẫu được phóng/thu theo tỉ lệ tương ứng.
    /// </summary>
    public static Bitmap? LoadTemplate(ActionStep s, Rectangle area)
    {
        if (!(s.IsImageStep || s.HasImageAnchor) || string.IsNullOrEmpty(s.ImageData)) return null;
        var template = ScreenCapture.FromBase64Png(s.ImageData);
        if (s.ImageScale <= 0) return template;

        double now = PowerHelper.ScaleAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2));
        double ratio = now / s.ImageScale;
        if (Math.Abs(ratio - 1) < 0.02) return template;

        int w = Math.Max(2, (int)Math.Round(template.Width * ratio)), h = Math.Max(2, (int)Math.Round(template.Height * ratio));
        var scaled = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(template, new Rectangle(0, 0, w, h), new Rectangle(0, 0, template.Width, template.Height), GraphicsUnit.Pixel);
        }
        template.Dispose();
        Services.Log.Info($"      Hình mẫu chụp ở scale {s.ImageScale:P0}, màn hình hiện tại {now:P0} → co giãn {ratio:0.##}×.");
        return scaled;
    }

    /// <summary>Vùng tìm: cửa sổ (cắt theo màn hình) hoặc toàn bộ màn hình ảo.</summary>
    public static Rectangle AreaOf(IntPtr window)
    {
        var screen = ScreenCapture.VirtualScreen;
        if (window == IntPtr.Zero) return screen;
        var rect = Rectangle.Intersect(WindowHelper.GetRect(window), screen);
        return rect.Width > 0 && rect.Height > 0 ? rect : screen;
    }

    /// <summary>Vùng tìm cho click đã ghi: cửa sổ đích cùng các popup đang mở của nó (menu, danh sách thả xuống… có thể tràn ra ngoài cửa sổ).</summary>
    private static Rectangle AnchorAreaOf(IntPtr window)
    {
        var area = AreaOf(window);
        if (window == IntPtr.Zero) return area;
        foreach (var popup in WindowHelper.ProcessPopups(window))
        {
            var r = Rectangle.Intersect(WindowHelper.GetRect(popup), ScreenCapture.VirtualScreen);
            if (r.Width > 0 && r.Height > 0) area = Rectangle.Union(area, r);
        }
        return area;
    }

    private static Rectangle Offset(Rectangle r, Point by) => new(r.X + by.X, r.Y + by.Y, r.Width, r.Height);

    private static Point ClickPoint(Rectangle r, ActionStep s) =>
        new(r.X + r.Width / 2 + s.X, r.Y + r.Height / 2 + s.Y);

    /// <summary>
    /// Tìm lại chỗ đã click lúc ghi theo hình mẫu đi kèm, chờ tối đa <paramref name="timeoutMs"/>.
    /// Đưa chuột tới vị trí lúc ghi trước: lúc ghi chỗ đó đang được rê chuột (hover) nên hình mẫu khớp nhất khi nó ở cùng trạng thái.
    /// Chỗ giống nhất gần đạt ngưỡng (thường chỉ khác hiệu ứng hover vì nút đã dời chỗ) → rê chuột lên đó rồi xem lại.
    /// </summary>
    /// <param name="window">Cửa sổ đích (vùng tìm); Zero = cả màn hình.</param>
    /// <param name="recorded">Điểm click theo tọa độ lúc ghi (tọa độ màn hình); null = không có (hình mẫu tự chụp cho bước click phần tử).</param>
    public static async Task<LocateResult> WaitAnchorAsync(ActionStep s, IntPtr window, Point? recorded, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var template = LoadTemplate(s, AnchorAreaOf(window)) ?? throw new InvalidOperationException("Bước không có hình mẫu.");
        var hovered = recorded ?? Cursor.Position;
        if (recorded is { } r)
        {
            InputSimulator.MoveTo(r.X, r.Y);
            await Task.Delay(HoverMs, ct);
        }

        int hoverTries = 0;
        while (true)
        {
            var result = await LocateOnceAsync(s, AnchorAreaOf(window), template, recorded);
            if (result.Found) return result;
            if (result.Bounds.Width > 0 && result.Score * 100 >= s.Confidence - 15 && hoverTries < 3 &&
                ClickAnchor.Distance(result.ClickPoint, hovered) > 3)
            {
                hoverTries++;
                hovered = result.ClickPoint;
                InputSimulator.MoveTo(hovered.X, hovered.Y);
                await Task.Delay(HoverMs, ct);
                continue;
            }
            if (sw.ElapsedMilliseconds >= timeoutMs) return result;
            await Task.Delay(PollIntervalMs, ct);
        }
    }
}
