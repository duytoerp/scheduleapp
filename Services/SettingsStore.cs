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
            lock (Sync)
            {
                if (_current == null)
                {
                    _current = Load();
                    // Bản cũ / sửa tay còn mật khẩu, token chữ thường → mã hóa và lưu ngay một lần; cài đặt bản cũ → chuyển đổi một lần.
                    if (ProtectPlaintextSecrets(_current) | Upgrade(_current)) Save();
                    WarnInsecureEmail(_current);
                }
                return _current;
            }
        }
    }

    /// <summary>Phiên bản cấu trúc cài đặt hiện tại (<see cref="AppSettings.SettingsVersion"/>).</summary>
    internal const int CurrentVersion = 1;

    /// <summary>
    /// Chuyển đổi cài đặt của bản cũ, mỗi bước đúng một lần (đánh dấu bằng <see cref="AppSettings.SettingsVersion"/>, lưu cùng cài đặt):
    /// 1 — tắt gửi ảnh chụp màn hình lỗi qua Telegram / email (bản cũ bật sẵn; ảnh cả màn hình có thể lộ thông tin khác).
    /// Người dùng bật lại sau đó thì giữ nguyên.
    /// </summary>
    /// <returns>
    /// true nếu có cài đặt vừa đổi (cần lưu ngay). Chỉ đổi dấu phiên bản thì false — dấu được ghi ở lần lưu sau
    /// (không ghi file lúc mở app khi không có gì đổi, vd lần đầu dùng hay đang dùng mặc định vì settings.json hỏng).
    /// </returns>
    internal static bool Upgrade(AppSettings s)
    {
        if (s.SettingsVersion >= CurrentVersion) return false;
        bool changed = false;
        if (s.SettingsVersion < 1 && (s.Telegram.SendScreenshot || s.Email.AttachScreenshot))
        {
            s.Telegram.SendScreenshot = false;
            s.Email.AttachScreenshot = false;
            changed = true;
            Log.Warn("Đã tắt gửi kèm ảnh chụp màn hình lỗi qua Telegram / email (ảnh cả màn hình có thể lộ thông tin khác) — " +
                     "bật lại trong ⚙ Cài đặt → Thông báo nếu cần.");
        }
        s.SettingsVersion = CurrentVersion;
        return changed;
    }

    /// <summary>Lúc mở app: email đang bật nhưng máy chủ SMTP tắt SSL/TLS và chưa cho phép gửi không mã hóa → mọi thông báo email sẽ lỗi.</summary>
    private static void WarnInsecureEmail(AppSettings s)
    {
        if (s.Email.Enabled && NotificationService.SmtpTlsProblem(s.Email) is { } problem)
            Log.Warn("Thông báo email sẽ không gửi được: " + problem);
    }

    /// <summary>
    /// Mã hóa DPAPI các ô bí mật còn là chữ thường (khóa AI, mật khẩu SMTP / IMAP, token Telegram / GitHub, secret và header của
    /// kết nối API, URL webhook). Ô đã mã hóa giữ nguyên.
    /// </summary>
    /// <returns>true nếu có ô vừa được mã hóa (cần lưu lại).</returns>
    internal static bool ProtectPlaintextSecrets(AppSettings s)
    {
        bool changed = false;
        string Fix(string value)
        {
            var result = Protector.EnsureProtected(value);
            if (result != value) changed = true;
            return result;
        }
        try
        {
            s.Telegram.BotToken = Fix(s.Telegram.BotToken);
            s.Email.Password = Fix(s.Email.Password);
            s.Inbox.Password = Fix(s.Inbox.Password);
            s.Ai.ApiKey = Fix(s.Ai.ApiKey);
            s.Update.Token = Fix(s.Update.Token);
            s.Webhook.Url = Fix(s.Webhook.Url);
            foreach (var c in s.ApiConnections)
            {
                c.Secret = Fix(c.Secret);
                c.Headers = Fix(c.Headers);
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warn("Không mã hóa được mật khẩu / token trong cài đặt: " + ex.Message);
        }
        if (changed) Log.Info("Đã mã hóa các mật khẩu / token còn lưu chữ thường trong cài đặt.");
        return changed;
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
            // Lúc mở chưa đọc được settings.json (đang bị giữ): đã báo người dùng, không ghi nhật ký lỗi mỗi phút.
            if (SafeFile.IsReadOnly(FilePath)) return;
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
