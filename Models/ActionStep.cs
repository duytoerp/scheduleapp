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
    MouseDrag,

    // Dữ liệu & tích hợp
    ContinueLoop,
    WriteData,
    HttpRequest,
    AskAi,
    Notify,

    // Kiểm thử & Dynamics 365 (model-driven app)
    Dynamics,
    Assert,

    /// <summary>Phát lần lượt danh sách video / nhạc bằng trình phát của ScheduleApp, chờ phát hết mới sang bước sau.</summary>
    PlayMedia,

    /// <summary>Thu nhỏ (ẩn xuống thanh tác vụ) cửa sổ đang dùng hoặc cửa sổ chỉ định, nhớ lại để "Kích hoạt cửa sổ" mở lại sau.</summary>
    MinimizeWindow
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
    LastStepFailed,

    /// <summary>Phần tử có trên trang web (CSS selector, qua cổng điều khiển trình duyệt).</summary>
    BrowserElement,
    /// <summary>Giá trị field trên form Dynamics 365 đang mở.</summary>
    D365FieldValue,
    /// <summary>Trạng thái field: bắt buộc / khóa / hiển thị / có thay đổi.</summary>
    D365FieldState,
    /// <summary>Form Dynamics 365 đang hiện thông báo / lỗi (có chứa chữ).</summary>
    D365Notification,
    /// <summary>Số bản ghi trả về từ truy vấn Web API (dùng phiên đăng nhập của trình duyệt).</summary>
    D365RecordCount,
    /// <summary>Số dòng của subgrid trên form.</summary>
    D365SubgridCount,
    /// <summary>Subgrid có dòng chứa chữ (ở bất kỳ cột nào).</summary>
    D365SubgridRow,
    /// <summary>Nút trên thanh lệnh đang hiện / bấm được / bị mờ.</summary>
    D365Command,
    /// <summary>Tên form đang mở (form selector).</summary>
    D365CurrentForm,
    /// <summary>Người dùng đang đăng nhập có vai trò bảo mật (security role).</summary>
    D365UserRole
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
    File,
    /// <summary>Thêm một dòng vào cuối biến danh sách (mỗi phần tử một dòng).</summary>
    ListAdd,
    /// <summary>Tách chuỗi thành danh sách theo dấu phân cách.</summary>
    Split,
    /// <summary>Trích giá trị từ chuỗi JSON theo đường dẫn (vd value[0].name).</summary>
    JsonPath
}

/// <summary>Cách ghi của bước "Ghi Excel / CSV".</summary>
public enum DataAction
{
    /// <summary>Thêm dòng mới vào cuối bảng.</summary>
    AppendRow,
    /// <summary>Sửa các ô của một dòng có sẵn (theo số dòng hoặc cột khóa).</summary>
    UpdateRow,
    /// <summary>Ghi nội dung vào file văn bản (.txt, .log…), thay nội dung cũ.</summary>
    WriteText,
    /// <summary>Thêm nội dung vào cuối file văn bản.</summary>
    AppendText
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

/// <summary>Hành động của bước "Dynamics 365" — chạy Client API (Xrm) trong tab model-driven app.</summary>
public enum D365Action
{
    OpenForm,
    OpenView,
    WaitForm,
    SetField,
    GetField,
    Save,
    Command,
    SelectTab,
    BpfNext,
    BpfPrevious,
    ConfirmDialog,
    GetRecordId,
    GetNotifications,
    WebApi,
    Cleanup,
    RunScript,

    // Subgrid trên form, danh sách (view), tạo nhanh
    SubgridOpenRow,
    SubgridGetValue,
    SubgridNew,
    SubgridRefresh,
    ViewQuery,
    ViewOpenRecord,
    QuickCreate,

    // Đăng nhập & người dùng
    Login,
    GetUser
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

    /// <summary>
    /// Click đã ghi kèm hình mẫu: điểm click lệch bao nhiêu so với tâm hình mẫu (px lúc chụp).
    /// Bước "Click vào hình ảnh" dùng X, Y cho độ lệch này; click ghi lại dùng X, Y cho tọa độ dự phòng.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int ImageOffsetX { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int ImageOffsetY { get; set; }

    /// <summary>
    /// Ảnh cả cửa sổ ứng dụng lúc ghi click (tên file trong recorded-shots, xem <c>Vision.RecordedShots</c>) — chỉ để xem lại
    /// đã click vào đâu, không dùng khi chạy. ContextClickX / Y = điểm click trong ảnh đó (px của ảnh đã lưu).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public string? ContextShot { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int ContextClickX { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int ContextClickY { get; set; }

    /// <summary>
    /// Phát video / nhạc: tổng thời lượng các file trong danh sách (ms) — tự tính mỗi khi danh sách thay đổi; 0 = chưa rõ
    /// (file dùng biến, không đọc được thời lượng…).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int MediaDurationMs { get; set; }

    /// <summary>Phát video: màn hình phát khi máy có nhiều màn hình — 0 = màn hình chính, -1 = màn hình đang có chuột, N = màn hình số N.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Monitor { get; set; }

    /// <summary>
    /// Phát video: mã màn hình thật đã chọn (<see cref="Native.Display.Id"/>) — số màn hình có thể đổi khi cắm lại / đổi dock, mã thì không.
    /// Khi chạy ưu tiên màn hình có mã này, không thấy thì theo số <see cref="Monitor"/>. null = không có (bước cũ, màn hình chính…).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MonitorId { get; set; }

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

    public D365Action D365Action { get; set; } = D365Action.SetField;

    /// <summary>Dynamics 365 — mở form: tên hoặc Id form chính cần mở (trống = form mặc định của người dùng).</summary>
    public string Form { get; set; } = "";

    /// <summary>Kiểm tra (Assert): mô tả hiện trong báo cáo kiểm thử (trống = tự mô tả theo điều kiện).</summary>
    public string Message { get; set; } = "";

    // ── Xử lý lỗi ──

    /// <summary>Số lần thử lại khi bước lỗi.</summary>
    public int Retries { get; set; }
    public int RetryDelayMs { get; set; } = 1000;
    public ErrorAction OnError { get; set; } = ErrorAction.Default;

    /// <summary>Nhãn cần nhảy tới khi bước lỗi (OnError = GotoLabel).</summary>
    public string ErrorLabel { get; set; } = "";

    /// <summary>Điểm dừng khi chạy thử từ trình soạn.</summary>
    public bool Breakpoint { get; set; }

    // ── Ghi dữ liệu, API ──

    public DataAction DataAction { get; set; } = DataAction.AppendRow;

    /// <summary>Ghi Excel/CSV — dòng cần sửa: số dòng trong Excel (vd {{row.rowNumber}}) hoặc "Cột=giá trị" để tìm theo cột khóa.</summary>
    public string RowRef { get; set; } = "";

    /// <summary>Gọi API: phương thức HTTP (GET, POST, PATCH…).</summary>
    public string Method { get; set; } = "GET";

    /// <summary>Gọi API: header thêm, mỗi dòng "Tên: giá trị".</summary>
    public string Headers { get; set; } = "";

    /// <summary>Gọi API: tên kết nối đã khai báo trong Cài đặt (URL gốc + xác thực); trống = gọi trực tiếp.</summary>
    public string Connection { get; set; } = "";

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
        [StepType.MouseDrag] = "Kéo thả chuột",
        [StepType.ContinueLoop] = "Bỏ qua, sang lần lặp kế",
        [StepType.WriteData] = "Ghi file (Excel / CSV / văn bản)",
        [StepType.HttpRequest] = "Gọi API (HTTP / REST)",
        [StepType.AskAi] = "Hỏi AI (Claude)",
        [StepType.Notify] = "Gửi thông báo",
        [StepType.Dynamics] = "Dynamics 365 (model-driven)",
        [StepType.Assert] = "Kiểm tra (Assert)",
        [StepType.PlayMedia] = "Phát video / nhạc",
        [StepType.MinimizeWindow] = "Thu nhỏ cửa sổ"
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
        [ConditionKind.LastStepFailed] = "Bước trước bị lỗi",
        [ConditionKind.BrowserElement] = "Phần tử có trên trang web (CSS)",
        [ConditionKind.D365FieldValue] = "D365: giá trị field",
        [ConditionKind.D365FieldState] = "D365: trạng thái field (bắt buộc / khóa / hiện)",
        [ConditionKind.D365Notification] = "D365: form có thông báo / lỗi",
        [ConditionKind.D365RecordCount] = "D365: số bản ghi (truy vấn Web API)",
        [ConditionKind.D365SubgridCount] = "D365: số dòng của subgrid",
        [ConditionKind.D365SubgridRow] = "D365: subgrid có dòng chứa chữ",
        [ConditionKind.D365Command] = "D365: nút trên thanh lệnh (hiện / bấm được)",
        [ConditionKind.D365CurrentForm] = "D365: form đang mở là",
        [ConditionKind.D365UserRole] = "D365: người dùng có vai trò (security role)"
    };

    /// <summary>Trạng thái nút cho điều kiện <see cref="ConditionKind.D365Command"/> (lưu trong <see cref="Arguments"/>).</summary>
    public static readonly Dictionary<string, string> D365CommandStates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["visible"] = "đang hiện (trên thanh lệnh hoặc trong menu …)",
        ["enabled"] = "hiện và bấm được",
        ["disabled"] = "hiện nhưng bị mờ (không bấm được)"
    };

    /// <summary>Trạng thái field cho điều kiện <see cref="ConditionKind.D365FieldState"/> (lưu trong <see cref="Arguments"/>).</summary>
    public static readonly Dictionary<string, string> D365FieldStates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["required"] = "bắt buộc nhập",
        ["recommended"] = "nên nhập",
        ["disabled"] = "bị khóa (chỉ đọc)",
        ["visible"] = "đang hiện",
        ["dirty"] = "đã sửa chưa lưu",
        ["empty"] = "đang trống"
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
        [VarSource.File] = "Nội dung file văn bản",
        [VarSource.ListAdd] = "Thêm vào cuối danh sách",
        [VarSource.Split] = "Tách chuỗi thành danh sách",
        [VarSource.JsonPath] = "Trích từ JSON (vd kết quả API)"
    };

    public static readonly Dictionary<DataAction, string> DataActionNames = new()
    {
        [DataAction.AppendRow] = "Thêm dòng mới vào cuối",
        [DataAction.UpdateRow] = "Sửa ô của một dòng có sẵn",
        [DataAction.WriteText] = "Ghi file văn bản (thay nội dung cũ)",
        [DataAction.AppendText] = "Thêm vào cuối file văn bản"
    };

    /// <summary>Ghi file văn bản thay vì bảng Excel / CSV.</summary>
    [JsonIgnore] public bool IsTextWrite => Type == StepType.WriteData && DataAction is DataAction.WriteText or DataAction.AppendText;

    /// <summary>Phát video: các dòng của Text (bỏ dòng trống, ghi chú #).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> MediaLines => Text.Replace("\r\n", "\n").Split('\n')
        .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    public static readonly string[] HttpMethods = ["GET", "POST", "PATCH", "PUT", "DELETE"];

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

    public static readonly Dictionary<D365Action, string> D365ActionNames = new()
    {
        [D365Action.OpenForm] = "Mở form bản ghi (mới / có sẵn)",
        [D365Action.OpenView] = "Mở danh sách (view)",
        [D365Action.WaitForm] = "Chờ form tải xong",
        [D365Action.SetField] = "Nhập giá trị field",
        [D365Action.GetField] = "Đọc giá trị field vào biến",
        [D365Action.Save] = "Lưu bản ghi",
        [D365Action.Command] = "Bấm nút trên thanh lệnh (ribbon)",
        [D365Action.SelectTab] = "Chuyển tab của form",
        [D365Action.BpfNext] = "Quy trình (BPF): sang giai đoạn kế",
        [D365Action.BpfPrevious] = "Quy trình (BPF): về giai đoạn trước",
        [D365Action.ConfirmDialog] = "Bấm nút trên hộp thoại",
        [D365Action.GetRecordId] = "Lấy Id bản ghi vào biến",
        [D365Action.GetNotifications] = "Đọc thông báo / lỗi trên form vào biến",
        [D365Action.WebApi] = "Gọi Web API (phiên đăng nhập trình duyệt)",
        [D365Action.Cleanup] = "Xóa dữ liệu test đã tạo",
        [D365Action.RunScript] = "Chạy JavaScript với formContext / Xrm",
        [D365Action.SubgridOpenRow] = "Subgrid: mở bản ghi của một dòng",
        [D365Action.SubgridGetValue] = "Subgrid: đọc giá trị một ô vào biến",
        [D365Action.SubgridNew] = "Subgrid: tạo bản ghi liên quan mới (+ Mới)",
        [D365Action.SubgridRefresh] = "Subgrid: làm mới",
        [D365Action.ViewQuery] = "Danh sách (view): đọc các bản ghi vào biến",
        [D365Action.ViewOpenRecord] = "Danh sách (view): tìm và mở bản ghi",
        [D365Action.QuickCreate] = "Tạo nhanh (quick create) bản ghi",
        [D365Action.Login] = "Đăng nhập Microsoft (tài khoản test, MFA mã TOTP)",
        [D365Action.GetUser] = "Đọc người dùng & vai trò vào biến"
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
            StepType.FocusWindow or StepType.MinimizeWindow => 5_000,
            StepType.RunCommand => 60_000,
            StepType.ClickImage or StepType.ClickText => 10_000,
            StepType.WaitForImage or StepType.WaitForText => 30_000,
            StepType.ClickElement or StepType.SetElementText => 10_000,
            StepType.WaitForElement or StepType.Browser => 15_000,
            StepType.Dynamics => 30_000,
            StepType.Assert => 5_000,
            StepType.SetVariable => 30_000,
            StepType.HttpRequest => 60_000,
            StepType.AskAi => 120_000,
            StepType.If or StepType.Loop => 0,
            _ => 1_000
        },
        WaitForUser = type == StepType.Reminder,
        // Phát video: mặc định toàn màn hình.
        Force = type == StepType.PlayMedia,
        Count = type switch { StepType.Loop => 3, StepType.MouseScroll => -3, _ => 1 },
        DelayAfterMs = IsControlType(type) || type is StepType.SetVariable or StepType.LogMessage or StepType.WriteData
            or StepType.HttpRequest or StepType.AskAi or StepType.Notify or StepType.Assert ? 0 : 500
    };

    /// <summary>Bản sao nông (dùng khi thay biến trước lúc chạy — không sao chép lại dữ liệu hình mẫu).</summary>
    public ActionStep ShallowCopy() => (ActionStep)MemberwiseClone();

    public ActionStep Clone() =>
        JsonSerializer.Deserialize<ActionStep>(JsonSerializer.Serialize(this, JsonDefaults.Options), JsonDefaults.Options)!;

    /// <summary>Bước điều khiển luồng (không thao tác gì ngoài màn hình).</summary>
    public static bool IsControlType(StepType t) => t is StepType.If or StepType.Else or StepType.EndIf or StepType.Loop
        or StepType.EndLoop or StepType.BreakLoop or StepType.ContinueLoop or StepType.Label or StepType.Goto or StepType.StopFlow;

    [JsonIgnore] public bool IsControl => IsControlType(Type);

    /// <summary>Bước này có giả lập chuột/bàn phím hay không.</summary>
    [JsonIgnore]
    public bool UsesInput => Type is StepType.MouseClick or StepType.TypeText or StepType.KeyPress
        or StepType.ClickImage or StepType.ClickText or StepType.ClickElement or StepType.SetElementText
        or StepType.MouseScroll or StepType.MouseDrag or StepType.CallJob;

    /// <summary>Bước này chụp màn hình để nhận dạng.</summary>
    [JsonIgnore]
    public bool UsesScreen => Type is StepType.ClickImage or StepType.WaitForImage or StepType.ClickText or StepType.WaitForText
        || (Type is StepType.If or StepType.Loop or StepType.Assert && Condition is ConditionKind.ImageOnScreen or ConditionKind.TextOnScreen)
        || (Type == StepType.SetVariable && VarSource == VarSource.ScreenText)
        || (Type is StepType.AskAi or StepType.Notify && Force) || HasImageAnchor || Type == StepType.PlayMedia;

    /// <summary>Click phần tử còn giữ tọa độ lúc ghi macro — dùng khi không tìm thấy phần tử.</summary>
    [JsonIgnore] public bool HasRecordedPoint => Type == StepType.ClickElement && !string.IsNullOrWhiteSpace(Target) && (X != 0 || Y != 0);

    /// <summary>Loại click có thể kèm hình mẫu để tìm lại theo hình ảnh (click chuột, click phần tử).</summary>
    public static bool CanHaveImageAnchor(StepType t) => t is StepType.MouseClick or StepType.ClickElement;

    /// <summary>
    /// Click (thường do trình ghi macro tạo) kèm hình mẫu chụp quanh điểm click: khi chạy tìm lại chỗ đó theo hình ảnh
    /// rồi mới dùng tọa độ — vẫn click đúng khi cửa sổ di chuyển, đổi kích thước hay bố cục xê dịch.
    /// </summary>
    [JsonIgnore] public bool HasImageAnchor => CanHaveImageAnchor(Type) && !string.IsNullOrEmpty(ImageData);

    [JsonIgnore] public bool IsImageStep =>Type is StepType.ClickImage or StepType.WaitForImage;
    [JsonIgnore] public bool IsTextStep => Type is StepType.ClickText or StepType.WaitForText;

    /// <summary>Bước điều kiện có dùng các trường điều kiện không.</summary>
    [JsonIgnore] public bool HasCondition => Type is StepType.If or StepType.Assert || (Type == StepType.Loop && LoopKind == LoopKind.While);

    public string Describe() => Type switch
    {
        StepType.LaunchApp => $"Mở \"{Target}\"{(string.IsNullOrWhiteSpace(Arguments) ? "" : " " + Arguments)}",
        StepType.Reminder => $"Nhắc: {Short(string.IsNullOrWhiteSpace(Target) ? Text : Target)}{(WaitForUser ? "  (chờ xác nhận)" : "")}",
        StepType.Wait => $"Chờ {FormatMs(DelayMs)}",
        StepType.WaitForWindow => $"Chờ cửa sổ \"{Target}\" (tối đa {FormatMs(DelayMs)})",
        StepType.FocusWindow => $"Kích hoạt cửa sổ \"{Target}\"",
        StepType.MinimizeWindow => (string.IsNullOrWhiteSpace(Target) ? "Thu nhỏ cửa sổ đang dùng" : $"Thu nhỏ cửa sổ \"{Target}\"") + IntoVar(),
        StepType.MouseClick =>
            (HasImageAnchor ? $"{ClickName()} chuột {ButtonName(Button)} theo hình mẫu {ImageWidth}×{ImageHeight}" : $"{ClickName()} chuột {ButtonName(Button)} tại ({X}, {Y})") +
            (string.IsNullOrWhiteSpace(Target) ? " trên màn hình" : $" trong cửa sổ \"{Target}\"") +
            (HasImageAnchor ? $" · dự phòng ({X}, {Y})" : ""),
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
            VarSource.ListAdd => $"thêm \"{Short(Text)}\" vào cuối",
            VarSource.Split => $"tách \"{Short(Text)}\" theo \"{(Arguments.Length == 0 ? "," : Arguments)}\"",
            VarSource.JsonPath => $"JSON {Short(Text)} → {Short(Arguments)}",
            _ => ""
        } + (string.IsNullOrWhiteSpace(Arguments) || !UsesRegex ? "" : $"  (regex {Short(Arguments)})"),
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
        StepType.ClickElement => $"{ClickName()} phần tử [{Short(Text)}]" + SearchArea() +
                                 (HasImageAnchor ? " · dự phòng: hình mẫu" + (HasRecordedPoint ? $", ({X}, {Y})" : "")
                                     : HasRecordedPoint ? $" · dự phòng ({X}, {Y})" : ""),
        StepType.SetElementText => $"Nhập \"{Short(Arguments)}\" vào [{Short(Text)}]" + SearchArea(),
        StepType.WaitForElement => $"Chờ phần tử [{Short(Text)}] (tối đa {FormatMs(DelayMs)})" + SearchArea(),
        StepType.Browser => BrowserAction switch
        {
            BrowserAction.Launch => $"Mở {(string.IsNullOrWhiteSpace(Target) ? "Chrome" : Target)} chế độ điều khiển{(Force ? " (ẩn)" : "")}" +
                                    (string.IsNullOrWhiteSpace(Arguments) ? "" : $" · hồ sơ \"{Short(Arguments)}\"") +
                                    (string.IsNullOrWhiteSpace(Text) ? "" : $" → {Short(Text)}"),
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
        StepType.ContinueLoop => "Bỏ qua phần còn lại, sang lần lặp kế tiếp",
        StepType.WriteData when IsTextWrite => (DataAction == DataAction.WriteText ? "Ghi vào" : "Thêm vào cuối") +
                              $" \"{Short(Target)}\": {Short(Text.Replace("\r", "").Replace("\n", " ⏎ "))}",
        StepType.PlayMedia => MediaLines.Count switch
        {
            0 => "Phát video (chưa chọn file)",
            1 => $"Phát \"{Short(MediaName(MediaLines[0]))}\"" + (MediaDurationMs > 0 ? $" ({FormatDuration(MediaDurationMs)})" : ""),
            int n => $"Phát lần lượt {n} mục" + (MediaDurationMs > 0 ? $" (tổng {FormatDuration(MediaDurationMs)})" : "") + ": " +
                     Short(string.Join(" → ", MediaLines.Select(MediaName)))
        } + (Force ? " · toàn màn hình" : "") + (Monitor != 0 ? " · " + Native.Displays.ChoiceName(Monitor) : ""),
        StepType.WriteData => (DataAction == DataAction.AppendRow ? "Thêm dòng vào" : $"Sửa dòng [{Short(RowRef)}] của") +
                              $" \"{Short(Target)}\"" + (string.IsNullOrWhiteSpace(Arguments) ? "" : $" [sheet {Arguments}]") +
                              $": {Short(string.Join(", ", Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))}",
        StepType.HttpRequest => $"{Method.ToUpperInvariant()} " + (string.IsNullOrWhiteSpace(Connection) ? "" : $"[{Connection}] ") + Short(Target) +
                                (string.IsNullOrWhiteSpace(Arguments) ? "" : $" → {Short(Arguments)}") + IntoVar(),
        StepType.AskAi => $"AI: \"{Short(Text)}\"" + (Force ? " + ảnh màn hình" + SearchArea() : "") + IntoVar(),
        StepType.Notify => $"Gửi: {Short(Text)}" + (Force ? " + ảnh màn hình" : ""),
        StepType.Dynamics => "D365: " + D365Action switch
        {
            D365Action.OpenForm => $"mở form {Short(Text)}" + (string.IsNullOrWhiteSpace(Arguments) ? " (mới)" : $" [{Short(Arguments)}]") +
                                   (string.IsNullOrWhiteSpace(Form) ? "" : $" · form \"{Short(Form)}\""),
            D365Action.OpenView => $"mở danh sách {Short(Text)}" + (string.IsNullOrWhiteSpace(Arguments) ? "" : $" [view {Short(Arguments)}]"),
            D365Action.WaitForm => $"chờ form tải xong (tối đa {FormatMs(DelayMs)})",
            D365Action.SetField => $"{Short(Text)} = \"{Short(Arguments)}\"",
            D365Action.GetField => $"đọc {Short(Text)}" + IntoVar(),
            D365Action.Save => "lưu bản ghi" + IntoVar(),
            D365Action.Command => $"bấm \"{Short(Text)}\"",
            D365Action.SelectTab => $"chuyển tab \"{Short(Text)}\"",
            D365Action.BpfNext => "BPF sang giai đoạn kế",
            D365Action.BpfPrevious => "BPF về giai đoạn trước",
            D365Action.ConfirmDialog => $"hộp thoại → bấm \"{(string.IsNullOrWhiteSpace(Text) ? "nút chính" : Short(Text))}\"",
            D365Action.GetRecordId => "lấy Id bản ghi" + IntoVar(),
            D365Action.GetNotifications => "đọc thông báo trên form" + IntoVar(),
            D365Action.WebApi => $"{(string.IsNullOrWhiteSpace(Method) ? "GET" : Method.ToUpperInvariant())} {Short(Arguments)}" + IntoVar(),
            D365Action.Cleanup => "xóa dữ liệu test đã tạo ({{d365.created}})",
            D365Action.RunScript => $"JS: {Short(Text)}" + IntoVar(),
            D365Action.SubgridOpenRow => $"subgrid {Short(Text)} → mở dòng {RowName()}",
            D365Action.SubgridGetValue => $"subgrid {Short(Text)} dòng {RowName()} · cột {(string.IsNullOrWhiteSpace(Arguments) ? "tên" : Short(Arguments))}" + IntoVar(),
            D365Action.SubgridNew => $"subgrid {Short(Text)} → + Mới",
            D365Action.SubgridRefresh => $"làm mới subgrid {Short(Text)}",
            D365Action.ViewQuery => $"đọc view {ViewName()} của {Short(Text)}" + SearchText() + IntoVar(),
            D365Action.ViewOpenRecord => $"mở bản ghi trong view {ViewName()} của {Short(Text)}" + SearchText(),
            D365Action.QuickCreate => $"tạo nhanh {Short(Arguments)}: {Short(string.Join(", ", Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))}" + IntoVar(),
            D365Action.Login => $"đăng nhập Microsoft \"{Short(Text)}\"" + (string.IsNullOrWhiteSpace(RowRef) ? "" : " + mã MFA (TOTP)"),
            D365Action.GetUser => "đọc người dùng & vai trò" + IntoVar(),
            _ => ""
        } + InTab(),
        StepType.Assert => "Kiểm tra: " + (string.IsNullOrWhiteSpace(Message) ? DescribeCondition() : Short(Message)),
        _ => Type.ToString()
    };

    /// <summary>
    /// Bước cần xem kỹ khi công việc đến từ nguồn khác (Telegram, file nhập): chạy lệnh, mở ứng dụng, gửi HTTP, ghi file, mở trang web /
    /// chạy JavaScript, gõ chữ / nhấn phím, đóng ứng dụng — bản xem trước và màn hình duyệt luôn hiện đầy đủ nội dung của chúng.
    /// </summary>
    [JsonIgnore]
    public bool IsRisky => Type is StepType.RunCommand or StepType.LaunchApp or StepType.HttpRequest or StepType.WriteData or StepType.TypeText
                               or StepType.KeyPress or StepType.SetElementText or StepType.CloseApp
        || (Type == StepType.SetVariable && VarSource == VarSource.Command)
        || (Type == StepType.Browser && BrowserAction is BrowserAction.Launch or BrowserAction.Navigate or BrowserAction.SetValue or BrowserAction.RunScript)
        || (Type == StepType.Dynamics && D365Action is D365Action.RunScript or D365Action.WebApi);

    /// <summary>
    /// Bước phải hiện ra (đầy đủ) khi duyệt công việc từ nguồn khác: bước <see cref="IsRisky"/>, gọi công việc khác, và các bước tạo giá trị
    /// mà bước nguy hiểm có thể dùng qua {{biến}} (Gán biến, Lặp theo dòng / file), điều kiện / bước D365 gửi truy vấn hoặc xóa / sửa
    /// bản ghi, và mọi bước có dùng {{secret:…}} (bí mật có thể bị gửi đi ở bất cứ trường nào).
    /// </summary>
    [JsonIgnore]
    public bool NeedsReview => IsRisky || Type is StepType.CallJob or StepType.SetVariable
        || (Type == StepType.Loop && LoopKind is LoopKind.Rows or LoopKind.Lines or LoopKind.Files)
        || (HasCondition && Condition == ConditionKind.D365RecordCount)
        || (Type == StepType.Dynamics && D365Action is D365Action.Cleanup or D365Action.QuickCreate)
        || UsesSecret;

    /// <summary>Có trường nào chứa {{secret:…}}.</summary>
    [JsonIgnore]
    public bool UsesSecret => new[] { Target, Text, Arguments, Headers, RowRef, Message, Form }
        .Any(f => f?.Contains("{{secret:", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Đang dựng mô tả đầy đủ (<see cref="FullDescribe"/>) — <see cref="Short"/> không cắt chữ.</summary>
    [ThreadStatic] private static bool _fullText;

    /// <summary>
    /// Như <see cref="Describe"/> nhưng KHÔNG cắt ngắn: lệnh, đường dẫn + tham số, phương thức + URL + nội dung, file ghi, chữ gõ, giá trị
    /// gán biến, điều kiện… (xuống dòng thành " ⏎ ", ký tự điều khiển vô hình hiện thành ⟦U+…⟧). Không che bí mật — người gọi dùng <c>Log.Redact</c>.
    /// </summary>
    public string FullDescribe()
    {
        bool outer = _fullText;
        _fullText = true;
        try { return FullDescribeCore(); }
        finally { _fullText = outer; }
    }

    private string FullDescribeCore()
    {
        if (!IsRisky)
            return Type == StepType.Assert && !string.IsNullOrWhiteSpace(Message)
                ? $"Kiểm tra: {Short(Message)} ({DescribeCondition()})" // nhãn tự đặt có thể che điều kiện thật
                : Describe();
        static string One(string s) => Visible(s.Replace("\r", "").Replace("\n", " ⏎ "));
        var window = string.IsNullOrWhiteSpace(Target) ? " (vào cửa sổ đang dùng)" : $" vào \"{Target}\"";
        return Type switch
        {
            StepType.RunCommand => $"Chạy lệnh: {One(Target)}" + IntoVar(),
            StepType.SetVariable => $"{{{{{Variable}}}}} ← output lệnh: {One(Target)}",
            StepType.LaunchApp => $"Mở \"{Target}\"" + (string.IsNullOrWhiteSpace(Arguments) ? "" : $" với tham số: {One(Arguments)}"),
            StepType.HttpRequest => $"{(string.IsNullOrWhiteSpace(Method) ? "GET" : Method.ToUpperInvariant())} " +
                                    (string.IsNullOrWhiteSpace(Connection) ? "" : $"[kết nối {Connection}] ") + One(Target) +
                                    (string.IsNullOrWhiteSpace(Headers) ? "" : $" · header: {One(Headers)}") +
                                    (string.IsNullOrWhiteSpace(Text) ? "" : $" · nội dung gửi: {One(Text)}") + IntoVar(),
            StepType.WriteData => (DataAction switch
                                  {
                                      DataAction.WriteText => "Ghi đè file",
                                      DataAction.AppendText => "Thêm vào cuối file",
                                      DataAction.AppendRow => "Thêm dòng vào file",
                                      _ => $"Sửa dòng [{One(RowRef)}] của file"
                                  }) + $" \"{Target}\"" + (string.IsNullOrWhiteSpace(Arguments) || IsTextWrite ? "" : $" [sheet {Arguments}]") + $": {One(Text)}",
            StepType.TypeText => $"Gõ \"{One(Text)}\"" + window,
            StepType.KeyPress => $"Nhấn {One(Text)}" + window,
            StepType.SetElementText => $"Nhập \"{One(Arguments)}\" vào [{One(Text)}]" + SearchArea(),
            StepType.Browser => BrowserAction switch
            {
                BrowserAction.Launch => $"Mở {(string.IsNullOrWhiteSpace(Target) ? "Chrome" : Target)} chế độ điều khiển{(Force ? " (ẩn)" : "")}" +
                                        (string.IsNullOrWhiteSpace(Arguments) ? "" : $" · hồ sơ \"{One(Arguments)}\"") +
                                        (string.IsNullOrWhiteSpace(Text) ? "" : $" → {One(Text)}"),
                BrowserAction.Navigate => $"Mở trang {One(Text)}" + InTab(),
                BrowserAction.SetValue => $"Nhập \"{One(Arguments)}\" vào \"{One(Text)}\"" + InTab(),
                _ => $"Chạy JavaScript: {One(Text)}" + IntoVar() + InTab()
            },
            StepType.Dynamics when D365Action == D365Action.RunScript => $"D365: chạy JavaScript: {One(Text)}" + IntoVar() + InTab(),
            StepType.Dynamics => $"D365: {(string.IsNullOrWhiteSpace(Method) ? "GET" : Method.ToUpperInvariant())} {One(Arguments)}" +
                                 (string.IsNullOrWhiteSpace(Text) ? "" : $" · nội dung gửi: {One(Text)}") + IntoVar() + InTab(),
            _ => Describe()
        };
    }

    /// <summary>Gán biến: ô "Tham số" là regex trích xuất (với các nguồn đọc dữ liệu thô).</summary>
    [JsonIgnore]
    public bool UsesRegex => VarSource is VarSource.Clipboard or VarSource.Command or VarSource.ScreenText or VarSource.Element or VarSource.File;

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
            ConditionKind.BrowserElement => $"trang web có \"{Short(Text)}\"" + InTab(),
            ConditionKind.D365FieldValue => CompareOp is CompareOp.IsEmpty or CompareOp.IsNotEmpty
                ? $"field {Short(Text)} {CompareNames[CompareOp]}"
                : $"field {Short(Text)} {CompareNames[CompareOp]} \"{Short(Arguments)}\"",
            ConditionKind.D365FieldState => $"field {Short(Text)} " + (D365FieldStates.TryGetValue(Arguments.Trim(), out var st) ? st : Short(Arguments)),
            ConditionKind.D365Notification => string.IsNullOrWhiteSpace(Text) ? "form có thông báo / lỗi" : $"form có thông báo chứa \"{Short(Text)}\"",
            ConditionKind.D365RecordCount => $"số bản ghi của {Short(Text)} {CompareNames[CompareOp]} {Short(Arguments)}",
            ConditionKind.D365SubgridCount => $"số dòng subgrid {Short(Text)} {CompareNames[CompareOp]} {Short(Arguments)}",
            ConditionKind.D365SubgridRow => $"subgrid {Short(Text)} có dòng chứa \"{Short(Arguments)}\"",
            ConditionKind.D365Command => $"nút \"{Short(Text)}\" " + (D365CommandStates.TryGetValue(Arguments.Trim(), out var cs) ? cs : Short(Arguments)),
            ConditionKind.D365CurrentForm => $"form đang mở {CompareNames[CompareOp]} \"{Short(Arguments)}\"",
            ConditionKind.D365UserRole => $"người dùng có vai trò \"{Short(Text)}\"",
            _ => ""
        };
    }

    private string RowName() => string.IsNullOrWhiteSpace(RowRef) ? "1" : Short(RowRef);

    private string ViewName() => string.IsNullOrWhiteSpace(Arguments) ? "mặc định" : $"\"{Short(Arguments)}\"";

    private string SearchText() => string.IsNullOrWhiteSpace(RowRef) ? "" : $" · tìm \"{Short(RowRef)}\"";

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

    /// <summary>Thời lượng dạng đồng hồ, làm tròn tới giây: 0:12 · 4:05 · 1:02:03.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        long total = (long)Math.Round(Math.Max(0, duration.TotalSeconds), MidpointRounding.AwayFromZero);
        long h = total / 3600, m = total / 60 % 60, s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    public static string FormatDuration(int ms) => FormatDuration(TimeSpan.FromMilliseconds(ms));

    /// <summary>Tên ngắn của một dòng trong danh sách phát: tên file / thư mục (ổ đĩa "D:\" giữ nguyên).</summary>
    private static string MediaName(string line)
    {
        var path = line.Trim().Trim('"', '\'').TrimEnd('\\', '/');
        var name = Path.GetFileName(path);
        return name.Length > 0 ? name : path;
    }

    private static string Short(string s)
    {
        s = s.Replace("\r", "").Replace("\n", " ⏎ ");
        if (_fullText) return Visible(s);
        return s.Length > 50 ? s[..50] + "…" : s;
    }

    /// <summary>
    /// Ký tự định dạng vô hình (đảo chiều chữ U+202E, khoảng trắng độ rộng 0…) thành ⟦U+XXXX⟧ — để màn hình duyệt không hiện lệnh khác lệnh thật.
    /// </summary>
    internal static string Visible(string s)
    {
        if (!s.Any(c => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)) return s;
        var sb = new System.Text.StringBuilder(s.Length + 16);
        foreach (var c in s)
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) sb.Append($"⟦U+{(int)c:X4}⟧");
            else sb.Append(c);
        return sb.ToString();
    }
}
