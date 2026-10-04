using System.Diagnostics;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>Bước "Thu nhỏ cửa sổ" nhớ đúng cửa sổ vào biến, "Kích hoạt cửa sổ" với biến đó mở lại; mẫu 15:30 phát 2 video.</summary>
public class MinimizeWindowTests
{
    [Fact]
    public async Task MinimizesWindowAndReopensTheSameOneFromVariable()
    {
        // Cửa sổ của tiến trình khác (cửa sổ của chính ScheduleApp luôn bị bỏ qua), đặt ngoài màn hình.
        var title = "SA-thu-nho-" + Guid.NewGuid().ToString("N")[..8];
        using var proc = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form; " +
            $"$f.Text = '{title}'; $f.StartPosition = 'Manual'; $f.Location = New-Object System.Drawing.Point(-3000, -3000); $f.ShowDialog()\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var h = await WindowHelper.WaitForAsync(title, 20_000, CancellationToken.None);
            Assert.False(Win32.IsIconic(h));

            var job = new Job
            {
                Steps =
                [
                    S(StepType.MinimizeWindow, s => { s.Target = title; s.Variable = "ungDungTruoc"; s.DelayMs = 5000; }),
                    S(StepType.LogMessage, s => s.Text = "nhớ: {{ungDungTruoc}} / {{lastWindow}}"),
                ]
            };
            Assert.Equal($"Thu nhỏ cửa sổ \"{title}\" → {{{{ungDungTruoc}}}}", job.Steps[0].Describe());
            var (r, ctx) = await RunAsync(job);
            Assert.True(r.Ok, r.Message);
            Assert.True(Win32.IsIconic(h));
            Assert.Equal("hwnd:" + h.ToInt64(), ctx.Vars["ungDungTruoc"]);
            Assert.Equal(ctx.Vars["ungDungTruoc"], ctx.Vars["lastWindow"]);

            // Mở lại theo handle đã nhớ — không phụ thuộc tiêu đề.
            (r, _) = await RunAsync(new Job
            {
                Variables = [new VariableDef { Name = "ungDungTruoc", Value = ctx.Vars["ungDungTruoc"] }],
                Steps = [S(StepType.FocusWindow, s => { s.Target = "{{ungDungTruoc}}"; s.DelayMs = 5000; })]
            });
            Assert.True(r.Ok, r.Message);
            Assert.False(Win32.IsIconic(h));

            // Cửa sổ đã đóng → handle cũ không còn khớp.
            proc.Kill(true);
            proc.WaitForExit(10_000);
            Assert.Equal(IntPtr.Zero, WindowHelper.Find(WindowHelper.HandleRef(h)));
        }
        finally
        {
            if (!proc.HasExited) proc.Kill(true);
        }
    }

    [Fact]
    public void ActiveUserWindowIsNeverThisAppOrMinimized()
    {
        var h = WindowHelper.ActiveUserWindow();
        if (h == IntPtr.Zero) return;   // máy chạy test không có cửa sổ ứng dụng nào
        Assert.False(WindowHelper.BelongsToThisApp(h));
        Assert.False(Win32.IsIconic(h));
        Assert.Equal(IntPtr.Zero, WindowHelper.Find("hwnd:abc"));
    }

    [Fact]
    public void VideoTemplateHidesPlaysTwoVideosTenSecondsApartThenReopens()
    {
        var resource = typeof(Job).Assembly.GetManifestResourceNames().Single(n => n.EndsWith("vi-du-mau.json"));
        using var stream = typeof(Job).Assembly.GetManifestResourceStream(resource)!;
        var json = new StreamReader(stream).ReadToEnd();
        var job = JobStore.ImportJson(json).Single(j => j.Name.StartsWith("15 · 15:30"));

        Assert.Equal(ScheduleType.Daily, job.Schedule.Type);
        // Mẫu lưu giờ Việt Nam (+07:00); đọc ra theo múi giờ của máy chạy test.
        Assert.Equal(DateTimeOffset.Parse("2026-10-04T15:30:00+07:00").LocalDateTime.TimeOfDay, job.Schedule.StartAt.TimeOfDay);
        Assert.Equal(
            [StepType.MinimizeWindow, StepType.PlayMedia, StepType.Wait, StepType.PlayMedia, StepType.Label,
             StepType.If, StepType.FocusWindow, StepType.EndIf],
            job.Steps.Select(s => s.Type));
        Assert.True(FlowStructure.Build(job.Steps).IsValid);
        Assert.Equal("ungDungTruoc", job.Steps[0].Variable);
        Assert.Equal("", job.Steps[0].Target);                     // cửa sổ đang dùng lúc 15:30
        Assert.Equal("{{video1}}", job.Steps[1].Text);
        Assert.Equal(10_000, job.Steps[2].DelayMs);
        Assert.Equal("{{video2}}", job.Steps[3].Text);
        Assert.Equal("moLai", job.Steps[4].Target);
        // Chỉ mở lại khi cửa sổ đã nhớ còn đó: lúc 15:30 không có cửa sổ nào (biến rỗng) / ứng dụng đã bị đóng → bỏ qua, không lỗi.
        Assert.Equal(ConditionKind.WindowExists, job.Steps[5].Condition);
        Assert.Equal("{{ungDungTruoc}}", job.Steps[5].Target);
        Assert.False(job.Steps[5].Negate);
        Assert.Equal("{{ungDungTruoc}}", job.Steps[6].Target);
        // Esc khi đang phát → vẫn mở lại ứng dụng.
        Assert.All(job.Steps.Where(s => s.Type == StepType.PlayMedia), s =>
        {
            Assert.True(s.Force);
            Assert.Equal(ErrorAction.GotoLabel, s.OnError);
            Assert.Equal(job.Steps[4].Target, s.ErrorLabel);
        });

        // Thiết lập mẫu hỏi 2 file video.
        var items = TemplateSetup.Collect([job], remember: false);
        Assert.Equal(["video1", "video2"], items.Where(i => i.NeedsInput).Select(i => i.Key));
    }

    private static Job VideoTemplate()
    {
        var resource = typeof(Job).Assembly.GetManifestResourceNames().Single(n => n.EndsWith("vi-du-mau.json"));
        using var stream = typeof(Job).Assembly.GetManifestResourceStream(resource)!;
        return JobStore.ImportJson(new StreamReader(stream).ReadToEnd()).Single(j => j.Name.StartsWith("15 · 15:30"));
    }

    [Fact]
    public async Task VideoTemplateRunsWhenNoAppWindowIsOpen()
    {
        // Lúc 15:30 chỉ có màn hình nền (mọi cửa sổ đã thu nhỏ / chỉ có ScheduleApp): "Thu nhỏ cửa sổ" không có gì để ẩn.
        // Bỏ 2 bước phát video và bước chờ (không phát video thật khi chạy test) — phần còn lại của mẫu giữ nguyên.
        var job = VideoTemplate();
        job.Steps.RemoveAll(s => s.Type is StepType.PlayMedia or StepType.Wait);
        WindowHelper.ActiveUserWindowOverride.Value = () => IntPtr.Zero;
        try
        {
            var (r, ctx) = await RunAsync(job);
            Assert.True(r.Ok, r.Message);                          // không dừng ở bước đầu, không lỗi ở bước mở lại
            Assert.Equal("", ctx.Vars["ungDungTruoc"]);
            Assert.Equal("", ctx.Vars["lastWindow"]);
        }
        finally
        {
            WindowHelper.ActiveUserWindowOverride.Value = null;
        }

        // Ứng dụng đã nhớ bị đóng trong lúc phát video → handle cũ không còn → bỏ qua bước mở lại, không lỗi.
        var reopen = VideoTemplate();
        reopen.Steps.RemoveRange(0, reopen.Steps.FindIndex(s => s.Type == StepType.Label));
        reopen.Variables.Add(new VariableDef { Name = "ungDungTruoc", Value = "hwnd:1" });
        Assert.False(Win32.IsWindow(1));
        var (r2, _) = await RunAsync(reopen);
        Assert.True(r2.Ok, r2.Message);
    }

    /// <summary>
    /// Cửa sổ chính + hộp thoại có thanh tiêu đề thuộc nó + popup không tiêu đề thuộc nó, của một tiến trình PowerShell riêng, ngoài màn hình.
    /// </summary>
    private static Process StartWindowsWithDialog(string main, string dialog, string popup) =>
        Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; " +
            $"$f = New-Object System.Windows.Forms.Form; $f.Text = '{main}'; $f.StartPosition = 'Manual'; $f.Location = New-Object System.Drawing.Point(-3000, -3000); " +
            "$f.Add_Shown({ " +
            $"$d = New-Object System.Windows.Forms.Form; $d.Text = '{dialog}'; $d.FormBorderStyle = 'FixedDialog'; $d.ShowInTaskbar = $false; " +
            "$d.StartPosition = 'Manual'; $d.Location = New-Object System.Drawing.Point(-2900, -2900); $d.Show($f); " +
            $"$p = New-Object System.Windows.Forms.Form; $p.Text = '{popup}'; $p.FormBorderStyle = 'None'; $p.ShowInTaskbar = $false; " +
            "$p.StartPosition = 'Manual'; $p.Location = New-Object System.Drawing.Point(-2800, -2800); $p.Show($f) }); " +
            "$f.ShowDialog()\"")
        { UseShellExecute = false, CreateNoWindow = true })!;

    [Fact]
    public async Task EmptyTargetPicksTheAppWindowThatOwnsTheActiveDialog()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        string main = "SA-chu-" + id, dialog = "SA-hop-thoai-" + id, popup = "SA-popup-" + id;
        using var proc = StartWindowsWithDialog(main, dialog, popup);
        try
        {
            var d = await WindowHelper.WaitForAsync(dialog, 20_000, CancellationToken.None);
            var p = await WindowHelper.WaitForAsync(popup, 20_000, CancellationToken.None);
            var m = WindowHelper.GetOpenWindows().Single(w => w.Title == main).Handle;
            Assert.False(WindowHelper.IsPopup(d));                 // có thanh tiêu đề
            Assert.True(WindowHelper.IsPopup(p));                  // không tiêu đề + có chủ (như cửa sổ trình chiếu)
            Assert.Equal(m, WindowHelper.OwnerWindow(d));

            List<WindowInfo> Mine() => WindowHelper.GetOpenWindows().Where(w =>
            {
                Win32.GetWindowThreadProcessId(w.Handle, out uint pid);
                return pid == (uint)proc.Id;
            }).ToList();

            // Đang chọn hộp thoại → cửa sổ ứng dụng chủ; đang chọn popup không tiêu đề → chính nó; đang chọn cửa sổ chính → nó.
            Assert.Equal(m, WindowHelper.ChooseUserWindow(WindowHelper.GetOpenWindows(), d));
            Assert.Equal(p, WindowHelper.ChooseUserWindow(WindowHelper.GetOpenWindows(), p));
            Assert.Equal(m, WindowHelper.ChooseUserWindow(WindowHelper.GetOpenWindows(), m));
            // Cửa sổ đang chọn không thuộc danh sách (vd ScheduleApp) → cửa sổ chính trên cùng; hộp thoại (nằm trên chủ) → chủ của nó.
            Assert.Equal(m, WindowHelper.ChooseUserWindow(Mine(), IntPtr.Zero));
            Assert.Equal(IntPtr.Zero, WindowHelper.ChooseUserWindow([], d));

            // Chạy bước "Thu nhỏ cửa sổ" (trống) như khi hộp thoại đang được chọn — không đổi focus thật của máy.
            WindowHelper.ActiveUserWindowOverride.Value = () => WindowHelper.ChooseUserWindow(WindowHelper.GetOpenWindows(), d);
            FlowResult r;
            FlowContext ctx;
            try
            {
                (r, ctx) = await RunAsync(new Job { Steps = [S(StepType.MinimizeWindow, s => s.Variable = "ungDungTruoc")] });
            }
            finally
            {
                WindowHelper.ActiveUserWindowOverride.Value = null;
            }
            Assert.True(r.Ok, r.Message);
            Assert.Equal(WindowHelper.HandleRef(m), ctx.Vars["ungDungTruoc"]);
            Assert.True(Win32.IsIconic(m));
            Assert.False(Win32.IsWindowVisible(d));                // hộp thoại ẩn theo cửa sổ chủ

            // Mở lại theo handle đã nhớ → cửa sổ chủ và hộp thoại hiện lại.
            (r, _) = await RunAsync(new Job
            {
                Variables = [new VariableDef { Name = "ungDungTruoc", Value = ctx.Vars["ungDungTruoc"] }],
                Steps = [S(StepType.FocusWindow, s => { s.Target = "{{ungDungTruoc}}"; s.DelayMs = 5000; })]
            });
            Assert.True(r.Ok, r.Message);
            Assert.False(Win32.IsIconic(m));
            for (int i = 0; i < 40 && !Win32.IsWindowVisible(d); i++) await Task.Delay(50);
            Assert.True(Win32.IsWindowVisible(d));
        }
        finally
        {
            if (!proc.HasExited) proc.Kill(true);
        }
    }
}
