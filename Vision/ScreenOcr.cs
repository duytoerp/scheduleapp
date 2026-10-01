using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ScheduleApp.Vision;

internal sealed record TextMatch(Rectangle Bounds, string LineText);

/// <summary>Nhận dạng chữ trên ảnh màn hình bằng Windows OCR (có sẵn trong Windows 10/11).</summary>
internal static class ScreenOcr
{
    private static OcrEngine? _engine;
    private static readonly object Sync = new();

    /// <summary>Ngôn ngữ OCR đang dùng (ưu tiên tiếng Việt nếu đã cài gói).</summary>
    public static string LanguageName => Engine.RecognizerLanguage.DisplayName;

    public static bool VietnameseAvailable => OcrEngine.IsLanguageSupported(new Language("vi"));

    private static OcrEngine Engine
    {
        get
        {
            lock (Sync) return _engine ??= CreateEngine();
        }
    }

    private static OcrEngine CreateEngine()
    {
        foreach (var tag in new[] { "vi-VN", "vi" })
        {
            var lang = new Language(tag);
            if (OcrEngine.IsLanguageSupported(lang)) return OcrEngine.TryCreateFromLanguage(lang);
        }
        return OcrEngine.TryCreateFromUserProfileLanguages()
               ?? (OcrEngine.AvailableRecognizerLanguages.Count > 0
                   ? OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0])
                   : null)
               ?? throw new InvalidOperationException("Windows chưa cài gói nhận dạng chữ (OCR) cho ngôn ngữ nào.");
    }

    /// <summary>
    /// Tìm mọi vị trí chứa <paramref name="query"/> (so sánh bỏ dấu, không phân biệt hoa thường, bỏ khoảng trắng/dấu câu).
    /// Trả về cả toàn bộ chữ đọc được để chẩn đoán khi không tìm thấy.
    /// </summary>
    public static async Task<(List<TextMatch> Matches, string AllText)> FindAsync(Bitmap image, string query)
    {
        var engine = Engine;
        // Phóng to 2 lần giúp đọc chữ giao diện cỡ nhỏ chính xác hơn.
        double scale = Math.Max(image.Width, image.Height) * 2 <= OcrEngine.MaxImageDimension ? 2.0 : 1.0;
        using var scaled = scale == 1.0 ? ScreenCapture.Crop(image, new Rectangle(0, 0, image.Width, image.Height)) : Resize(image, scale);
        var result = await engine.RecognizeAsync(await ToSoftwareBitmapAsync(scaled));

        var target = Squash(query);
        var matches = new List<TextMatch>();
        if (target.Length == 0) return (matches, result.Text);

        foreach (var line in result.Lines)
        {
            var words = line.Words;
            var chars = new StringBuilder();
            var owner = new List<int>();
            for (int w = 0; w < words.Count; w++)
                foreach (var c in Squash(words[w].Text))
                {
                    chars.Append(c);
                    owner.Add(w);
                }

            var hay = chars.ToString();
            foreach (var (idx, length) in FindInLine(hay, target))
            {
                int first = owner[idx], last = owner[idx + length - 1];
                double left = double.MaxValue, top = double.MaxValue, right = 0, bottom = 0;
                for (int w = first; w <= last; w++)
                {
                    var r = words[w].BoundingRect;
                    left = Math.Min(left, r.X);
                    top = Math.Min(top, r.Y);
                    right = Math.Max(right, r.X + r.Width);
                    bottom = Math.Max(bottom, r.Y + r.Height);
                }
                var bounds = Rectangle.FromLTRB(
                    (int)(left / scale), (int)(top / scale), (int)Math.Ceiling(right / scale), (int)Math.Ceiling(bottom / scale));
                matches.Add(new TextMatch(bounds, line.Text));
            }
        }
        return (matches, result.Text);
    }

    /// <summary>
    /// Vị trí (đầu, độ dài) của <paramref name="target"/> trong một dòng. Ưu tiên khớp chính xác;
    /// nếu không có và chuỗi đủ dài (≥ 5 ký tự) thì cho phép sai khoảng 1 ký tự trên 5 —
    /// OCR hay đọc nhầm chữ có dấu (vd "Đăng nhập" → "Däng nhöp").
    /// </summary>
    private static List<(int Index, int Length)> FindInLine(string hay, string target)
    {
        var result = new List<(int, int)>();
        for (int idx = hay.IndexOf(target, StringComparison.Ordinal); idx >= 0;
             idx = hay.IndexOf(target, idx + target.Length, StringComparison.Ordinal))
            result.Add((idx, target.Length));
        if (result.Count > 0 || target.Length < 5) return result;

        int maxErrors = Math.Max(1, target.Length / 5);
        var candidates = new List<(int Index, int Length, int Distance)>();
        for (int start = 0; start < hay.Length; start++)
        {
            (int Length, int Distance)? best = null;
            for (int len = target.Length - maxErrors; len <= target.Length + maxErrors; len++)
            {
                if (len <= 0 || start + len > hay.Length) continue;
                int d = Levenshtein(hay.AsSpan(start, len), target);
                if (d <= maxErrors && (best == null || d < best.Value.Distance)) best = (len, d);
            }
            if (best != null) candidates.Add((start, best.Value.Length, best.Value.Distance));
        }

        // Chọn các vị trí tốt nhất, không chồng lấn nhau.
        foreach (var c in candidates.OrderBy(c => c.Distance).ThenBy(c => c.Index))
            if (result.All(r => c.Index + c.Length <= r.Item1 || r.Item1 + r.Item2 <= c.Index))
                result.Add((c.Index, c.Length));
        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }

    private static int Levenshtein(ReadOnlySpan<char> a, string b)
    {
        Span<int> prev = stackalloc int[b.Length + 1];
        Span<int> cur = stackalloc int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            cur.CopyTo(prev);
        }
        return prev[b.Length];
    }

    /// <summary>Đọc toàn bộ chữ trên ảnh, mỗi dòng OCR một dòng.</summary>
    public static async Task<string> ReadTextAsync(Bitmap image)
    {
        double scale = Math.Max(image.Width, image.Height) * 2 <= OcrEngine.MaxImageDimension ? 2.0 : 1.0;
        using var scaled = scale == 1.0 ? ScreenCapture.Crop(image, new Rectangle(0, 0, image.Width, image.Height)) : Resize(image, scale);
        var result = await Engine.RecognizeAsync(await ToSoftwareBitmapAsync(scaled));
        return string.Join("\n", result.Lines.Select(l => l.Text));
    }

    /// <summary>Bỏ dấu tiếng Việt (giữ nguyên chữ hoa/thường, khoảng trắng và dấu câu).</summary>
    public static string RemoveDiacritics(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch { 'đ' => 'd', 'Đ' => 'D', _ => c });
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Chuẩn hóa để so khớp: bỏ dấu tiếng Việt, chữ thường, chỉ giữ chữ và số.</summary>
    public static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            char ch = c is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(c);
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    private static Bitmap Resize(Bitmap src, double scale)
    {
        var bmp = new Bitmap((int)(src.Width * scale), (int)(src.Height * scale), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(0, 0, bmp.Width, bmp.Height), new Rectangle(0, 0, src.Width, src.Height), GraphicsUnit.Pixel);
        return bmp;
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;
        using var stream = ms.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
