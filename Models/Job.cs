using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScheduleApp.Models;

/// <summary>Một công việc: lịch chạy + danh sách bước thao tác.</summary>
public sealed class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Công việc mới";
    public bool Enabled { get; set; } = true;
    public ScheduleConfig Schedule { get; set; } = new();

    /// <summary>Hiện nhắc nhở trước giờ chạy N phút (0 = không nhắc).</summary>
    public int RemindBeforeMinutes { get; set; }

    public bool StopOnError { get; set; } = true;
    public List<ActionStep> Steps { get; set; } = [];

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
