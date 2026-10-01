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

    /// <summary>Kịch bản kiểm thử: mỗi lần chạy ghi lại kết quả từng bước / từng kiểm tra và xuất báo cáo (HTML + JUnit XML).</summary>
    public bool IsTestCase { get; set; }

    /// <summary>Sau khi chạy, xóa các bản ghi Dynamics 365 mà flow đã tạo (danh sách trong {{d365.created}}).</summary>
    public bool CleanupTestData { get; set; }

    /// <summary>Tag của kịch bản kiểm thử, cách nhau dấu phẩy (vd "smoke, regression") — chọn bộ chạy bằng --tag.</summary>
    public string Tags { get; set; } = "";

    /// <summary>Mã test case / yêu cầu bên ngoài (vd Azure DevOps Test Plans 1234) — ghi vào báo cáo và junit.xml.</summary>
    public string TestCaseId { get; set; } = "";

    /// <summary>Kiểm thử theo dữ liệu: file Excel / CSV, mỗi dòng chạy kịch bản một lần (biến {{row.TênCột}}) và là một test case riêng.</summary>
    public string DataFile { get; set; } = "";

    /// <summary>Sheet của <see cref="DataFile"/> (trống = sheet đầu).</summary>
    public string DataSheet { get; set; } = "";

    /// <summary>Danh sách tag đã tách và bỏ khoảng trắng.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> TagList =>
        Tags.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

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
