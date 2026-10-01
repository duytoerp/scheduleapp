using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;

// Các bài kiểm thử dùng chung trạng thái tĩnh (cài đặt, bí mật, thư mục dữ liệu) → chạy tuần tự.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ScheduleApp.Tests;

internal static class TestSupport
{
    /// <summary>Thư mục dữ liệu tạm cho cả lần chạy kiểm thử (không đụng tới %AppData%\ScheduleApp thật).</summary>
    public static string DataDir { get; private set; } = "";

    [ModuleInitializer]
    internal static void Init()
    {
        DataDir = Path.Combine(Path.GetTempPath(), "ScheduleAppTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DataDir);
        Environment.SetEnvironmentVariable("SCHEDULEAPP_DATA_DIR", DataDir);
        SettingsStore.Current.ScreenshotOnError = false;
        SettingsStore.Current.PreventSleepWhileRunning = false;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(DataDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
    }

    /// <summary>Thư mục tạm riêng cho một bài kiểm thử.</summary>
    public static string NewDir()
    {
        var dir = Path.Combine(DataDir, "t" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static ActionStep S(StepType t, Action<ActionStep>? cfg = null)
    {
        var s = ActionStep.CreateDefault(t);
        s.DelayAfterMs = 0;
        cfg?.Invoke(s);
        return s;
    }

    public static ActionStep SetVar(string name, VarSource src, string text, string target = "", string args = "") =>
        S(StepType.SetVariable, s => { s.Variable = name; s.VarSource = src; s.Text = text; s.Target = target; s.Arguments = args; });

    public static async Task<(FlowResult R, FlowContext C)> RunAsync(Job job, params Job[] others)
    {
        var all = others.Append(job).ToList();
        var ctx = new FlowContext(job, new FakeUi(), RunOptions.Default, id => all.FirstOrDefault(j => j.Id == id), CancellationToken.None);
        var r = await FlowEngine.RunAsync(job, ctx, 0, true);
        return (r, ctx);
    }

    /// <summary>Tạo file .xlsx mẫu: sharedStrings, ô ngày, ô trống, dòng trống, inline string, 2 sheet.</summary>
    public static void MakeXlsx(string path)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>");
        Add("xl/workbook.xml", $"<workbook xmlns=\"{ns}\" xmlns:r=\"{r}\"><sheets><sheet name=\"Danh sách\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Khác\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>");
        Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Target=\"/xl/worksheets/sheet2.xml\"/></Relationships>");
        Add("xl/sharedStrings.xml", $"<sst xmlns=\"{ns}\"><si><t>Mã</t></si><si><t>Họ tên</t></si><si><t>Ngày</t></si><si><r><t>Số </t></r><r><t>tiền</t></r></si><si><t>Phạm Văn D</t></si></sst>");
        Add("xl/styles.xml", $"<styleSheet xmlns=\"{ns}\"><numFmts><numFmt numFmtId=\"164\" formatCode=\"dd/mm/yyyy\"/></numFmts><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"164\"/></cellXfs></styleSheet>");
        Add("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{ns}\"><dimension ref=\"A1:E4\"/><sheetData>" +
            "<row r=\"1\" spans=\"1:5\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c><c r=\"D1\" t=\"s\"><v>3</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\"><v>1</v></c><c r=\"B2\" t=\"s\"><v>4</v></c><c r=\"C2\" s=\"1\"><v>46096</v></c><c r=\"D2\"><v>1500000.25</v></c></row>" +
            "<row r=\"4\"><c r=\"A4\" t=\"str\"><v>2</v></c><c r=\"D4\"><f>D2*2</f><v>3000000.5</v></c><c r=\"E4\" t=\"inlineStr\"><is><t>ghi chú</t></is></c></row>" +
            "</sheetData></worksheet>");
        Add("xl/worksheets/sheet2.xml", $"<worksheet xmlns=\"{ns}\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Z</t></is></c></row></sheetData></worksheet>");
    }
}

/// <summary>Giao diện giả: nhắc nhở và hỏi đáp trả lời ngay, không hiện cửa sổ.</summary>
internal sealed class FakeUi : IUserNotifier
{
    public List<string> Reminders { get; } = [];
    public Task ShowReminderAsync(string title, string message, bool waitForUser, CancellationToken ct) { Reminders.Add(message); return Task.CompletedTask; }
    public void Notify(string title, string text, bool isError) { }
    public IDisposable ClearScreenForAutomation() => new Noop();
    public Task<string?> PromptAsync(string title, string message, string defaultValue, bool password, CancellationToken ct) => Task.FromResult<string?>("nhập: " + defaultValue);
    public Task<DebugCommand> DebugPauseAsync(string jobName, int stepIndex, string stepText, string reason, IReadOnlyDictionary<string, string> variables, CancellationToken ct) => Task.FromResult(DebugCommand.Continue);
    public Task<bool> AskContinueAsync(string title, string message, CancellationToken ct) => Task.FromResult(true);
    private sealed class Noop : IDisposable { public void Dispose() { } }
}

/// <summary>Chỉ chạy khi đặt biến môi trường SCHEDULEAPP_LIVE_TESTS=1 (cần màn hình / trình duyệt Edge).</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCHEDULEAPP_LIVE_TESTS") != "1")
            Skip = "Bài kiểm thử cần màn hình/trình duyệt — đặt SCHEDULEAPP_LIVE_TESTS=1 để chạy.";
    }
}
