using System.Text.Json;

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
    WaitForText
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

/// <summary>Một bước trong flow tự động. Ý nghĩa các trường phụ thuộc vào <see cref="Type"/>.</summary>
public sealed class ActionStep
{
    public StepType Type { get; set; } = StepType.LaunchApp;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Đường dẫn ứng dụng / tiêu đề cửa sổ / tên tiến trình / lệnh / tiêu đề nhắc nhở.
    /// Với bước nhận dạng màn hình: cửa sổ giới hạn vùng tìm (trống = cả màn hình).
    /// </summary>
    public string Target { get; set; } = "";

    /// <summary>Tham số dòng lệnh khi mở ứng dụng.</summary>
    public string Arguments { get; set; } = "";

    /// <summary>Văn bản cần gõ / nội dung nhắc nhở / tổ hợp phím / chữ cần tìm (OCR).</summary>
    public string Text { get; set; } = "";

    /// <summary>Tọa độ click; với bước nhận dạng màn hình là độ lệch so với tâm vùng tìm thấy.</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public MouseButtonKind Button { get; set; } = MouseButtonKind.Left;
    public bool DoubleClick { get; set; }

    /// <summary>Thời gian chờ (bước Chờ) hoặc timeout (chờ cửa sổ, chạy lệnh, nhận dạng), tính bằng ms.</summary>
    public int DelayMs { get; set; } = 1000;

    /// <summary>Nhắc nhở: tạm dừng flow cho tới khi người dùng xác nhận.</summary>
    public bool WaitForUser { get; set; }

    /// <summary>Đóng ứng dụng: kill tiến trình thay vì yêu cầu đóng lịch sự.</summary>
    public bool Force { get; set; }

    /// <summary>Thời gian nghỉ sau khi hoàn thành bước (ms).</summary>
    public int DelayAfterMs { get; set; } = 500;

    /// <summary>Hình mẫu (PNG, base64) cho bước nhận dạng hình ảnh.</summary>
    public string ImageData { get; set; } = "";
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    /// <summary>Độ khớp tối thiểu của hình mẫu, tính theo %.</summary>
    public int Confidence { get; set; } = 85;

    /// <summary>Chọn lần xuất hiện thứ mấy khi chữ cần tìm xuất hiện nhiều chỗ (bắt đầu từ 1).</summary>
    public int MatchIndex { get; set; } = 1;

    /// <summary>Gõ văn bản: gõ từng phím hay dán qua clipboard.</summary>
    public TypeMode TypeMode { get; set; } = TypeMode.Auto;

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
        [StepType.WaitForText] = "Chờ chữ xuất hiện (OCR)"
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
            _ => 1_000
        },
        WaitForUser = type == StepType.Reminder
    };

    public ActionStep Clone() =>
        JsonSerializer.Deserialize<ActionStep>(JsonSerializer.Serialize(this, JsonDefaults.Options), JsonDefaults.Options)!;

    /// <summary>Bước này có giả lập chuột/bàn phím hay không.</summary>
    public bool UsesInput => Type is StepType.MouseClick or StepType.TypeText or StepType.KeyPress
        or StepType.ClickImage or StepType.ClickText;

    /// <summary>Bước này chụp màn hình để nhận dạng.</summary>
    public bool UsesScreen => Type is StepType.ClickImage or StepType.WaitForImage or StepType.ClickText or StepType.WaitForText;

    public bool IsImageStep => Type is StepType.ClickImage or StepType.WaitForImage;
    public bool IsTextStep => Type is StepType.ClickText or StepType.WaitForText;

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
        StepType.RunCommand => $"Chạy lệnh: {Short(Target)}",
        StepType.CloseApp => $"Đóng \"{Target}\"{(Force ? " (buộc đóng)" : "")}",
        StepType.ClickImage => $"{ClickName()} vào hình mẫu {ImageWidth}×{ImageHeight} (khớp ≥ {Confidence}%)" + SearchArea(),
        StepType.WaitForImage => $"Chờ hình mẫu {ImageWidth}×{ImageHeight} xuất hiện (tối đa {FormatMs(DelayMs)})" + SearchArea(),
        StepType.ClickText => $"{ClickName()} vào chữ \"{Short(Text)}\"" + (MatchIndex > 1 ? $" (lần thứ {MatchIndex})" : "") + SearchArea(),
        StepType.WaitForText => $"Chờ chữ \"{Short(Text)}\" xuất hiện (tối đa {FormatMs(DelayMs)})" + SearchArea(),
        _ => Type.ToString()
    };

    private string ClickName() => DoubleClick ? "Double-click" : "Click";

    private string InWindow() => string.IsNullOrWhiteSpace(Target) ? "" : $" vào \"{Target}\"";

    private string SearchArea() => string.IsNullOrWhiteSpace(Target) ? "" : $" trong \"{Target}\"";

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
