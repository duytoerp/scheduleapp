using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.Tests;

/// <summary>Bước "Mở trình duyệt" chạy ẩn (headless): không cửa sổ, khung trang rộng như màn hình Full HD.</summary>
public sealed class HeadlessLiveTests
{
    [LiveFact]
    public async Task LaunchStepCanRunHeadlessWithFullHdViewport()
    {
        Assert.True(LiveBrowser.EdgePath != null, "Máy không có Microsoft Edge.");
        using var server = new MiniHttpServer((_, _, _, _) => (200, "<html><body>ok</body></html>"));
        var host = new Uri(server.BaseUrl).Authority;
        SettingsStore.Current.BrowserPort = 9339;
        var job = new Job { Name = "headless" };
        var ctx = new FlowContext(job, new FakeUi(), RunOptions.Default, _ => null, CancellationToken.None);
        var step = ActionStep.CreateDefault(StepType.Browser);
        step.BrowserAction = BrowserAction.Launch;
        step.Target = "Edge";
        step.Text = server.BaseUrl;
        step.Arguments = "An headless";
        step.Force = true;
        Assert.Contains("chế độ điều khiển (ẩn)", step.Describe());
        try
        {
            await StepExecutor.ExecuteAsync(ctx.ExpandStep(step), job, ctx);
            // --window-size đặt kích thước cửa sổ (kể cả khung); vùng trang hẹp hơn một chút — vẫn đủ rộng để thanh lệnh D365 không bị thu gọn.
            Assert.Equal("1920", await BrowserClient.EvalAsync(host, "String(window.outerWidth)", CancellationToken.None));
            Assert.True(int.Parse(await BrowserClient.EvalAsync(host, "String(window.innerWidth)", CancellationToken.None)) > 1800);
            Assert.Contains("Headless", await BrowserClient.EvalAsync(host, "navigator.userAgent", CancellationToken.None));
        }
        finally
        {
            foreach (var d in BrowserProfiles.AllDirs().Where(BrowserProfiles.InUse).ToList())
            {
                try { await BrowserClient.CloseBrowserAsync(d, CancellationToken.None); }
                catch (TimeoutException) { }
            }
            try { BrowserClient.LastLaunched?.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* đã thoát */ }
        }
    }
}
