using System.Diagnostics;
using System.Net.Http;
using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.Tests;

/// <summary>Edge headless mở sẵn một trang, điều khiển qua cổng remote debugging riêng của bài kiểm thử.</summary>
internal sealed class LiveBrowser : IAsyncDisposable
{
    private readonly Process _proc;

    private LiveBrowser(Process proc) => _proc = proc;

    public static string? EdgePath => new[]
    {
        Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
        Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe")
    }.FirstOrDefault(File.Exists);

    /// <param name="profileDir">Thư mục hồ sơ Edge (null = hồ sơ mới trong thư mục tạm).</param>
    /// <param name="headless">Chạy ẩn (mặc định); false = hiện cửa sổ (vd để đăng nhập tay / quan sát).</param>
    public static async Task<LiveBrowser> StartAsync(int port, string url, string? profileDir = null, bool headless = true)
    {
        var edge = EdgePath;
        Assert.True(edge != null, "Máy không có Microsoft Edge.");
        SettingsStore.Current.BrowserPort = port;
        var profile = profileDir ?? Path.Combine(TestSupport.NewDir(), "edge-" + port);
        var proc = Process.Start(new ProcessStartInfo(edge!,
            $"{(headless ? "--headless=new --window-size=1920,1080 " : "")}--remote-debugging-port={port} --user-data-dir=\"{profile}\" --no-first-run --disable-extensions \"{url}\"") { UseShellExecute = false })!;
        BrowserClient.Track(proc, port, profile);   // chỉ cổng do đúng tiến trình này lắng nghe mới được kết nối
        using var http = new HttpClient();
        for (int i = 0; i < 50; i++)
        {
            try { if ((await http.GetAsync($"http://127.0.0.1:{port}/json/version")).IsSuccessStatusCode) break; } catch (HttpRequestException) { }
            await Task.Delay(200);
        }
        await Task.Delay(1000);
        return new LiveBrowser(proc);
    }

    /// <summary>Chạy JavaScript trong tab của máy chủ giả lập (localhost) — Edge có thể tự mở thêm tab khác (trang chào của tiện ích…).</summary>
    public static Task<string> JsAsync(string code) => BrowserClient.EvalAsync("localhost", code, CancellationToken.None);

    /// <summary>Chuyển tab sang <paramref name="url"/> và chờ trang tải xong.</summary>
    public static async Task GoAsync(string url)
    {
        await JsAsync($"location.href = {System.Text.Json.JsonSerializer.Serialize(url)}; 1");
        await Task.Delay(800);
        for (int i = 0; i < 30 && await JsAsync("document.readyState") != "complete"; i++) await Task.Delay(100);
    }

    public ValueTask DisposeAsync()
    {
        try { _proc.Kill(true); } catch (InvalidOperationException) { }
        _proc.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Chạy từng bước Dynamics 365 / Kiểm tra trong một FlowContext dùng chung (như một flow).</summary>
internal sealed class D365Steps
{
    private readonly Job _job = new() { Name = "d365" };

    public D365Steps() => Ctx = new FlowContext(_job, new FakeUi(), RunOptions.Default, _ => null, CancellationToken.None);

    public FlowContext Ctx { get; }

    public string this[string variable] => Ctx.Vars[variable];

    public Task Do(D365Action a, string text = "", string args = "", string variable = "", string rowRef = "", string form = "",
        bool force = false, string method = "GET", int timeout = 5000)
    {
        var s = ActionStep.CreateDefault(StepType.Dynamics);
        s.D365Action = a; s.Text = text; s.Arguments = args; s.Variable = variable; s.RowRef = rowRef; s.Form = form;
        s.Force = force; s.Method = method; s.DelayMs = timeout;
        return StepExecutor.ExecuteAsync(Ctx.ExpandStep(s), _job, Ctx);
    }

    public Task Check(ConditionKind kind, string text, string args = "", CompareOp op = CompareOp.Equals, bool negate = false, int wait = 2000)
    {
        var s = ActionStep.CreateDefault(StepType.Assert);
        s.Condition = kind; s.Text = text; s.Arguments = args; s.CompareOp = op; s.Negate = negate; s.DelayMs = wait;
        return StepExecutor.ExecuteAsync(Ctx.ExpandStep(s), _job, Ctx);
    }

    public Task<string> Js(string code) => LiveBrowser.JsAsync(code);
}
