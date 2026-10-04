using System.Drawing;
using System.Reflection;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;
using ScheduleApp.UI;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>Bật / tắt khung trạng thái góc phải: nút "Ẩn" cho lần chạy này, cài đặt riêng từng công việc, mẫu phát video không hiện khung.</summary>
public class RunOverlayToggleTests
{
    private static void Sta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception(error.ToString());
    }

    [Theory]
    [InlineData(RunOverlayMode.Default, true, true)]
    [InlineData(RunOverlayMode.Default, false, false)]
    [InlineData(RunOverlayMode.Show, false, true)]
    [InlineData(RunOverlayMode.Hide, true, false)]
    public void JobSettingOverridesGlobalSetting(RunOverlayMode mode, bool setting, bool expected)
    {
        var p = new RunProgress(Guid.NewGuid(), "x", "thử", DateTime.Now, -1, 1, "", false) { Overlay = mode };
        Assert.Equal(expected, p.ShowOverlay(setting));
        Assert.Equal(expected, (p with { Step = 0 }).ShowOverlay(setting));   // giữ nguyên qua các lần cập nhật tiến độ
    }

    [Fact]
    public async Task ProgressCarriesTheJobsOverlaySetting()
    {
        var runner = new FlowRunner(new FakeUi(), _ => null);
        var events = new List<RunProgress>();
        runner.Progress += p => { lock (events) events.Add(p); };
        var job = new Job { Name = "Phát video", RunOverlay = RunOverlayMode.Hide, Steps = [S(StepType.LogMessage, s => s.Text = "a")] };
        var r = await runner.EnqueueAsync(job, "thử");
        Assert.True(r!.Ok, r.Message);
        lock (events)
        {
            Assert.True(events.Count >= 3);
            Assert.All(events, e => Assert.False(e.ShowOverlay(true)));
        }
    }

    [Fact]
    public async Task SubJobSetToHideHidesTheOverlayForTheWholeRun()
    {
        // "Họp chiều" (theo cài đặt chung) gọi công việc phát video (Không hiện), gọi lồng qua một công việc trung gian.
        var video = new Job { Name = "Phát video", RunOverlay = RunOverlayMode.Hide, Steps = [S(StepType.LogMessage, s => s.Text = "phát")] };
        var middle = new Job { Name = "Trung gian", Steps = [S(StepType.CallJob, s => s.JobRef = video.Id)] };
        var parent = new Job { Name = "Họp chiều", Steps = [S(StepType.LogMessage, s => s.Text = "a"), S(StepType.CallJob, s => s.JobRef = middle.Id)] };
        var shown = new Job { Name = "Luôn hiện", RunOverlay = RunOverlayMode.Show, Steps = [S(StepType.CallJob, s => s.JobRef = video.Id)] };
        var disabledCall = new Job { Name = "Gọi đã tắt", Steps = [S(StepType.CallJob, s => { s.JobRef = video.Id; s.Enabled = false; })] };
        var all = new[] { video, middle, parent, shown, disabledCall };
        var runner = new FlowRunner(new FakeUi(), id => all.FirstOrDefault(j => j.Id == id));

        Assert.Equal(RunOverlayMode.Hide, runner.EffectiveOverlay(parent));
        Assert.Equal(RunOverlayMode.Show, runner.EffectiveOverlay(shown));          // cài đặt riêng của công việc chính được ưu tiên
        Assert.Equal(RunOverlayMode.Default, runner.EffectiveOverlay(disabledCall));
        Assert.Equal(RunOverlayMode.Hide, runner.EffectiveOverlay(middle));
        Assert.Equal(RunOverlayMode.Default, runner.EffectiveOverlay(new Job { Steps = [S(StepType.LogMessage)] }));

        var events = new List<RunProgress>();
        runner.Progress += p => { lock (events) events.Add(p); };
        var r = await runner.EnqueueAsync(parent, "thử", new RunOptions { IsTest = true });
        Assert.True(r!.Ok, r.Message);
        lock (events)
        {
            Assert.True(events.Count >= 3);
            Assert.All(events, e => Assert.False(e.ShowOverlay(true)));
        }
    }

    [Fact]
    public async Task FailureJobSetToHideHidesTheOverlayWhileItRuns()
    {
        var cleanup = new Job { Name = "Xử lý lỗi bằng video", RunOverlay = RunOverlayMode.Hide, Steps = [S(StepType.LogMessage, s => s.Text = "xử lý")] };
        var job = new Job
        {
            Name = "Có lỗi",
            OnFailureJobId = cleanup.Id,
            Steps = [S(StepType.StopFlow, s => { s.Force = true; s.Text = "lỗi thử"; })]
        };
        var runner = new FlowRunner(new FakeUi(), id => id == cleanup.Id ? cleanup : null);
        Assert.Equal(RunOverlayMode.Default, runner.EffectiveOverlay(job));        // chỉ chạy khi lỗi → không ẩn cả lần chạy
        var events = new List<RunProgress>();
        runner.Progress += p => { lock (events) events.Add(p); };
        var r = await runner.EnqueueAsync(job, "thử", new RunOptions { IsTest = true });
        Assert.False(r!.Ok);
        lock (events)
        {
            Assert.True(events[0].ShowOverlay(true));                              // đang chạy công việc chính → hiện
            int hidden = events.FindIndex(e => !e.ShowOverlay(true));
            Assert.True(hidden > 0, "không ẩn khung khi chạy công việc xử lý lỗi");
            Assert.Null(events[hidden].Ok);
            Assert.False(events[^1].Ok);
            Assert.True(events[^1].ShowOverlay(true));                             // kết quả cuối vẫn hiện
        }
    }

    [Fact]
    public void HideButtonHidesForThisRunOnly()
    {
        Sta(() =>
        {
            using var f = new RunOverlay { Location = new Point(-20000, -20000) };
            var p = new RunProgress(Guid.NewGuid(), "Trình chiếu", "lịch", DateTime.Now, 0, 3, "Thu nhỏ cửa sổ đang dùng", false);
            f.ShowProgress(p);
            f.Location = new Point(-20000, -20000);
            Assert.True(f.Visible);
            var buttons = Buttons(f);
            Assert.Equal(["⏸ Tạm dừng", "■ Dừng", "— Ẩn"], buttons.Select(b => b.Text));
            // Đủ chỗ trong khung (không bị cắt).
            Assert.All(buttons, b => Assert.True(b.Right <= f.ClientSize.Width, $"{b.Text} tràn ra ngoài khung"));
            var hint = f.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Label>().Single();
            Assert.True(!hint.Visible || hint.Right <= f.ClientSize.Width, "gợi ý phím tắt bị cắt");

            buttons.Single(b => b.Text == "— Ẩn").PerformClick();
            Assert.False(f.Visible);
            f.ShowProgress(p with { Step = 1, StepText = "Phát video" });           // bước sau của cùng lần chạy → vẫn ẩn
            f.ShowProgress(p with { Ok = true });                                   // kết thúc → vẫn ẩn
            Assert.False(f.Visible);

            // Kịch bản / dòng dữ liệu / lần chạy lại kế tiếp của cùng bộ kiểm thử (mỗi cái là một lần chạy riêng) → vẫn ẩn.
            f.ShowProgress(p with { Started = DateTime.Now.AddSeconds(2), Step = -1, Ok = null });
            f.ShowProgress(p with { JobId = Guid.NewGuid(), JobName = "Kịch bản 2", Started = DateTime.Now.AddSeconds(3), Step = 0, Ok = null });
            Assert.False(f.Visible);
            Assert.True(f.IsDismissed);

            // Hết flow đang chạy / đang chờ → lần chạy sau hiện lại.
            f.EndDismissal();
            f.ShowProgress(p with { Started = DateTime.Now.AddSeconds(5), Step = -1, Ok = null });
            Assert.True(f.Visible);
            f.Hide();
        });
    }

    [Fact]
    public async Task RunnerIsStillBusyWhenTheNextSuiteCaseIsQueuedRightAfter()
    {
        // MainForm bỏ "Ẩn" khi RunningChanged(false) và runner không còn bận. Bộ kiểm thử xếp kịch bản kế ngay sau kịch bản trước
        // trên cùng luồng giao diện — lúc callback (BeginInvoke) chạy, kịch bản kế đã nằm trong hàng đợi.
        var runner = new FlowRunner(new FakeUi(), _ => null);
        var busyAtIdle = new List<bool>();
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                using var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), ShowInTaskbar = false };
                host.Show();
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                runner.RunningChanged += running =>
                {
                    if (!running) host.BeginInvoke(new System.Windows.Forms.MethodInvoker(() => busyAtIdle.Add(runner.IsBusy)));
                };
                var jobs = Enumerable.Range(1, 3).Select(i => new Job { Name = "Kịch bản " + i, Steps = [S(StepType.LogMessage, s => s.Text = "x")] }).ToList();
                bool done = false;
                host.BeginInvoke(new System.Windows.Forms.MethodInvoker(async () =>
                {
                    try
                    {
                        foreach (var j in jobs) await runner.EnqueueAsync(j, "kiểm thử", new RunOptions { IsTest = true });
                    }
                    catch (Exception ex) { error = ex; }
                    finally { done = true; }
                }));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while ((!done || busyAtIdle.Count < 3) && sw.ElapsedMilliseconds < 30_000)
                {
                    Application.DoEvents();
                    Thread.Sleep(5);
                }
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        await Task.Run(() => t.Join());
        if (error != null) throw new Exception(error.ToString());
        Assert.Equal([true, true, false], busyAtIdle);
    }

    [Fact]
    public void EditorSavesPerJobOverlaySetting()
    {
        Sta(() =>
        {
            var job = new Job { Name = "Trình chiếu rồi phát video" };
            var ui = new FakeUi();
            using var f = new JobEditorForm(job, new FlowRunner(ui, _ => null), [job], ui, isNew: false)
            {
                StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000)
            };
            f.Show();
            Application.DoEvents();
            var combo = (ComboBox)typeof(JobEditorForm).GetField("_cboOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f)!;
            Assert.Equal(3, combo.Items.Count);
            Assert.StartsWith("Theo cài đặt chung", combo.Text);
            Assert.False(f.HasUnsavedChanges);

            combo.SelectedIndex = Array.IndexOf(Enum.GetValues<RunOverlayMode>(), RunOverlayMode.Hide);
            Assert.True(f.HasUnsavedChanges);
            var saved = new Job();
            typeof(JobEditorForm).GetMethod("ApplyTo", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f, [saved]);
            Assert.Equal(RunOverlayMode.Hide, saved.RunOverlay);
            Assert.Equal(RunOverlayMode.Hide, JobStore.ImportJson(System.Text.Json.JsonSerializer.Serialize(new[] { saved }, JsonDefaults.Options)).Single().RunOverlay);
            f.AskSaveChanges = _ => DialogResult.No;
            f.Close();
        });
    }

    [Fact]
    public void VideoTemplateDoesNotShowOverlay()
    {
        var resource = typeof(Job).Assembly.GetManifestResourceNames().Single(n => n.EndsWith("vi-du-mau.json"));
        using var stream = typeof(Job).Assembly.GetManifestResourceStream(resource)!;
        var jobs = JobStore.ImportJson(new StreamReader(stream).ReadToEnd());
        Assert.Equal(RunOverlayMode.Hide, jobs.Single(j => j.Name.StartsWith("15 · 15:30")).RunOverlay);
        Assert.All(jobs.Where(j => !j.Name.StartsWith("15 · ")), j => Assert.Equal(RunOverlayMode.Default, j.RunOverlay));
    }

    private static List<Button> Buttons(Form f) =>
        f.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Where(b => b.Visible).OrderBy(b => b.Left).ToList();
}
