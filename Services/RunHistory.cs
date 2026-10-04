using System.Text.Json;
using ScheduleApp.Models;
using ScheduleApp.Vision;

namespace ScheduleApp.Services;

/// <summary>Một lần chạy flow (lưu trong history.jsonl).</summary>
public sealed class RunRecord
{
    public Guid JobId { get; set; }
    public string JobName { get; set; } = "";
    public string Trigger { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    /// <summary>Bước lỗi (tính từ 1), 0 nếu không có.</summary>
    public int FailedStep { get; set; }
    public string? Screenshot { get; set; }

    /// <summary>Báo cáo kiểm thử (index.html) của lần chạy — chỉ có với kịch bản kiểm thử.</summary>
    public string? Report { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public TimeSpan Duration => End - Start;
}

/// <summary>Lịch sử chạy: ghi nối tiếp từng dòng JSON, giữ tối đa <see cref="MaxRecords"/> lần gần nhất trong bộ nhớ.</summary>
public static class RunHistory
{
    private const int MaxRecords = 2000;
    private static readonly object Sync = new();
    private static List<RunRecord>? _records;

    private static string FilePath => Path.Combine(JobStore.DataDir, "history.jsonl");

    public static event Action<RunRecord>? Added;

    public static List<RunRecord> All
    {
        get
        {
            lock (Sync) return [.. Records];
        }
    }

    private static List<RunRecord> Records
    {
        get
        {
            if (_records != null) return _records;
            _records = [];
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (var line in File.ReadLines(FilePath))
                    {
                        if (line.Length == 0) continue;
                        try
                        {
                            if (JsonSerializer.Deserialize<RunRecord>(line, JsonDefaults.Options) is { } r) _records.Add(r);
                        }
                        catch (JsonException) { /* bỏ dòng hỏng */ }
                    }
                    if (_records.Count > MaxRecords)
                    {
                        _records = _records[^MaxRecords..];
                        Rewrite();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Không đọc được lịch sử chạy: " + ex.Message);
            }
            return _records;
        }
    }

    public static void Add(RunRecord record)
    {
        lock (Sync)
        {
            Records.Add(record);
            try
            {
                Directory.CreateDirectory(JobStore.DataDir);
                File.AppendAllText(FilePath, JsonSerializer.Serialize(record, Compact) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Log.Error("Không ghi được lịch sử chạy: " + ex.Message);
            }
        }
        Added?.Invoke(record);
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Records.Clear();
            try
            {
                File.Delete(FilePath);
                File.Delete(SafeFile.BackupPath(FilePath)); // bản trước của lần rút gọn — xóa lịch sử là xóa hết
            }
            catch (IOException) { }
        }
    }

    /// <summary>Ghi lại file sau khi bỏ bớt lần chạy cũ (file tạm + giữ bản trước thành .bak — mất điện giữa chừng không mất lịch sử).</summary>
    private static void Rewrite()
    {
        try
        {
            SafeFile.WriteAllText(FilePath, string.Concat(_records!.Select(r => JsonSerializer.Serialize(r, Compact) + Environment.NewLine)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Không rút gọn được lịch sử chạy: " + ex.Message);
        }
    }

    private static readonly JsonSerializerOptions Compact = new(JsonDefaults.Options) { WriteIndented = false };
}

/// <summary>Chụp màn hình khi bước lỗi để biết chuyện gì đã xảy ra lúc không có ai ngồi máy.</summary>
public static class ErrorScreenshots
{
    public static string Dir => Path.Combine(Log.LogDir, "screenshots");

    /// <summary>Chụp toàn bộ màn hình; trả về đường dẫn file hoặc null nếu tắt/không chụp được.</summary>
    public static string? Capture(string jobName, int stepNumber) =>
        SettingsStore.Current.ScreenshotOnError ? CaptureAlways(jobName, $"buoc{stepNumber}") : null;

    /// <summary>Chụp toàn bộ màn hình (kể cả khi tắt "chụp khi lỗi") — dùng cho bước "Gửi thông báo" kèm ảnh.</summary>
    public static string? CaptureAlways(string jobName, string suffix)
    {
        try
        {
            var now = DateTime.Now;
            var dir = Path.Combine(Dir, now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dir);
            var safeName = string.Concat(jobName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (safeName.Length > 60) safeName = safeName[..60];
            var path = Path.Combine(dir, $"{now:HHmmss}_{safeName}_{suffix}.png");
            using var shot = ScreenCapture.Capture(ScreenCapture.VirtualScreen);
            shot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Log.Info($"      📷 Đã chụp màn hình: {path}");
            return path;
        }
        catch (Exception ex)
        {
            Log.Warn("      Không chụp được màn hình: " + ex.Message);
            return null;
        }
    }

    /// <summary>Xóa thư mục ảnh lỗi cũ hơn số ngày trong cài đặt.</summary>
    public static void Cleanup()
    {
        int days = SettingsStore.Current.KeepScreenshotsDays;
        if (days <= 0 || !Directory.Exists(Dir)) return;
        foreach (var d in Directory.GetDirectories(Dir))
        {
            if (DateTime.TryParseExact(Path.GetFileName(d), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date) &&
                date < DateTime.Today.AddDays(-days))
            {
                try { Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
