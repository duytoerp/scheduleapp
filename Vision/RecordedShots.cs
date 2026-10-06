using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using ScheduleApp.Native;
using ScheduleApp.Services;

namespace ScheduleApp.Vision;

/// <summary>
/// Ảnh cả cửa sổ ứng dụng lúc ghi một click — chỉ để xem lại bước (biết đã click vào đâu), không dùng khi chạy.
/// Lưu thành file JPEG trong %AppData%\ScheduleApp\recorded-shots (không nhét vào jobs.json → file công việc không phình,
/// ảnh không bị gửi kèm khi nhờ AI sửa flow); bước chỉ giữ tên file.
/// </summary>
internal static partial class RecordedShots
{
    /// <summary>Cạnh dài nhất của ảnh lưu (px) — đủ rõ để nhận ra chỗ click, mỗi ảnh chỉ vài chục KB.</summary>
    internal const int MaxSide = 1600;

    private const long JpegQuality = 80;

    /// <summary>Ảnh không còn bước nào dùng (kể cả trong phiên bản cũ) quá số ngày này thì xóa.</summary>
    internal static readonly TimeSpan KeepUnused = TimeSpan.FromDays(14);

    public static string Dir => Path.Combine(JobStore.DataDir, "recorded-shots");

    /// <summary>Tên do app tạo (32 ký tự hex + .jpg) — tên lạ từ file công việc nhập vào (vd "..\..\x") không bao giờ được mở.</summary>
    [GeneratedRegex(@"^[0-9a-f]{32}\.jpg$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"[0-9a-f]{32}\.jpg")]
    private static partial Regex NameInText();

    public static bool IsValidName(string? name) => name != null && NamePattern().IsMatch(name);

    /// <summary>Vùng của cửa sổ trên màn hình: khung nhìn thấy (bỏ viền trong suốt), cắt theo màn hình; rỗng nếu không chụp được.</summary>
    public static Rectangle WindowArea(IntPtr root)
    {
        Rectangle r = Win32.DwmGetWindowAttribute(root, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT b, System.Runtime.InteropServices.Marshal.SizeOf<Win32.RECT>()) == 0
            ? Rectangle.FromLTRB(b.Left, b.Top, b.Right, b.Bottom)
            : WindowHelper.GetRect(root);
        return Rectangle.Intersect(r, ScreenCapture.VirtualScreen);
    }

    /// <summary>
    /// Lưu ảnh (thu nhỏ nếu lớn hơn <see cref="MaxSide"/>), trả về tên file và điểm click đã quy theo ảnh lưu.
    /// <paramref name="click"/> tính theo <paramref name="image"/>.
    /// </summary>
    public static (string Name, Point Click) Save(Bitmap image, Point click)
    {
        double k = Math.Min(1.0, (double)MaxSide / Math.Max(image.Width, image.Height));
        var size = new Size(Math.Max(1, (int)Math.Round(image.Width * k)), Math.Max(1, (int)Math.Round(image.Height * k)));
        Directory.CreateDirectory(Dir);
        string name = Guid.NewGuid().ToString("N") + ".jpg";
        using (var scaled = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, new Rectangle(Point.Empty, size));
            }
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var options = new EncoderParameters(1);
            options.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
            scaled.Save(Path.Combine(Dir, name), codec, options);
        }
        return (name, new Point((int)Math.Round(click.X * k), (int)Math.Round(click.Y * k)));
    }

    /// <summary>Đọc ảnh vào bộ nhớ (không giữ khóa file); null nếu tên không hợp lệ / file đã bị xóa / hỏng.</summary>
    public static Bitmap? Load(string? name)
    {
        if (!IsValidName(name)) return null;
        try
        {
            var path = Path.Combine(Dir, name!);
            if (!File.Exists(path)) return null;
            using var ms = new MemoryStream(File.ReadAllBytes(path));
            using var loaded = new Bitmap(ms);
            return new Bitmap(loaded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Xóa ảnh không còn được nhắc tới trong jobs.json hay các phiên bản cũ (versions\…) và đã cũ hơn <see cref="KeepUnused"/>
    /// (ảnh của lần ghi chưa lưu / công việc đã xóa). Trả về số ảnh đã xóa.
    /// </summary>
    public static int Cleanup() => Cleanup(Dir, JobStore.DataDir, DateTime.UtcNow);

    internal static int Cleanup(string shotsDir, string dataDir, DateTime nowUtc)
    {
        if (!Directory.Exists(shotsDir)) return 0;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<string> { Path.Combine(dataDir, "jobs.json") };
        var versions = Path.Combine(dataDir, "versions");
        if (Directory.Exists(versions)) sources.AddRange(Directory.EnumerateFiles(versions, "*.json", SearchOption.AllDirectories));
        foreach (var file in sources)
        {
            // Không đọc được một nguồn (đang ghi, bị khóa…) → không xóa gì cả cho chắc.
            if (!File.Exists(file)) continue;
            try { foreach (Match m in NameInText().Matches(File.ReadAllText(file))) used.Add(m.Value); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }
        int deleted = 0;
        foreach (var path in Directory.EnumerateFiles(shotsDir, "*.jpg"))
        {
            var name = Path.GetFileName(path);
            if (!IsValidName(name) || used.Contains(name)) continue;
            try
            {
                if (nowUtc - File.GetLastWriteTimeUtc(path) < KeepUnused) continue;
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return deleted;
    }
}
