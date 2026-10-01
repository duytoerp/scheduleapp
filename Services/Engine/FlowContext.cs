using ScheduleApp.Models;

namespace ScheduleApp.Services.Engine;

/// <summary>Tùy chọn cho một lần chạy flow (chạy thử, gỡ lỗi, biến truyền từ trình kích hoạt…).</summary>
public sealed class RunOptions
{
    /// <summary>Bắt đầu từ bước thứ mấy (0 = đầu flow).</summary>
    public int StartIndex { get; init; }

    /// <summary>Chạy thử từ trình soạn (không gửi thông báo ra ngoài).</summary>
    public bool IsTest { get; init; }

    /// <summary>Dừng trước mỗi bước, chờ người dùng bấm "Bước tiếp".</summary>
    public bool StepMode { get; init; }

    /// <summary>Dừng tại các bước có điểm dừng (breakpoint).</summary>
    public bool UseBreakpoints { get; init; }

    /// <summary>Báo vị trí bước đang chạy của flow gốc (để tô sáng trên khung thiết kế). Gọi từ luồng nền.</summary>
    public Action<int>? StepStarted { get; init; }

    /// <summary>Biến có sẵn khi bắt đầu (vd {{trigger.file}} khi kích hoạt do có file mới).</summary>
    public Dictionary<string, string>? Variables { get; init; }

    /// <summary>Chạy kiểm thử: ghi lại kết quả từng bước để xuất báo cáo (null = không ghi).</summary>
    public Testing.TestRecorder? Recorder { get; init; }

    public static readonly RunOptions Default = new();

    /// <summary>Bản sao có gắn bộ ghi kết quả kiểm thử.</summary>
    public RunOptions With(Testing.TestRecorder recorder) => new()
    {
        StartIndex = StartIndex,
        IsTest = IsTest,
        StepMode = StepMode,
        UseBreakpoints = UseBreakpoints,
        StepStarted = StepStarted,
        Variables = Variables,
        Recorder = recorder
    };

    /// <summary>Bản sao với biến có sẵn khác.</summary>
    public RunOptions WithVariables(Dictionary<string, string>? variables) => new()
    {
        StartIndex = StartIndex,
        IsTest = IsTest,
        StepMode = StepMode,
        UseBreakpoints = UseBreakpoints,
        StepStarted = StepStarted,
        Variables = variables,
        Recorder = Recorder
    };
}

/// <summary>Lệnh của người dùng khi flow đang tạm dừng gỡ lỗi.</summary>
public enum DebugCommand
{
    Step,
    Continue,
    Stop
}

/// <summary>Kết quả chạy flow.</summary>
public sealed record FlowResult(bool Ok, string Message, int FailedStep = -1, string? Screenshot = null);

/// <summary>Trạng thái dùng chung trong một lần chạy flow (kể cả các flow con được gọi).</summary>
public sealed class FlowContext
{
    public FlowContext(Job rootJob, IUserNotifier ui, RunOptions options, Func<Guid, Job?> findJob, CancellationToken ct)
    {
        RootJob = rootJob;
        Ui = ui;
        Options = options;
        FindJob = findJob;
        Ct = ct;
        StepMode = options.StepMode;
        Expander = new VariableExpander(Vars);
        CurrentJob = rootJob;
    }

    public Job RootJob { get; }
    public IUserNotifier Ui { get; }
    public RunOptions Options { get; }
    public Func<Guid, Job?> FindJob { get; }
    public CancellationToken Ct { get; }

    public Dictionary<string, string> Vars { get; } = new(StringComparer.OrdinalIgnoreCase);
    public VariableExpander Expander { get; }

    /// <summary>Đang ở chế độ chạy từng bước.</summary>
    public bool StepMode { get; set; }

    /// <summary>Bước thực thi gần nhất bị lỗi (cho điều kiện "Bước trước bị lỗi").</summary>
    public bool LastStepFailed { get; set; }

    /// <summary>Độ sâu gọi flow con (chống đệ quy vô hạn).</summary>
    public int Depth { get; set; }

    /// <summary>Ảnh chụp màn hình lần lỗi gần nhất.</summary>
    public string? LastScreenshot { get; set; }

    /// <summary>Công việc đang chạy (gốc hoặc công việc con được gọi).</summary>
    public Job CurrentJob { get; set; }

    /// <summary>Giá trị thực tế của điều kiện vừa đánh giá (vd giá trị field) — hiện trong báo cáo khi bước Kiểm tra sai.</summary>
    public string ConditionDetail { get; set; } = "";

    public Testing.TestRecorder? Recorder => Options.Recorder;

    /// <summary>Kiểm tra trước mỗi bước (chế độ an toàn: tạm dừng khi người dùng đụng chuột/phím).</summary>
    public Func<FlowContext, Task>? BeforeStep { get; init; }

    public string Expand(string? template) => Expander.Expand(template ?? "");

    /// <summary>Bản sao của bước với các trường văn bản đã thay {{biến}}.</summary>
    public ActionStep ExpandStep(ActionStep s)
    {
        var copy = s.ShallowCopy();
        copy.Target = Expand(s.Target);
        copy.Arguments = Expand(s.Arguments);
        // Ghi Excel: mỗi dòng "Cột=giá trị" được thay biến riêng lúc ghi (giá trị có thể chứa xuống dòng).
        copy.Text = s.Type == StepType.WriteData ? s.Text : Expand(s.Text);
        copy.RowRef = Expand(s.RowRef);
        copy.Headers = Expand(s.Headers);
        copy.Message = Expand(s.Message);
        copy.Form = Expand(s.Form);
        return copy;
    }

    public void SetVar(string name, string value)
    {
        name = name.Trim();
        if (name.StartsWith("{{") && name.EndsWith("}}")) name = name[2..^2].Trim();
        if (name.Length == 0) throw new InvalidOperationException("Chưa nhập tên biến.");
        Vars[name] = value;
    }
}
