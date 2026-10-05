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

/// <summary>Khung trạng thái ở góc phải màn hình khi công việc chạy.</summary>
public enum RunOverlayMode
{
    /// <summary>Theo cài đặt chung (Cài đặt → Chung).</summary>
    Default,
    Show,
    /// <summary>Không hiện — vd đang trình chiếu / phát video, không muốn khung che màn hình.</summary>
    Hide
}

/// <summary>Biến khai báo sẵn của công việc (giá trị ban đầu).</summary>
public sealed class VariableDef
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    /// <summary>Giải thích ngắn cho người dùng (mẫu có sẵn) — hiện trong màn hình "Thiết lập mẫu".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
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

    /// <summary>
    /// Thời gian chạy tối đa (phút, 0 = không giới hạn): quá thời gian thì dừng lần chạy và ghi là lỗi — một công việc bị treo
    /// không chặn mãi các công việc khác. Không ghi vào JSON khi bằng 0 (file cũ giữ nguyên).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int MaxRunMinutes { get; set; }

    /// <summary>Giá trị lớn nhất của <see cref="MaxRunMinutes"/>: 7 ngày.</summary>
    public const int MaxRunMinutesLimit = 7 * 24 * 60;

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

    /// <summary>Hiện khung trạng thái ở góc phải màn hình khi chạy công việc này.</summary>
    public RunOverlayMode RunOverlay { get; set; }

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

    /// <summary>
    /// Chờ duyệt trên máy: công việc tạo / sửa qua Telegram hoặc nhập từ file không chạy (lịch, kích hoạt, /run, dòng lệnh,
    /// được công việc khác gọi) cho tới khi người dùng xem từng bước và bấm Duyệt. Không ghi vào JSON khi false (file cũ giữ nguyên).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NeedsApproval { get; set; }

    /// <summary>Vì sao cần duyệt, vd "Tạo qua Telegram 05/10 14:32", "Nhập từ file cong-viec.json 05/10 14:32".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApprovalReason { get; set; }

    /// <summary>Được tự chạy theo lịch / trình kích hoạt: đang bật và không chờ duyệt.</summary>
    [JsonIgnore] public bool Armed => Enabled && !NeedsApproval;

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
