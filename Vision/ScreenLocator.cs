using System.Diagnostics;
using ScheduleApp.Models;
using ScheduleApp.Native;

namespace ScheduleApp.Vision;

/// <param name="Found">Tìm thấy và đạt ngưỡng.</param>
/// <param name="Bounds">Vùng tìm thấy (tọa độ màn hình).</param>
/// <param name="ClickPoint">Tâm vùng tìm thấy cộng độ lệch X/Y của bước.</param>
/// <param name="Detail">Mô tả cho log (độ khớp, số vị trí, chữ đọc được…).</param>
internal sealed record LocateResult(bool Found, Rectangle Bounds, Point ClickPoint, string Detail);

/// <summary>Tìm hình mẫu hoặc chữ trên màn hình cho các bước nhận dạng.</summary>
internal static class ScreenLocator
{
    private const int PollIntervalMs = 400;

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
    public static async Task<LocateResult> LocateOnceAsync(ActionStep s, Rectangle area, Bitmap? template)
    {
        using var shot = ScreenCapture.Capture(area);

        if (s.IsImageStep)
        {
            if (template == null) throw new InvalidOperationException("Chưa chụp hình mẫu.");
            var m = await Task.Run(() => ImageMatcher.FindBest(shot, template));
            if (m == null) return new LocateResult(false, Rectangle.Empty, Point.Empty, "hình mẫu lớn hơn vùng tìm");
            var bounds = Offset(m.Bounds, area.Location);
            bool ok = m.Score * 100 >= s.Confidence;
            return new LocateResult(ok, bounds, ClickPoint(bounds, s), $"độ khớp {m.Score * 100:0}% (ngưỡng {s.Confidence}%)");
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

    /// <summary>
    /// Hình mẫu của bước; nếu màn hình tìm kiếm có mức scale khác lúc chụp (vd chụp ở 100%, chạy ở 125%)
    /// thì hình mẫu được phóng/thu theo tỉ lệ tương ứng.
    /// </summary>
    public static Bitmap? LoadTemplate(ActionStep s, Rectangle area)
    {
        if (!s.IsImageStep || string.IsNullOrEmpty(s.ImageData)) return null;
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

    private static Rectangle Offset(Rectangle r, Point by) => new(r.X + by.X, r.Y + by.Y, r.Width, r.Height);

    private static Point ClickPoint(Rectangle r, ActionStep s) =>
        new(r.X + r.Width / 2 + s.X, r.Y + r.Height / 2 + s.Y);
}
