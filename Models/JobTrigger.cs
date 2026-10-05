using System.Text.Json.Serialization;

namespace ScheduleApp.Models;

public enum TriggerType
{
    /// <summary>Phím tắt toàn hệ thống, vd Ctrl+Alt+1.</summary>
    Hotkey,
    /// <summary>Có file mới trong thư mục (Value = thư mục, Value2 = bộ lọc, vd *.pdf).</summary>
    FileCreated,
    /// <summary>Một ứng dụng vừa mở (Value = tên tiến trình).</summary>
    ProcessStarted,
    /// <summary>Một ứng dụng vừa đóng (Value = tên tiến trình).</summary>
    ProcessExited,
    /// <summary>Máy không có thao tác chuột/phím trong N phút.</summary>
    Idle,
    /// <summary>Mở khóa màn hình Windows.</summary>
    SessionUnlock,
    /// <summary>Khi ScheduleApp khởi động (thường là lúc đăng nhập Windows).</summary>
    AppStartup,
    /// <summary>Có email mới chưa đọc (Value = tiêu đề chứa, Value2 = người gửi: địa chỉ hoặc @tên miền, Minutes = chu kỳ kiểm tra).</summary>
    EmailReceived
}

/// <summary>Một điều kiện kích hoạt công việc ngoài lịch chạy.</summary>
public sealed class JobTrigger : IJsonOnDeserializing
{
    public TriggerType Type { get; set; } = TriggerType.Hotkey;
    public bool Enabled { get; set; } = true;
    public string Value { get; set; } = "";
    public string Value2 { get; set; } = "";
    public int Minutes { get; set; } = 10;

    /// <summary>
    /// Email: chỉ chạy với thư mà máy chủ nhận thư xác nhận đúng người gửi (DMARC / DKIM / SPF đạt). Bật sẵn cho trình kích hoạt mới;
    /// trình kích hoạt đã lưu từ bản cũ (file không có trường này) giữ là tắt để không tự dưng ngừng chạy.
    /// </summary>
    public bool RequireAuthenticatedEmail { get; set; } = true;

    void IJsonOnDeserializing.OnDeserializing() => RequireAuthenticatedEmail = false;

    public JobTrigger Clone() => (JobTrigger)MemberwiseClone();

    public static readonly Dictionary<TriggerType, string> TypeNames = new()
    {
        [TriggerType.Hotkey] = "Phím tắt",
        [TriggerType.FileCreated] = "Có file mới trong thư mục",
        [TriggerType.ProcessStarted] = "Ứng dụng vừa mở",
        [TriggerType.ProcessExited] = "Ứng dụng vừa đóng",
        [TriggerType.Idle] = "Máy rảnh (không dùng chuột/phím)",
        [TriggerType.SessionUnlock] = "Mở khóa màn hình",
        [TriggerType.AppStartup] = "ScheduleApp khởi động / đăng nhập Windows",
        [TriggerType.EmailReceived] = "Có email mới (Outlook / IMAP)"
    };

    public string Describe() => (Enabled ? "" : "(tắt) ") + Type switch
    {
        TriggerType.Hotkey => $"Phím tắt {Value}",
        TriggerType.FileCreated => $"File mới {(string.IsNullOrWhiteSpace(Value2) ? "*.*" : Value2)} trong \"{Value}\"",
        TriggerType.ProcessStarted => $"Khi mở \"{Value}\"",
        TriggerType.ProcessExited => $"Khi đóng \"{Value}\"",
        TriggerType.Idle => $"Rảnh {Minutes} phút",
        TriggerType.SessionUnlock => "Khi mở khóa màn hình",
        TriggerType.AppStartup => "Khi ScheduleApp khởi động",
        TriggerType.EmailReceived => "Email mới" +
                                     (string.IsNullOrWhiteSpace(Value) ? "" : $" tiêu đề chứa \"{Value}\"") +
                                     (string.IsNullOrWhiteSpace(Value2) ? "" : $" từ \"{Value2}\"") +
                                     (RequireAuthenticatedEmail ? ", đã xác thực" : "") +
                                     $" (kiểm tra mỗi {Math.Max(1, Minutes)} phút)",
        _ => Type.ToString()
    };
}
