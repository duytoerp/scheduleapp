using System.Runtime.InteropServices;
using System.Text;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.Services.Testing;

/// <summary>
/// Chạy bộ kiểm thử từ dòng lệnh (CI / Azure DevOps / Task Scheduler), không mở giao diện:
///   ScheduleApp.exe --test "Nhóm hoặc tên công việc" [--report "thư mục"]
/// Mã thoát: 0 = mọi kịch bản đạt, 1 = có kịch bản không đạt, 2 = không tìm thấy kịch bản / tham số sai.
/// </summary>
internal static class TestCli
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public static int Run(string query, string? reportRoot)
    {
        // ScheduleApp là ứng dụng cửa sổ: gắn vào console của tiến trình gọi để in kết quả.
        AttachConsole(-1);
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch (IOException) { }

        void Print(string line)
        {
            try { Console.WriteLine(line); } catch (IOException) { }
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            Print("Cách dùng: ScheduleApp.exe --test \"Nhóm hoặc tên công việc\" [--report \"thư mục báo cáo\"]   (\"*\" = mọi kịch bản kiểm thử)");
            return 2;
        }

        var jobs = JobStore.Load();
        var selected = TestSuite.Select(jobs, query);
        if (selected.Count == 0)
        {
            Print($"Không tìm thấy kịch bản kiểm thử nào khớp \"{query}\" (tên nhóm, tên công việc, hoặc \"*\").");
            return 2;
        }

        Log.Written += Print;
        var ui = new HeadlessNotifier();
        var runner = new FlowRunner(ui, id => jobs.FirstOrDefault(j => j.Id == id));
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            runner.StopAll();
        };

        var name = selected.Count == 1 && !selected[0].Group.Equals(query.Trim().Trim('"'), StringComparison.CurrentCultureIgnoreCase)
            ? selected[0].Name
            : query.Trim().Trim('"');
        var result = TestSuite.RunAsync(runner, selected, name,
            string.IsNullOrWhiteSpace(reportRoot) ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(reportRoot.Trim().Trim('"'))))
            .GetAwaiter().GetResult();
        Log.Written -= Print;

        Print("");
        foreach (var c in result.Cases)
            Print($"{(c.Ok ? "  ✔ ĐẠT      " : "  ✖ KHÔNG ĐẠT")}  {c.Name}  ({TestReport.Duration(c.Seconds)}){(c.Ok ? "" : " — " + c.Message)}");
        Print("");
        Print($"{(result.Ok ? "ĐẠT" : "KHÔNG ĐẠT")}: {TestReport.Summary(result.Cases)}");
        Print($"Báo cáo: {result.ReportPath}");
        Print($"JUnit:   {Path.Combine(Path.GetDirectoryName(result.ReportPath)!, "junit.xml")}");
        return result.Ok ? 0 : 1;
    }
}

/// <summary>Giao diện "không màn hình" cho chế độ dòng lệnh: nhắc nhở chỉ ghi log, hỏi nhập dùng giá trị mặc định.</summary>
internal sealed class HeadlessNotifier : IUserNotifier
{
    public Task ShowReminderAsync(string title, string message, bool waitForUser, CancellationToken ct)
    {
        Log.Info($"      (nhắc nhở) {title}: {message}");
        return Task.CompletedTask;
    }

    public void Notify(string title, string text, bool isError)
    {
        if (isError) Log.Warn($"{title}: {text}");
    }

    public IDisposable ClearScreenForAutomation() => new Noop();

    public Task<string?> PromptAsync(string title, string message, string defaultValue, bool password, CancellationToken ct)
    {
        if (defaultValue.Length > 0) Log.Info($"      (hỏi nhập) \"{message}\" → dùng giá trị mặc định.");
        else Log.Warn($"      (hỏi nhập) \"{message}\" — chạy dòng lệnh không hỏi được, coi như người dùng hủy.");
        return Task.FromResult<string?>(defaultValue.Length > 0 ? defaultValue : null);
    }

    public Task<DebugCommand> DebugPauseAsync(string jobName, int stepIndex, string stepText, string reason,
        IReadOnlyDictionary<string, string> variables, CancellationToken ct) => Task.FromResult(DebugCommand.Continue);

    public Task<bool> AskContinueAsync(string title, string message, CancellationToken ct) => Task.FromResult(true);

    private sealed class Noop : IDisposable
    {
        public void Dispose() { }
    }
}
