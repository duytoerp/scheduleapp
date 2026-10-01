using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Services.Engine;
using ScheduleApp.Vision;

namespace ScheduleApp.Services.Testing;

/// <summary>Kết quả một bước trong lần chạy kiểm thử.</summary>
public sealed class StepRecord
{
    /// <summary>Thứ tự bước trong công việc chứa nó (tính từ 1).</summary>
    public int Number { get; init; }

    /// <summary>Độ sâu gọi công việc con (0 = công việc gốc).</summary>
    public int Depth { get; init; }

    public string JobName { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsAssert { get; init; }
    public bool Ok { get; init; }

    /// <summary>Lỗi (bước lỗi) hoặc giá trị thực tế (kiểm tra).</summary>
    public string Detail { get; init; } = "";

    public DateTime Start { get; init; }
    public double Seconds { get; init; }

    /// <summary>Ảnh chụp lúc bước lỗi (đường dẫn file), nếu có.</summary>
    public string? Screenshot { get; set; }
}

/// <summary>Kết quả một kịch bản kiểm thử (một công việc).</summary>
public sealed class TestCaseResult
{
    public Guid JobId { get; init; }
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public List<StepRecord> Steps { get; init; } = [];

    public double Seconds => (End - Start).TotalSeconds;
    public int AssertsPassed => Steps.Count(s => s.IsAssert && s.Ok);
    public int AssertsFailed => Steps.Count(s => s.IsAssert && !s.Ok);

    public static TestCaseResult Create(Job job, DateTime start, DateTime end, FlowResult? result, TestRecorder recorder) => new()
    {
        JobId = job.Id,
        Name = job.Name,
        Group = job.Group,
        Start = start,
        End = end,
        Ok = result?.Ok == true,
        Message = result?.Message ?? "Không chạy (đang chạy sẵn hoặc bị hủy trong hàng đợi)",
        Steps = recorder.Steps
    };
}

/// <summary>
/// Ghi lại từng bước của một lần chạy kiểm thử (thời gian, kết quả, giá trị thực tế của các bước Kiểm tra) và chụp ảnh khi bước lỗi.
/// Gọi từ luồng chạy flow.
/// </summary>
public sealed class TestRecorder
{
    private static readonly string[] BrowserTabs = ["main.aspx", ".dynamics.com", ".crm"];
    private readonly object _sync = new();
    private readonly List<StepRecord> _steps = [];
    private int _shots;

    /// <param name="screenshotDir">Thư mục lưu ảnh khi bước lỗi; null = không chụp.</param>
    public TestRecorder(string? screenshotDir)
    {
        ScreenshotDir = screenshotDir;
    }

    public string? ScreenshotDir { get; }

    /// <summary>Các bước đã chạy, theo thứ tự bắt đầu.</summary>
    public List<StepRecord> Steps
    {
        get { lock (_sync) return [.. _steps.OrderBy(s => s.Start)]; }
    }

    public void Add(StepRecord record)
    {
        lock (_sync) _steps.Add(record);
    }

    /// <summary>
    /// Chụp ảnh lúc bước lỗi: ưu tiên nội dung tab trình duyệt đang điều khiển (rõ ràng, chụp được cả khi cửa sổ bị che / headless),
    /// không có trình duyệt thì chụp màn hình.
    /// </summary>
    public async Task<string?> CaptureAsync(ActionStep step, int number, CancellationToken ct)
    {
        if (ScreenshotDir == null) return null;
        try
        {
            Directory.CreateDirectory(ScreenshotDir);
            var path = Path.Combine(ScreenshotDir, $"{Interlocked.Increment(ref _shots):000}_buoc{number}.png");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                if (await BrowserClient.IsRunningAsync(timeout.Token))
                {
                    var tab = step.Type is StepType.Browser or StepType.Dynamics ? step.Target : "";
                    tab = await BrowserClient.PreferTabAsync(tab, BrowserTabs, timeout.Token);
                    await File.WriteAllBytesAsync(path, await BrowserClient.CaptureScreenshotAsync(tab, timeout.Token), ct);
                    return path;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Warn("      Không chụp được trang web: " + ex.Message);
            }

            // Bước chỉ xử lý dữ liệu (so sánh biến, gọi API…): ảnh màn hình không giúp gì mà còn lộ màn hình của người dùng.
            if (!ShowsScreen(step)) return null;
            using var shot = ScreenCapture.Capture(ScreenCapture.VirtualScreen);
            shot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("      Không chụp được ảnh cho báo cáo: " + ex.Message);
            return null;
        }
    }

    /// <summary>Bước thao tác / đọc trên màn hình (ảnh chụp lúc lỗi có ích).</summary>
    internal static bool ShowsScreen(ActionStep s) =>
        s.UsesInput || s.UsesScreen
        || s.Type is StepType.LaunchApp or StepType.CloseApp or StepType.WaitForWindow or StepType.FocusWindow or StepType.Reminder
            or StepType.ClickElement or StepType.SetElementText or StepType.WaitForElement
        || (s.Type == StepType.SetVariable && s.VarSource == VarSource.Element)
        || (s.HasCondition && s.Condition is ConditionKind.WindowExists or ConditionKind.ElementExists);
}
