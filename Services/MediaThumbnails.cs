using System.Diagnostics;
using System.Drawing;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace ScheduleApp.Services;

/// <summary>
/// Ảnh thu nhỏ của video / nhạc giống Explorer: lấy từ bộ tạo ảnh thu nhỏ của Windows (khung hình video, ảnh bìa bài hát…)
/// và dùng chung bộ nhớ đệm ảnh thu nhỏ của Explorer nên nhanh. File không có ảnh thu nhỏ → biểu tượng của loại file.
/// </summary>
internal static class MediaThumbnails
{
    /// <param name="Image">Ảnh — người gọi giữ và tự Dispose.</param>
    /// <param name="IsIcon">File không có ảnh thu nhỏ, đây là biểu tượng của loại file.</param>
    public sealed record Thumbnail(Bitmap Image, bool IsIcon) : IDisposable
    {
        public void Dispose() => Image.Dispose();
    }

    /// <summary>Đọc tối đa vài file cùng lúc — thư mục nhiều video không làm nghẽn máy.</summary>
    private static readonly SemaphoreSlim Gate = new(3);

    /// <summary>Ảnh thu nhỏ có cạnh dài khoảng <paramref name="size"/> px, giữ tỉ lệ khung hình; null nếu không có file / không đọc được.</summary>
    public static async Task<Thumbnail?> GetAsync(string path, int size, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            if (!File.Exists(full)) return null;
            var file = await StorageFile.GetFileFromPathAsync(full).AsTask(ct).ConfigureAwait(false);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)Math.Clamp(size, 16, 1024), ThumbnailOptions.ResizeThumbnail)
                .AsTask(ct).ConfigureAwait(false);
            if (thumb == null || thumb.Size == 0) return null;
            using var stream = thumb.AsStreamForRead();
            using var decoded = Image.FromStream(stream);
            // Chép sang ảnh riêng: ảnh đọc từ luồng cần luồng còn mở suốt thời gian dùng.
            return new Thumbnail(new Bitmap(decoded), thumb.Type == ThumbnailType.Icon);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaThumbnails {path}: {ex.Message}");
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
