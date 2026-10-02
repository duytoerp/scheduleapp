using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ScheduleApp.Vision;

internal sealed record MatchResult(Rectangle Bounds, double Score);

/// <summary>
/// Tìm hình mẫu trong ảnh chụp màn hình bằng tương quan chéo chuẩn hóa (NCC) trên ảnh xám —
/// không bị ảnh hưởng bởi thay đổi độ sáng đồng đều. Tìm thô trên ảnh thu nhỏ rồi tinh chỉnh ở độ phân giải gốc.
/// </summary>
internal static class ImageMatcher
{
    private const int Candidates = 12;

    /// <summary>
    /// Vị trí khớp nhất, hoặc null nếu hình mẫu lớn hơn vùng tìm.
    /// Score (0..1) = độ khớp hình dạng (NCC) × độ khớp màu — nút cùng hình nhưng khác màu (vd nút bị mờ) sẽ bị điểm thấp.
    /// </summary>
    public static MatchResult? FindBest(Bitmap haystack, Bitmap needle) => FindAll(haystack, needle).FirstOrDefault();

    /// <summary>
    /// Các vị trí giống hình mẫu nhất (tối đa 12, không chồng lên nhau quá nửa), độ khớp giảm dần; rỗng nếu hình mẫu lớn hơn vùng tìm.
    /// Hình mẫu lớn chỉ giữ các vị trí có hình dạng kém vị trí tốt nhất không quá 0,15 — vẫn đủ để nhận ra chỗ trùng lặp.
    /// </summary>
    public static List<MatchResult> FindAll(Bitmap haystack, Bitmap needle)
    {
        var hay = GrayImage.From(haystack);
        var tpl = GrayImage.From(needle);
        if (tpl.W < 2 || tpl.H < 2 || tpl.W > hay.W || tpl.H > hay.H) return [];

        // 1) Lấy các ứng viên theo hình dạng; hình mẫu đủ lớn thì tìm thô trên ảnh thu nhỏ k lần cho nhanh.
        int k = Math.Clamp(Math.Min(tpl.W, tpl.H) / 10, 1, 8);
        List<(int X, int Y, float Score)> candidates;
        int radius;
        if (k == 1)
        {
            var map = ScoreMap(hay, tpl, out int mw, out int mh);
            candidates = TopCandidates(map, mw, mh, tpl.W, tpl.H, Candidates);
            radius = 0;
        }
        else
        {
            var ts = tpl.Downscale(k);
            var coarse = ScoreMap(hay.Downscale(k), ts, out int cw, out int ch);
            candidates = TopCandidates(coarse, cw, ch, ts.W, ts.H, Candidates).Select(c => (c.X * k, c.Y * k, c.Score)).ToList();
            radius = k + 1;
            // Bỏ các ứng viên kém xa ứng viên tốt nhất để phần tinh chỉnh (tốn kém với hình mẫu lớn) chạy ít lần.
            float top = candidates.Count > 0 ? candidates[0].Score : 0;
            candidates = candidates.Where(c => c.Score >= top - 0.15f).ToList();
        }

        // 2) Tinh chỉnh vị trí ở độ phân giải gốc, rồi chấm thêm điểm màu.
        var stats = new TemplateStats(tpl);
        var integral = new Integral(hay);
        var results = new List<MatchResult>();
        foreach (var (cx, cy, _) in candidates)
        {
            int bx = cx, by = cy;
            double bestShape = double.MinValue;
            for (int y = Math.Max(0, cy - radius); y <= Math.Min(hay.H - tpl.H, cy + radius); y++)
                for (int x = Math.Max(0, cx - radius); x <= Math.Min(hay.W - tpl.W, cx + radius); x++)
                {
                    double s = Score(hay, stats, integral, x, y);
                    if (s > bestShape) (bestShape, bx, by) = (s, x, y);
                }
            double score = Math.Max(0, bestShape) * ColorScore(hay, tpl, bx, by);
            results.Add(new MatchResult(new Rectangle(bx, by, tpl.W, tpl.H), score));
        }

        // Tinh chỉnh có thể đưa hai ứng viên về cùng một chỗ → giữ cái khớp hơn.
        var distinct = new List<MatchResult>();
        foreach (var r in results.OrderByDescending(r => r.Score))
            if (distinct.All(d => Overlap(d.Bounds, r.Bounds) < 0.5)) distinct.Add(r);
        return distinct;
    }

    /// <summary>Tỉ lệ diện tích chồng nhau của hai vùng cùng kích thước (0..1).</summary>
    public static double Overlap(Rectangle a, Rectangle b)
    {
        var i = Rectangle.Intersect(a, b);
        return i.IsEmpty ? 0 : (double)i.Width * i.Height / Math.Max(1, Math.Min(a.Width * a.Height, b.Width * b.Height));
    }

    /// <summary>1 − sai lệch RGB trung bình / 255 giữa hình mẫu và vùng tại (x, y).</summary>
    private static double ColorScore(GrayImage hay, GrayImage tpl, int x, int y)
    {
        long diff = 0;
        for (int ty = 0; ty < tpl.H; ty++)
        {
            int h = (y + ty) * hay.Stride + x * 4, t = ty * tpl.Stride;
            for (int tx = 0; tx < tpl.W; tx++, h += 4, t += 4)
                diff += Math.Abs(hay.Bgra[h] - tpl.Bgra[t]) + Math.Abs(hay.Bgra[h + 1] - tpl.Bgra[t + 1]) + Math.Abs(hay.Bgra[h + 2] - tpl.Bgra[t + 2]);
        }
        return 1 - diff / (3.0 * 255 * tpl.W * tpl.H);
    }

    /// <summary>Hình mẫu gần như một màu (ít chi tiết) — dễ khớp nhầm với vùng trống khác trên màn hình.</summary>
    public static bool IsLowDetail(Bitmap bmp)
    {
        var g = GrayImage.From(bmp);
        var stats = new TemplateStats(g);
        return stats.Norm / Math.Sqrt(g.W * g.H) < 8; // độ lệch chuẩn độ sáng < 8/255
    }

    private static float[] ScoreMap(GrayImage hay, GrayImage tpl, out int mw, out int mh)
    {
        int w = mw = hay.W - tpl.W + 1;
        mh = hay.H - tpl.H + 1;
        var map = new float[mw * mh];
        var stats = new TemplateStats(tpl);
        var integral = new Integral(hay);
        Parallel.For(0, mh, y =>
        {
            for (int x = 0; x < w; x++) map[y * w + x] = (float)Score(hay, stats, integral, x, y);
        });
        return map;
    }

    private static double Score(GrayImage hay, TemplateStats t, Integral integral, int x, int y)
    {
        int n = t.W * t.H;
        var (sum, sumSq) = integral.Window(x, y, t.W, t.H);
        double varI = sumSq - sum * sum / n;

        if (t.Norm < 1e-3)
        {
            // Hình mẫu một màu: so độ sáng trung bình và độ đồng đều.
            double meanI = sum / n, std = Math.Sqrt(Math.Max(0, varI) / n);
            return Math.Max(0, 1 - (Math.Abs(meanI - t.Mean) + std) / 255.0);
        }
        if (varI <= 1e-6) return 0;

        var p = hay.P;
        var tz = t.Zero;
        int vec = Vector<float>.Count;
        double acc = 0;
        for (int ty = 0; ty < t.H; ty++)
        {
            int row = (y + ty) * hay.W + x, ti = ty * t.W, tx = 0;
            var vacc = Vector<float>.Zero;
            for (; tx <= t.W - vec; tx += vec)
                vacc += new Vector<float>(tz, ti + tx) * new Vector<float>(p, row + tx);
            float rowAcc = Vector.Dot(vacc, Vector<float>.One);
            for (; tx < t.W; tx++) rowAcc += tz[ti + tx] * p[row + tx];
            acc += rowAcc;
        }
        return acc / (t.Norm * Math.Sqrt(varI));
    }

    private static int ArgMax(float[] a)
    {
        int best = 0;
        for (int i = 1; i < a.Length; i++) if (a[i] > a[best]) best = i;
        return best;
    }

    /// <summary>Lấy các đỉnh cao nhất, loại bỏ các điểm quá gần nhau (non-maximum suppression).</summary>
    private static List<(int X, int Y, float Score)> TopCandidates(float[] map, int w, int h, int tw, int th, int count)
    {
        var result = new List<(int, int, float)>();
        var work = (float[])map.Clone();
        for (int c = 0; c < count; c++)
        {
            int i = ArgMax(work);
            if (work[i] <= -1f) break;
            int cx = i % w, cy = i / w;
            result.Add((cx, cy, work[i]));
            for (int y = Math.Max(0, cy - th / 2); y <= Math.Min(h - 1, cy + th / 2); y++)
                for (int x = Math.Max(0, cx - tw / 2); x <= Math.Min(w - 1, cx + tw / 2); x++)
                    work[y * w + x] = -2f;
        }
        return result;
    }

    private sealed class GrayImage(int w, int h, float[] p, byte[] bgra, int stride)
    {
        public int W { get; } = w;
        public int H { get; } = h;
        public float[] P { get; } = p;

        /// <summary>Điểm ảnh màu gốc (chỉ có ở ảnh độ phân giải gốc, rỗng ở ảnh thu nhỏ).</summary>
        public byte[] Bgra { get; } = bgra;
        public int Stride { get; } = stride;

        public static GrayImage From(Bitmap bmp)
        {
            // Bitmap.Width/Height là lời gọi GDI+ — lưu ra biến, không gọi trong vòng lặp pixel.
            int w = bmp.Width, h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                var bytes = new byte[stride * h];
                Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                var p = new float[w * h];
                for (int y = 0; y < h; y++)
                {
                    int src = y * stride, dst = y * w;
                    for (int x = 0; x < w; x++, src += 4)
                        p[dst + x] = 0.114f * bytes[src] + 0.587f * bytes[src + 1] + 0.299f * bytes[src + 2];
                }
                return new GrayImage(w, h, p, bytes, stride);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        public GrayImage Downscale(int k)
        {
            int w = W / k, h = H / k;
            var p = new float[w * h];
            float inv = 1f / (k * k);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    for (int dy = 0; dy < k; dy++)
                    {
                        int row = (y * k + dy) * W + x * k;
                        for (int dx = 0; dx < k; dx++) s += P[row + dx];
                    }
                    p[y * w + x] = s * inv;
                }
            return new GrayImage(w, h, p, [], 0);
        }
    }

    private sealed class TemplateStats
    {
        public int W { get; }
        public int H { get; }
        public float[] Zero { get; }
        public double Mean { get; }
        public double Norm { get; }

        public TemplateStats(GrayImage t)
        {
            W = t.W;
            H = t.H;
            Mean = t.P.Average(v => (double)v);
            Zero = t.P.Select(v => (float)(v - Mean)).ToArray();
            Norm = Math.Sqrt(Zero.Sum(v => (double)v * v));
        }
    }

    /// <summary>Ảnh tích phân của tổng và tổng bình phương để tính trung bình/phương sai cửa sổ trong O(1).</summary>
    private sealed class Integral
    {
        private readonly double[] _s, _ss;
        private readonly int _stride;

        public Integral(GrayImage img)
        {
            _stride = img.W + 1;
            _s = new double[_stride * (img.H + 1)];
            _ss = new double[_stride * (img.H + 1)];
            for (int y = 0; y < img.H; y++)
            {
                double rs = 0, rss = 0;
                for (int x = 0; x < img.W; x++)
                {
                    double v = img.P[y * img.W + x];
                    rs += v;
                    rss += v * v;
                    int i = (y + 1) * _stride + x + 1;
                    _s[i] = _s[i - _stride] + rs;
                    _ss[i] = _ss[i - _stride] + rss;
                }
            }
        }

        public (double Sum, double SumSq) Window(int x, int y, int w, int h)
        {
            int a = y * _stride + x, b = a + w, c = (y + h) * _stride + x, d = c + w;
            return (_s[d] - _s[b] - _s[c] + _s[a], _ss[d] - _ss[b] - _ss[c] + _ss[a]);
        }
    }
}
