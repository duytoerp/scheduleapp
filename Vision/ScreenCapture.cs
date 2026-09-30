using System.Drawing.Imaging;

namespace ScheduleApp.Vision;

internal static class ScreenCapture
{
    /// <summary>Chụp một vùng màn hình (tọa độ vật lý, hỗ trợ nhiều màn hình).</summary>
    public static Bitmap Capture(Rectangle area)
    {
        var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    public static Rectangle VirtualScreen => SystemInformation.VirtualScreen;

    /// <summary>Cắt một vùng của ảnh với kích thước pixel chính xác (không phụ thuộc DPI của ảnh).</summary>
    public static Bitmap Crop(Bitmap source, Rectangle area)
    {
        var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.DrawImage(source, new Rectangle(0, 0, area.Width, area.Height), area, GraphicsUnit.Pixel);
        return bmp;
    }

    public static string ToBase64Png(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }

    public static Bitmap FromBase64Png(string data)
    {
        using var ms = new MemoryStream(Convert.FromBase64String(data));
        using var loaded = new Bitmap(ms);
        // Sao chép để không phụ thuộc stream đã đóng.
        return Crop(loaded, new Rectangle(0, 0, loaded.Width, loaded.Height));
    }
}
