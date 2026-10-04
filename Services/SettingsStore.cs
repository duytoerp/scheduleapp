using System.Globalization;
using System.Text.Json;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>Đọc/ghi cài đặt chung (settings.json) và tra cứu ngày nghỉ.</summary>
public static class SettingsStore
{
    private static readonly object Sync = new();
    private static AppSettings? _current;

    private static string FilePath => Path.Combine(JobStore.DataDir, "settings.json");

    public static AppSettings Current
    {
        get
        {
            lock (Sync) return _current ??= Load();
        }
    }

    private static AppSettings Load() => Load(FilePath);

    /// <summary>
    /// Đọc cài đặt; file hỏng → giữ bản .broken-…, dùng bản .bak của lần lưu trước (không lặng lẽ thay bằng mặc định rồi ghi đè sau 1 phút).
    /// </summary>
    internal static AppSettings Load(string path) =>
        SafeFile.Load(path, "cài đặt", "đang dùng cài đặt mặc định, hãy kiểm tra lại ⚙ Cài đặt",
            json => JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options)).Value ?? new AppSettings();

    public static void Save()
    {
        lock (Sync)
        {
            try
            {
                SafeFile.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonDefaults.Options));
            }
            catch (Exception ex)
            {
                Log.Error("Không lưu được cài đặt: " + ex.Message);
            }
        }
    }

    public static void Replace(AppSettings settings)
    {
        lock (Sync) _current = settings;
        Save();
    }

    /// <summary>Bỏ bản đã đọc trong bộ nhớ để lần sau đọc lại từ file (chỉ dùng cho kiểm thử).</summary>
    internal static void ResetForTests()
    {
        lock (Sync) _current = null;
    }

    /// <summary>Ngày <paramref name="date"/> có nằm trong danh sách ngày nghỉ không.</summary>
    public static bool IsHoliday(DateTime date)
    {
        foreach (var raw in Current.Holidays)
        {
            var s = raw.Trim();
            if (s.Length == 0) continue;
            if (DateTime.TryParseExact(s, ["d/M/yyyy", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var full))
            {
                if (full.Date == date.Date) return true;
            }
            else if (DateTime.TryParseExact(s + "/2000", ["d/M/yyyy", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var annual))
            {
                if (annual.Day == date.Day && annual.Month == date.Month) return true;
            }
        }
        return false;
    }
}
