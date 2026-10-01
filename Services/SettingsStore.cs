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

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonDefaults.Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error($"Không đọc được settings.json ({ex.Message}) — dùng cài đặt mặc định.");
        }
        return new AppSettings();
    }

    public static void Save()
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(JobStore.DataDir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonDefaults.Options));
                File.Move(tmp, FilePath, true);
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
