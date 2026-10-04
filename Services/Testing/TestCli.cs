using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.Services.Testing;

/// <summary>Tham số dòng lệnh của chế độ chạy kiểm thử.</summary>
internal sealed record TestCliOptions
{
    /// <summary>Tên nhóm / tên công việc / "*" (trống = mọi kịch bản, thường dùng cùng --tag).</summary>
    public string Query { get; init; } = "";
    public string? Tags { get; init; }
    public string? Environment { get; init; }
    public string? TestDir { get; init; }
    public string? ReportDir { get; init; }
    public string? Shard { get; init; }
    public int Retries { get; init; }
    public bool Headless { get; init; }
    public bool ListOnly { get; init; }

    public const string Usage =
        "Cách dùng: ScheduleApp.exe --test \"Nhóm hoặc tên công việc\" [--tag smoke] [--env UAT] [--test-dir \"thư mục kịch bản\"]\n" +
        "                           [--retry 1] [--shard 1/3] [--headless] [--report \"thư mục báo cáo\"] [--list]\n" +
        "  \"*\" hoặc bỏ trống tên = mọi kịch bản kiểm thử. Mã thoát: 0 = đạt, 1 = có kịch bản không đạt, 2 = không có kịch bản / tham số sai.";

    /// <summary>Đọc tham số; sai cú pháp thì báo <see cref="FormatException"/>.</summary>
    public static TestCliOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return null;
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return name == "--test" ? "" : throw new FormatException($"Thiếu giá trị sau {name}.");
            return args[i + 1].Trim().Trim('"');
        }
        bool Flag(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        int retries = 0;
        if (Value("--retry") is { } r && (!int.TryParse(r, NumberStyles.Integer, CultureInfo.InvariantCulture, out retries) || retries is < 0 or > 5))
            throw new FormatException($"--retry \"{r}\" không hợp lệ — dùng số từ 0 tới 5.");
        var shard = Value("--shard");
        if (shard != null) TestSuite.ParseShard(shard);
        return new TestCliOptions
        {
            Query = Value("--test") ?? "",
            Tags = Value("--tag"),
            Environment = Value("--env"),
            TestDir = Value("--test-dir") is { } d ? Path.GetFullPath(System.Environment.ExpandEnvironmentVariables(d)) : null,
            ReportDir = Value("--report") is { } rep ? Path.GetFullPath(System.Environment.ExpandEnvironmentVariables(rep)) : null,
            Shard = shard,
            Retries = retries,
            Headless = Flag("--headless"),
            ListOnly = Flag("--list")
        };
    }

    /// <summary>Chọn kịch bản: theo tên nhóm / tên, rồi lọc tag, rồi chia shard.</summary>
    public List<Job> Select(IEnumerable<Job> jobs)
    {
        var selected = TestSuite.WithTags(TestSuite.Select(jobs, Query), Tags);
        return Shard == null ? selected : TestSuite.Shard(selected, Shard);
    }

    /// <summary>Tên bộ kiểm thử trong báo cáo.</summary>
    public string SuiteName(IReadOnlyList<Job> selected)
    {
        var q = Query.Trim();
        var name = selected.Count == 1 && !selected[0].Group.Equals(q, StringComparison.CurrentCultureIgnoreCase) && q is not ("" or "*")
            ? selected[0].Name
            : q is "" or "*" ? "Tất cả kịch bản" : q;
        if (!string.IsNullOrWhiteSpace(Tags)) name += $" [tag {Tags.Trim()}]";
        if (Shard != null) name += $" (phần {Shard})";
        return name;
    }
}

/// <summary>
/// Chạy bộ kiểm thử từ dòng lệnh (CI / Azure DevOps / Task Scheduler), không mở giao diện:
///   ScheduleApp.exe --test "Nhóm hoặc tên công việc" [--tag …] [--env …] [--test-dir …] [--retry N] [--shard i/n] [--headless] [--report "thư mục"]
/// Mã thoát: 0 = mọi kịch bản đạt, 1 = có kịch bản không đạt, 2 = không tìm thấy kịch bản / tham số sai.
/// </summary>
internal static class TestCli
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    public static int Run(string[] args)
    {
        // ScheduleApp là ứng dụng cửa sổ: gắn vào console của tiến trình gọi để in kết quả.
        AttachConsole(-1);
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch (IOException) { }
        return Run(args, Print);

        static void Print(string line)
        {
            try { Console.WriteLine(line); } catch (IOException) { }
        }
    }

    /// <summary>Chạy với nơi in kết quả tùy chọn (kiểm thử dùng để đọc lại output).</summary>
    internal static int Run(string[] args, Action<string> print)
    {
        TestCliOptions o;
        List<Job> jobs;
        List<TestEnvironment> environments;
        try
        {
            o = TestCliOptions.Parse(args);
            jobs = o.TestDir != null ? TestFolder.Load(o.TestDir) : JobStore.Load();
            // Môi trường: environments.json của thư mục kịch bản (nếu có) ghi đè môi trường cùng tên trong Cài đặt.
            environments = [.. SettingsStore.Current.Environments];
            if (o.TestDir != null)
                foreach (var env in TestFolder.LoadEnvironments(o.TestDir))
                {
                    environments.RemoveAll(e => e.Name.Trim().Equals(env.Name.Trim(), StringComparison.CurrentCultureIgnoreCase));
                    environments.Add(env);
                }
        }
        catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            print(ex.Message);
            print(TestCliOptions.Usage);
            return 2;
        }
        // File dữ liệu hỏng (đã khôi phục từ .bak, bỏ qua công việc lỗi…): in ra để thấy ngay trong log CI — không hiện hộp thoại.
        foreach (var issue in DataIssues.Take()) print("⚠ " + issue);

        TestEnvironment? environment;
        try { environment = TestEnvironments.Find(environments, o.Environment); }
        catch (InvalidOperationException ex)
        {
            print(ex.Message);
            return 2;
        }

        var selected = o.Select(jobs);
        if (selected.Count == 0)
        {
            print($"Không tìm thấy kịch bản kiểm thử nào khớp \"{o.Query}\"" + (o.Tags != null ? $" với tag \"{o.Tags}\"" : "") +
                  (o.Shard != null ? $" trong phần {o.Shard}" : "") + " (tên nhóm, tên công việc, hoặc \"*\").");
            return 2;
        }
        if (o.ListOnly)
        {
            foreach (var j in selected)
                print($"{(j.Group.Length > 0 ? j.Group + " › " : "")}{j.Name}" + (j.Tags.Length > 0 ? $"  [{string.Join(", ", j.TagList)}]" : "") +
                      (j.DataFile.Length > 0 ? $"  (dữ liệu: {j.DataFile})" : ""));
            print($"{selected.Count} kịch bản.");
            return 0;
        }

        BrowserClient.ForceHeadless = o.Headless;
        Log.Written += print;
        var runner = new FlowRunner(new HeadlessNotifier(), id => jobs.FirstOrDefault(j => j.Id == id));
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            runner.StopAll();
        };
        Console.CancelKeyPress += cancel;
        SuiteResult result;
        try
        {
            result = TestSuite.RunAsync(runner, selected, o.SuiteName(selected), o.ReportDir, new SuiteOptions
            {
                Environment = environment?.Name,
                Variables = TestEnvironments.Variables(environment),
                Retries = o.Retries,
                BaseDir = o.TestDir
            }).GetAwaiter().GetResult();
        }
        finally
        {
            Log.Written -= print;
            Console.CancelKeyPress -= cancel;
            BrowserClient.ForceHeadless = false;
        }

        print("");
        foreach (var c in result.Cases)
            print($"{(c.Ok ? "  ✔ ĐẠT      " : "  ✖ KHÔNG ĐẠT")}  {c.Name}  ({TestReport.Duration(c.Seconds)})" +
                  (c.Flaky ? $" — đạt ở lần chạy thứ {c.Attempts}" : c.Ok ? "" : " — " + c.FailureSummary));
        print("");
        print($"{(result.Ok ? "ĐẠT" : "KHÔNG ĐẠT")}: {TestReport.Summary(result.Cases)}");
        print($"Báo cáo: {result.ReportPath}");
        print($"JUnit:   {Path.Combine(Path.GetDirectoryName(result.ReportPath)!, "junit.xml")}");
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
