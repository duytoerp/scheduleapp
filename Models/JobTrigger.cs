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
    AppStartup
}

/// <summary>Một điều kiện kích hoạt công việc ngoài lịch chạy.</summary>
public sealed class JobTrigger
{
    public TriggerType Type { get; set; } = TriggerType.Hotkey;
    public bool Enabled { get; set; } = true;
    public string Value { get; set; } = "";
    public string Value2 { get; set; } = "";
    public int Minutes { get; set; } = 10;

    public static readonly Dictionary<TriggerType, string> TypeNames = new()
    {
        [TriggerType.Hotkey] = "Phím tắt",
        [TriggerType.FileCreated] = "Có file mới trong thư mục",
        [TriggerType.ProcessStarted] = "Ứng dụng vừa mở",
        [TriggerType.ProcessExited] = "Ứng dụng vừa đóng",
        [TriggerType.Idle] = "Máy rảnh (không dùng chuột/phím)",
        [TriggerType.SessionUnlock] = "Mở khóa màn hình",
        [TriggerType.AppStartup] = "ScheduleApp khởi động / đăng nhập Windows"
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
        _ => Type.ToString()
    };
}
