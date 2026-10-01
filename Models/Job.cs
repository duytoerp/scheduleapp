using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScheduleApp.Models;

/// <summary>Xử lý lần chạy theo lịch bị lỡ (máy tắt, ngủ hoặc ScheduleApp không chạy).</summary>
public enum MissedRunPolicy
{
    Skip,
    RunOnce,
    Ask
}

/// <summary>Khi nào gửi thông báo ra ngoài (Telegram / email / webhook).</summary>
public enum NotifyMode
{
    OnError,
    Always,
    Never
}

/// <summary>Biến khai báo sẵn của công việc (giá trị ban đầu).</summary>
public sealed class VariableDef
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>Một công việc: lịch chạy + danh sách bước thao tác.</summary>
public sealed class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Công việc mới";

    /// <summary>Nhóm / thư mục hiển thị ở màn hình chính.</summary>
    public string Group { get; set; } = "";

    public bool Enabled { get; set; } = true;
    public ScheduleConfig Schedule { get; set; } = new();

    /// <summary>Hiện nhắc nhở trước giờ chạy N phút (0 = không nhắc).</summary>
    public int RemindBeforeMinutes { get; set; }

    public bool StopOnError { get; set; } = true;
    public List<ActionStep> Steps { get; set; } = [];

    /// <summary>Kích hoạt theo sự kiện (phím tắt, file mới, ứng dụng mở…), ngoài lịch chạy.</summary>
    public List<JobTrigger> Triggers { get; set; } = [];

    public List<VariableDef> Variables { get; set; } = [];

    public MissedRunPolicy MissedRunPolicy { get; set; } = MissedRunPolicy.Skip;

    /// <summary>Đánh thức máy khỏi chế độ ngủ trước giờ chạy.</summary>
    public bool WakeComputer { get; set; }

    /// <summary>Bỏ qua các ngày nghỉ lễ trong cài đặt.</summary>
    public bool SkipHolidays { get; set; }

    public NotifyMode NotifyMode { get; set; } = NotifyMode.OnError;

    /// <summary>Công việc chạy dọn dẹp khi flow thất bại (vd đóng ứng dụng, gửi báo cáo).</summary>
    public Guid? OnFailureJobId { get; set; }

    public DateTime? LastRun { get; set; }
    public string? LastResult { get; set; }

    [JsonIgnore] public DateTime? NextRun { get; set; }
    [JsonIgnore] public bool Reminded { get; set; }

    public Job Clone() => JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(this, JsonDefaults.Options), JsonDefaults.Options)!;
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
}
