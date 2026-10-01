using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScheduleApp.Models;

public enum StepType
{
    LaunchApp,
    Reminder,
    Wait,
    WaitForWindow,
    FocusWindow,
    MouseClick,
    TypeText,
    KeyPress,
    RunCommand,
    CloseApp,
    ClickImage,
    WaitForImage,
    ClickText,
    WaitForText,

    // Biến & dữ liệu
    SetVariable,
    LogMessage,

    // Điều khiển luồng (khối)
    If,
    Else,
    EndIf,
    Loop,
    EndLoop,
    BreakLoop,
    Label,
    Goto,
    StopFlow,
    CallJob,

    // Phần tử UI (UI Automation)
    ClickElement,
    SetElementText,
    WaitForElement,

    // Trình duyệt (Chrome DevTools Protocol)
    Browser,

    // Chuột mở rộng
    MouseScroll,
    MouseDrag
}

public enum MouseButtonKind
{
    Left,
    Right,
    Middle
}

/// <summary>Cách nhập văn bản của bước "Gõ văn bản".</summary>
public enum TypeMode
{
    /// <summary>Dán qua clipboard nếu phát hiện bộ gõ tiếng Việt (UniKey/EVKey…) đang chạy, ngược lại gõ từng phím.</summary>
    Auto,
    Keys,
    Paste
}

/// <summary>Loại điều kiện của bước Nếu / Lặp khi.</summary>
public enum ConditionKind
{
    Compare,
    WindowExists,
    ProcessRunning,
    FileExists,
    ImageOnScreen,
    TextOnScreen,
    ElementExists,
    LastStepFailed
}

public enum CompareOp
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    StartsWith,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,
    IsEmpty,
    IsNotEmpty,
    Regex
}

public enum LoopKind
{
    /// <summary>Lặp N lần.</summary>
    Count,
    /// <summary>Lặp khi điều kiện còn đúng.</summary>
    While,
    /// <summary>Lặp qua từng dòng của file CSV / Excel.</summary>
    Rows,
    /// <summary>Lặp qua từng dòng văn bản (file hoặc nội dung biến).</summary>
    Lines,
    /// <summary>Lặp qua các file trong thư mục.</summary>
    Files
}

/// <summary>Nguồn giá trị của bước "Gán biến".</summary>
public enum VarSource
{
    Value,
    Calc,
    Clipboard,
    Command,
    ScreenText,
    Element,
    AskUser,
    File
}

public enum BrowserAction
{
    Launch,
    Navigate,
    Click,
    SetValue,
    ReadText,
    WaitFor,
    RunScript
}

/// <summary>Xử lý khi bước bị lỗi (sau khi đã thử lại hết số lần).</summary>
public enum ErrorAction
{
    /// <summary>Theo cài đặt "Dừng flow khi một bước bị lỗi" của công việc.</summary>
    Default,
    Stop,
    Continue,
    GotoLabel
}

/// <summary>Một bước trong flow tự động. Ý nghĩa các trường phụ thuộc vào <see cref="Type"/>.</summary>
public sealed class ActionStep
{
    public StepType Type { get; set; } = StepType.LaunchApp;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Đường dẫn ứng dụng / tiêu đề cửa sổ / tên tiến trình / lệnh / tiêu đề nhắc nhở.
    /// Với bước nhận dạng màn hình: cửa sổ giới hạn vùng tìm (trống = cả màn hình).
    /// Điều kiện so sánh: vế trái. Lặp qua dòng/file: đường dẫn file/thư mục. Trình duyệt: tab (một phần URL/tiêu đề).
    /// </summary>
    public string Target { get; set; } = "";

    /// <summary>Tham số dòng lệnh khi mở ứng dụng. Điều kiện so sánh: vế phải. Lặp Excel: tên sheet. Gán biến: regex trích xuất.</summary>
    public string Arguments { get; set; } = "";

    /// <summary>Văn bản cần gõ / nội dung nhắc nhở / tổ hợp phím / chữ cần tìm (OCR) / bộ chọn phần tử / CSS selector / giá trị.</summary>
    public string Text { get; set; } = "";

    /// <summary>Tọa độ click; với bước nhận dạng màn hình là độ lệch so với tâm vùng tìm thấy. Kéo thả: điểm đầu.</summary>
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>Kéo thả: điểm cuối.</summary>
    public int X2 { get; set; }
    public int Y2 { get; set; }

    public MouseButtonKind Button { get; set; } = MouseButtonKind.Left;
    public bool DoubleClick { get; set; }

    /// <summary>Thời gian chờ (bước Chờ) hoặc timeout (chờ cửa sổ, chạy lệnh, nhận dạng), tính bằng ms.</summary>
    public int DelayMs { get; set; } = 1000;

    /// <summary>Nhắc nhở: tạm dừng flow cho tới khi người dùng xác nhận.</summary>
    public bool WaitForUser { get; set; }

    /// <summary>Đóng ứng dụng: kill tiến trình thay vì yêu cầu đóng lịch sự. Dừng flow: tính là thất bại.</summary>
    public bool Force { get; set; }

    /// <summary>Thời gian nghỉ sau khi hoàn thành bước (ms).</summary>
    public int DelayAfterMs { get; set; } = 500;

    /// <summary>Hình mẫu (PNG, base64) cho bước nhận dạng hình ảnh.</summary>
    public string ImageData { get; set; } = "";
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    /// <summary>Hệ số scale màn hình lúc chụp hình mẫu (1 = 100%, 1.25 = 125%…); 0 = không rõ.</summary>
    public double ImageScale { get; set; }

    /// <summary>Độ khớp tối thiểu của hình mẫu, tính theo %.</summary>
    public int Confidence { get; set; } = 85;

    /// <summary>Chọn lần xuất hiện thứ mấy khi chữ cần tìm xuất hiện nhiều chỗ (bắt đầu từ 1).</summary>
    public int MatchIndex { get; set; } = 1;

    /// <summary>Gõ văn bản: gõ từng phím hay dán qua clipboard.</summary>
    public TypeMode TypeMode { get; set; } = TypeMode.Auto;

    // ── Biến, điều kiện, vòng lặp ──

    /// <summary>Tên biến nhận kết quả (gán biến, đọc giá trị, output lệnh) / tiền tố biến của vòng lặp.</summary>
    public string Variable { get; set; } = "";

    public VarSource VarSource { get; set; } = VarSource.Value;
    public ConditionKind Condition { get; set; } = ConditionKind.Compare;
    public CompareOp CompareOp { get; set; } = CompareOp.Equals;

    /// <summary>Đảo điều kiện (KHÔNG …).</summary>
    public bool Negate { get; set; }

    public LoopKind LoopKind { get; set; } = LoopKind.Count;

    /// <summary>Số lần lặp / số nấc cuộn chuột (âm = cuộn xuống).</summary>
    public int Count { get; set; } = 1;

    /// <summary>Chạy công việc khác: Id công việc được gọi.</summary>
    public Guid? JobRef { get; set; }

    public BrowserAction BrowserAction { get; set; } = BrowserAction.Navigate;

    // ── Xử lý lỗi ──

    /// <summary>Số lần thử lại khi bước lỗi.</summary>
    public int Retries { get; set; }
    public int RetryDelayMs { get; set; } = 1000;
    public ErrorAction OnError { get; set; } = ErrorAction.Default;

    /// <summary>Nhãn cần nhảy tới khi bước lỗi (OnError = GotoLabel).</summary>
    public string ErrorLabel { get; set; } = "";

    /// <summary>Điểm dừng khi chạy thử từ trình soạn.</summary>
    public bool Breakpoint { get; set; }

    public static readonly Dictionary<StepType, string> TypeNames = new()
    {
        [StepType.LaunchApp] = "Mở ứng dụng / file / URL",
        [StepType.Reminder] = "Hiện nhắc nhở",
        [StepType.Wait] = "Chờ (delay)",
        [StepType.WaitForWindow] = "Chờ cửa sổ xuất hiện",
        [StepType.FocusWindow] = "Kích hoạt cửa sổ",
        [StepType.MouseClick] = "Click chuột",
        [StepType.TypeText] = "Gõ văn bản",
        [StepType.KeyPress] = "Nhấn phím / tổ hợp phím",
        [StepType.RunCommand] = "Chạy lệnh (cmd)",
        [StepType.CloseApp] = "Đóng ứng dụng",
        [StepType.ClickImage] = "Click vào hình ảnh",
        [StepType.WaitForImage] = "Chờ hình ảnh xuất hiện",
        [StepType.ClickText] = "Click vào chữ (OCR)",
        [StepType.WaitForText] = "Chờ chữ xuất hiện (OCR)",
        [StepType.SetVariable] = "Gán biến",
        [StepType.LogMessage] = "Ghi nhật ký",
        [StepType.If] = "Nếu (điều kiện)",
        [StepType.Else] = "Không thì",
        [StepType.EndIf] = "Hết Nếu",
        [StepType.Loop] = "Lặp",
        [StepType.EndLoop] = "Hết lặp",
        [StepType.BreakLoop] = "Thoát vòng lặp",
        [StepType.Label] = "Nhãn",
        [StepType.Goto] = "Nhảy tới nhãn",
        [StepType.StopFlow] = "Dừng flow",
        [StepType.CallJob] = "Chạy công việc khác",
        [StepType.ClickElement] = "Click phần tử UI",
        [StepType.SetElementText] = "Nhập vào phần tử UI",
        [StepType.WaitForElement] = "Chờ phần tử UI",
        [StepType.Browser] = "Trình duyệt (Chrome/Edge)",
        [StepType.MouseScroll] = "Cuộn chuột",
        [StepType.MouseDrag] = "Kéo thả chuột"
    };

    public static readonly Dictionary<ConditionKind, string> ConditionNames = new()
    {
        [ConditionKind.Compare] = "So sánh giá trị / biến",
        [ConditionKind.WindowExists] = "Cửa sổ đang mở",
        [ConditionKind.ProcessRunning] = "Tiến trình đang chạy",
        [ConditionKind.FileExists] = "File / thư mục tồn tại",
        [ConditionKind.ImageOnScreen] = "Hình ảnh có trên màn hình",
        [ConditionKind.TextOnScreen] = "Chữ có trên màn hình (OCR)",
        [ConditionKind.ElementExists] = "Phần tử UI tồn tại",
        [ConditionKind.LastStepFailed] = "Bước trước bị lỗi"
    };

    public static readonly Dictionary<CompareOp, string> CompareNames = new()
    {
        [CompareOp.Equals] = "bằng",
        [CompareOp.NotEquals] = "khác",
        [CompareOp.Contains] = "chứa",
        [CompareOp.NotContains] = "không chứa",
        [CompareOp.StartsWith] = "bắt đầu bằng",
        [CompareOp.Greater] = ">",
        [CompareOp.GreaterOrEqual] = "≥",
        [CompareOp.Less] = "<",
        [CompareOp.LessOrEqual] = "≤",
        [CompareOp.IsEmpty] = "rỗng",
        [CompareOp.IsNotEmpty] = "không rỗng",
        [CompareOp.Regex] = "khớp regex"
    };

    public static readonly Dictionary<LoopKind, string> LoopNames = new()
    {
        [LoopKind.Count] = "Lặp N lần",
        [LoopKind.While] = "Lặp khi điều kiện đúng",
        [LoopKind.Rows] = "Mỗi dòng của file Excel / CSV",
        [LoopKind.Lines] = "Mỗi dòng văn bản (file / biến)",
        [LoopKind.Files] = "Mỗi file trong thư mục"
    };

    public static readonly Dictionary<VarSource, string> VarSourceNames = new()
    {
        [VarSource.Value] = "Giá trị (có thể chứa {{biến}})",
        [VarSource.Calc] = "Phép tính (vd {{dem}} + 1)",
        [VarSource.Clipboard] = "Nội dung clipboard",
        [VarSource.Command] = "Output của lệnh cmd",
        [VarSource.ScreenText] = "Chữ đọc được trên màn hình (OCR)",
        [VarSource.Element] = "Giá trị của phần tử UI",
        [VarSource.AskUser] = "Hỏi người dùng nhập",
        [VarSource.File] = "Nội dung file văn bản"
    };

    public static readonly Dictionary<BrowserAction, string> BrowserActionNames = new()
    {
        [BrowserAction.Launch] = "Mở trình duyệt ở chế độ điều khiển",
        [BrowserAction.Navigate] = "Mở địa chỉ (URL)",
        [BrowserAction.Click] = "Click phần tử (CSS selector)",
        [BrowserAction.SetValue] = "Nhập giá trị vào ô (CSS selector)",
        [BrowserAction.ReadText] = "Đọc chữ / giá trị vào biến",
        [BrowserAction.WaitFor] = "Chờ phần tử xuất hiện",
        [BrowserAction.RunScript] = "Chạy JavaScript"
    };

    public static readonly Dictionary<ErrorAction, string> ErrorActionNames = new()
    {
        [ErrorAction.Default] = "Theo cài đặt của công việc",
        [ErrorAction.Stop] = "Dừng flow",
        [ErrorAction.Continue] = "Bỏ qua, chạy bước tiếp",
        [ErrorAction.GotoLabel] = "Nhảy tới nhãn…"
    };

    /// <summary>Bước mới với giá trị mặc định hợp lý cho từng loại.</summary>
    public static ActionStep CreateDefault(StepType type) => new()
    {
        Type = type,
        DelayMs = type switch
        {
            StepType.WaitForWindow => 15_000,
            StepType.FocusWindow => 5_000,
            StepType.RunCommand => 60_000,
            StepType.ClickImage or StepType.ClickText => 10_000,
            StepType.WaitForImage or StepType.WaitForText => 30_000,
            StepType.ClickElement or StepType.SetElementText => 10_000,
            StepType.WaitForElement or StepType.Browser => 15_000,
            StepType.SetVariable => 30_000,
            StepType.If or StepType.Loop => 0,
            _ => 1_000
        },
        WaitForUser = type == StepType.Reminder,
        Count = type switch { StepType.Loop => 3, StepType.MouseScroll => -3, _ => 1 },
        DelayAfterMs = IsControlType(type) || type is StepType.SetVariable or StepType.LogMessage ? 0 : 500
    };

    /// <summary>Bản sao nông (dùng khi thay biến trước lúc chạy — không sao chép lại dữ liệu hình mẫu).</summary>
    public ActionStep ShallowCopy() => (ActionStep)MemberwiseClone();

    public ActionStep Clone() =>
        JsonSerializer.Deserialize<ActionStep>(JsonSerializer.Serialize(this, JsonDefaults.Options), JsonDefaults.Options)!;

    /// <summary>Bước điều khiển luồng (không thao tác gì ngoài màn hình).</summary>
    public static bool IsControlType(StepType t) => t is StepType.If or StepType.Else or StepType.EndIf or StepType.Loop
        or StepType.EndLoop or StepType.BreakLoop or StepType.Label or StepType.Goto or StepType.StopFlow;

    [JsonIgnore] public bool IsControl => IsControlType(Type);

    /// <summary>Bước này có giả lập chuột/bàn phím hay không.</summary>
    [JsonIgnore]
    public bool UsesInput => Type is StepType.MouseClick or StepType.TypeText or StepType.KeyPress
        or StepType.ClickImage or StepType.ClickText or StepType.ClickElement or StepType.SetElementText
        or StepType.MouseScroll or StepType.MouseDrag or StepType.CallJob;

    /// <summary>Bước này chụp màn hình để nhận dạng.</summary>
    [JsonIgnore]
    public bool UsesScreen => Type is StepType.ClickImage or StepType.WaitForImage or StepType.ClickText or StepType.WaitForText
        || (Type is StepType.If or StepType.Loop && Condition is ConditionKind.ImageOnScreen or ConditionKind.TextOnScreen)
        || (Type == StepType.SetVariable && VarSource == VarSource.ScreenText);

    [JsonIgnore] public bool IsImageStep => Type is StepType.ClickImage or StepType.WaitForImage;
    [JsonIgnore] public bool IsTextStep => Type is StepType.ClickText or StepType.WaitForText;

    /// <summary>Bước điều kiện có dùng các trường điều kiện không.</summary>
    [JsonIgnore] public bool HasCondition => Type == StepType.If || (Type == StepType.Loop && LoopKind == LoopKind.While);

    public string Describe() => Type switch
    {
        StepType.LaunchApp => $"Mở \"{Target}\"{(string.IsNullOrWhiteSpace(Arguments) ? "" : " " + Arguments)}",
        StepType.Reminder => $"Nhắc: {Short(string.IsNullOrWhiteSpace(Text) ? Target : Text)}{(WaitForUser ? "  (chờ xác nhận)" : "")}",
        StepType.Wait => $"Chờ {FormatMs(DelayMs)}",
        StepType.WaitForWindow => $"Chờ cửa sổ \"{Target}\" (tối đa {FormatMs(DelayMs)})",
        StepType.FocusWindow => $"Kích hoạt cửa sổ \"{Target}\"",
        StepType.MouseClick =>
            $"{ClickName()} chuột {ButtonName(Button)} tại ({X}, {Y})" +
            (string.IsNullOrWhiteSpace(Target) ? " trên màn hình" : $" trong cửa sổ \"{Target}\""),
        StepType.TypeText => $"Gõ \"{Short(Text)}\"" + InWindow(),
        StepType.KeyPress => $"Nhấn {Text}" + InWindow(),
        StepType.RunCommand => $"Chạy lệnh: {Short(Target)}" + IntoVar(),
        StepType.CloseApp => $"Đóng \"{Target}\"{(Force ? " (buộc đóng)" : "")}",
        StepType.ClickImage => $"{ClickName()} vào hình mẫu {ImageWidth}×{ImageHeight} (khớp ≥ {Confidence}%)" + SearchArea(),
        StepType.WaitForImage => $"Chờ hình mẫu {ImageWidth}×{ImageHeight} xuất hiện (tối đa {FormatMs(DelayMs)})" + SearchArea(),
        StepType.ClickText => $"{ClickName()} vào chữ \"{Short(Text)}\"" + (MatchIndex > 1 ? $" (lần thứ {MatchIndex})" : "") + SearchArea(),
        StepType.WaitForText => $"Chờ chữ \"{Short(Text)}\" xuất hiện (tối đa {FormatMs(DelayMs)})" + SearchArea(),
        StepType.SetVariable => $"{{{{{Variable}}}}} ← " + VarSource switch
        {
            VarSource.Value => $"\"{Short(Text)}\"",
            VarSource.Calc => $"= {Short(Text)}",
            VarSource.Clipboard => "clipboard",
            VarSource.Command => $"output lệnh: {Short(Target)}",
            VarSource.ScreenText => "chữ OCR" + SearchArea(),
            VarSource.Element => $"phần tử [{Short(Text)}]" + SearchArea(),
            VarSource.AskUser => $"hỏi: \"{Short(Text)}\"",
            VarSource.File => $"file \"{Short(Target)}\"",
            _ => ""
        } + (string.IsNullOrWhiteSpace(Arguments) || VarSource is VarSource.Value or VarSource.Calc or VarSource.AskUser ? "" : $"  (regex {Short(Arguments)})"),
        StepType.LogMessage => $"Ghi: {Short(Text)}",
        StepType.If => "Nếu " + DescribeCondition(),
        StepType.Else => "Ngược lại",
        StepType.EndIf => "Hết khối Nếu",
        StepType.Loop => LoopKind switch
        {
            LoopKind.Count => $"Lặp {Count} lần",
            LoopKind.While => "Lặp khi " + DescribeCondition(),
            LoopKind.Rows => $"Mỗi dòng của \"{Short(Target)}\"" + (string.IsNullOrWhiteSpace(Arguments) ? "" : $" [sheet {Arguments}]") + $" → {{{{{LoopVar}.Cột}}}}",
            LoopKind.Lines => $"Mỗi dòng của \"{Short(Target)}\" → {{{{{LoopVar}}}}}",
            LoopKind.Files => $"Mỗi file {(string.IsNullOrWhiteSpace(Arguments) ? "*.*" : Arguments)} trong \"{Short(Target)}\" → {{{{{LoopVar}}}}}",
            _ => ""
        },
        StepType.EndLoop => "Quay lại đầu vòng lặp",
        StepType.BreakLoop => "Thoát khỏi vòng lặp hiện tại",
        StepType.Label => $"■ {Target}",
        StepType.Goto => $"→ nhãn \"{Target}\"",
        StepType.StopFlow => (Force ? "Dừng flow (tính là lỗi)" : "Dừng flow") + (string.IsNullOrWhiteSpace(Text) ? "" : $": {Short(Text)}"),
        StepType.CallJob => $"Chạy \"{Target}\"",
        StepType.ClickElement => $"{ClickName()} phần tử [{Short(Text)}]" + SearchArea(),
        StepType.SetElementText => $"Nhập \"{Short(Arguments)}\" vào [{Short(Text)}]" + SearchArea(),
        StepType.WaitForElement => $"Chờ phần tử [{Short(Text)}] (tối đa {FormatMs(DelayMs)})" + SearchArea(),
        StepType.Browser => BrowserAction switch
        {
            BrowserAction.Launch => $"Mở {(string.IsNullOrWhiteSpace(Target) ? "Chrome" : Target)} chế độ điều khiển" + (string.IsNullOrWhiteSpace(Text) ? "" : $" → {Short(Text)}"),
            BrowserAction.Navigate => $"Mở {Short(Text)}" + InTab(),
            BrowserAction.Click => $"Click \"{Short(Text)}\"" + InTab(),
            BrowserAction.SetValue => $"Nhập \"{Short(Arguments)}\" vào \"{Short(Text)}\"" + InTab(),
            BrowserAction.ReadText => $"Đọc \"{Short(Text)}\"" + IntoVar() + InTab(),
            BrowserAction.WaitFor => $"Chờ \"{Short(Text)}\" (tối đa {FormatMs(DelayMs)})" + InTab(),
            BrowserAction.RunScript => $"JS: {Short(Text)}" + IntoVar() + InTab(),
            _ => ""
        },
        StepType.MouseScroll => $"Cuộn {(Count >= 0 ? "lên" : "xuống")} {Math.Abs(Count)} nấc" +
                                (X != 0 || Y != 0 ? $" tại ({X}, {Y})" : "") + InWindow(),
        StepType.MouseDrag => $"Kéo từ ({X}, {Y}) tới ({X2}, {Y2})" + InWindow(),
        _ => Type.ToString()
    };

    [JsonIgnore] public string LoopVar => string.IsNullOrWhiteSpace(Variable) ? (LoopKind == LoopKind.Rows ? "row" : "item") : Variable.Trim();

    public string DescribeCondition()
    {
        var not = Negate ? "KHÔNG " : "";
        return not + Condition switch
        {
            ConditionKind.Compare => CompareOp is CompareOp.IsEmpty or CompareOp.IsNotEmpty
                ? $"\"{Short(Target)}\" {CompareNames[CompareOp]}"
                : $"\"{Short(Target)}\" {CompareNames[CompareOp]} \"{Short(Arguments)}\"",
            ConditionKind.WindowExists => $"cửa sổ \"{Target}\" đang mở",
            ConditionKind.ProcessRunning => $"tiến trình \"{Target}\" đang chạy",
            ConditionKind.FileExists => $"tồn tại \"{Short(Target)}\"",
            ConditionKind.ImageOnScreen => $"thấy hình mẫu {ImageWidth}×{ImageHeight}" + SearchArea(),
            ConditionKind.TextOnScreen => $"thấy chữ \"{Short(Text)}\"" + SearchArea(),
            ConditionKind.ElementExists => $"có phần tử [{Short(Text)}]" + SearchArea(),
            ConditionKind.LastStepFailed => "bước trước bị lỗi",
            _ => ""
        };
    }

    private string ClickName() => DoubleClick ? "Double-click" : "Click";

    private string InWindow() => string.IsNullOrWhiteSpace(Target) ? "" : $" vào \"{Target}\"";

    private string SearchArea() => string.IsNullOrWhiteSpace(Target) ? "" : $" trong \"{Target}\"";

    private string InTab() => string.IsNullOrWhiteSpace(Target) ? "" : $"  [tab {Short(Target)}]";

    private string IntoVar() => string.IsNullOrWhiteSpace(Variable) ? "" : $" → {{{{{Variable}}}}}";

    public static string ButtonName(MouseButtonKind b) => b switch
    {
        MouseButtonKind.Right => "phải",
        MouseButtonKind.Middle => "giữa",
        _ => "trái"
    };

    public static string FormatMs(int ms) => ms >= 1000 ? $"{ms / 1000.0:0.#} giây" : $"{ms} ms";

    private static string Short(string s)
    {
        s = s.Replace("\r", "").Replace("\n", " ⏎ ");
        return s.Length > 50 ? s[..50] + "…" : s;
    }
}
