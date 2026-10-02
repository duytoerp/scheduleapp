using System.Drawing;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.UI;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>Khung trạng thái flow đang chạy (góc phải màn hình): tiến độ từng bước, nút Tạm dừng, né chỗ sắp click.</summary>
public class RunOverlayTests
{
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "hết giờ chờ");
    }

    [Fact]
    public async Task ReportsProgressAndPausesBeforeNextStep()
    {
        var ui = new FakeUi();
        var sub = new Job { Name = "Công việc con", Steps = [S(StepType.Wait, s => s.DelayMs = 600), S(StepType.LogMessage, s => s.Text = "con xong")] };
        var job = new Job
        {
            Name = "Thử tạm dừng",
            Steps =
            [
                S(StepType.Wait, s => s.DelayMs = 600),
                S(StepType.LogMessage, s => s.Text = "bước 2"),
                S(StepType.CallJob, s => { s.JobRef = sub.Id; s.Target = sub.Name; }),
                S(StepType.LogMessage, s => s.Text = "bước 4")
            ]
        };
        var runner = new FlowRunner(ui, id => id == sub.Id ? sub : null);
        var events = new List<RunProgress>();
        runner.Progress += p => { lock (events) events.Add(p); };
        int Last() { lock (events) return events.Count > 0 ? events[^1].Step : -2; }

        Assert.False(runner.RequestPause());                    // chưa có flow nào chạy
        var run = runner.EnqueueAsync(job, "thử");
        await WaitUntil(() => Last() == 0);
        Assert.True(runner.RequestPause());                     // bấm ⏸ khi bước 1 đang chạy
        await WaitUntil(() => Last() == 2);
        await Task.Delay(100);
        Assert.True(runner.RequestPause());                     // bấm ⏸ khi đang trong công việc con
        var r = await run;
        Assert.True(r!.Ok, r.Message);

        // Dừng đúng trước bước kế tiếp, mỗi lần bấm một lần — kể cả bước trong công việc con.
        Assert.Equal([("Thử tạm dừng", 1, "Tạm dừng"), ("Công việc con", 1, "Tạm dừng")], ui.Pauses);
        Assert.False(runner.RequestPause());

        List<RunProgress> all;
        lock (events) all = [.. events];
        Assert.Equal([-1, 0, 1, 2, 3], all.SkipLast(1).Select(e => e.Step));
        Assert.All(all, e => Assert.Equal((job.Id, 4, "Thử tạm dừng"), (e.JobId, e.Total, e.JobName)));
        Assert.Equal(job.Steps[0].Describe(), all[1].StepText);
        Assert.Null(all[^2].Ok);
        Assert.True(all[^1].Ok);
        Assert.All(all, e => Assert.Equal(all[0].Started, e.Started));
    }

    [Fact]
    public async Task ReportsFailureAndStop()
    {
        var runner = new FlowRunner(new FakeUi(), _ => null);
        var events = new List<RunProgress>();
        runner.Progress += p => { lock (events) events.Add(p); };

        var bad = new Job { Name = "Lỗi", Steps = [S(StepType.LogMessage, s => s.Text = "a"), S(StepType.StopFlow, s => { s.Force = true; s.Text = "hỏng ở đây"; })] };
        var r = await runner.EnqueueAsync(bad, "thử");
        Assert.False(r!.Ok);
        RunProgress end;
        lock (events) end = events[^1];
        Assert.Equal((false, "hỏng ở đây", 2), (end.Ok, end.Message, end.FailedStep));

        var slow = new Job { Name = "Chậm", Steps = [S(StepType.Wait, s => s.DelayMs = 5000)] };
        var run = runner.EnqueueAsync(slow, "thử");
        await WaitUntil(() => { lock (events) return events[^1].JobId == slow.Id && events[^1].Step == 0; });
        runner.StopAll();
        r = await run;
        lock (events) end = events[^1];
        Assert.Equal((false, "Đã dừng"), (end.Ok, end.Message));
    }

    [Fact]
    public void MovesAwayFromThePointAboutToBeClicked()
    {
        var area = new Rectangle(0, 0, 1920, 1040);
        var size = new Size(390, 170);
        var corner = new Point(1920 - 390 - 12, 1040 - 170 - 12);

        // Không che → giữ nguyên chỗ.
        Assert.Equal(corner, RunOverlay.Place(area, size, corner, new Point(500, 500), 12));
        // Sắp click vào chỗ khung đang che (kể cả sát mép) → sang góc trái dưới.
        Assert.Equal(new Point(12, corner.Y), RunOverlay.Place(area, size, corner, new Point(1700, 950), 12));
        Assert.Equal(new Point(12, corner.Y), RunOverlay.Place(area, size, corner, new Point(corner.X - 5, corner.Y + 10), 12));
        // Người dùng đã kéo khung ra giữa màn hình → né về góc phải dưới.
        var dragged = new Point(800, 400);
        Assert.Equal(corner, RunOverlay.Place(area, size, dragged, new Point(900, 450), 12));
    }

    [Fact]
    public void ShowsStepPauseAndResult()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                using var f = new RunOverlay { Location = new Point(-20000, -20000) };
                f.Show();                                       // ngoài màn hình — để vẽ được cả các nút
                // Không lọt vào ảnh chụp màn hình (tìm theo hình ảnh, ảnh lỗi).
                Assert.True(Native.Win32.GetWindowDisplayAffinity(f.Handle, out var affinity));
                Assert.Equal(0x11u, affinity);
                var start = DateTime.Now.AddSeconds(-75);
                var p = new RunProgress(Guid.NewGuid(), "Chạy Clip 1 → Clip 2", "lịch", start, -1, 5, "", false);
                Assert.True(f.Apply(p));
                Assert.Equal("● ĐANG CHẠY", f.HeaderText);
                Assert.Equal(("Đang chuẩn bị…", ""), f.BodyText);
                Assert.Equal(f.LogicalToDeviceUnits(390), f.Width);

                Assert.False(f.Apply(p with { Step = 1, StepText = "Phát \"Clip_2.mp4\" (0:16)", StepStarted = DateTime.Now.AddSeconds(-5) }));
                Assert.Equal(("Bước 2/5 · 0:05", "Phát \"Clip_2.mp4\" (0:16)"), f.BodyText);
                Assert.Equal(StripeOf(f), Color.FromArgb(76, 194, 255));

                // Nhật ký: bỏ dòng trùng mô tả bước, giữ dòng chi tiết mới nhất.
                f.AddLog("10:00:01 [INFO]    ▶ 2/2 · Clip_2.mp4 · 0:16");
                f.AddLog("10:00:01 [INFO]    [3/5] Ghi vào \"a.txt\"");
                Assert.Equal("▶ 2/2 · Clip_2.mp4 · 0:16", f.Detail);

                f.MarkPauseRequested();
                Assert.StartsWith("⏸ SẼ TẠM DỪNG", f.HeaderText);
                f.SetPaused("Tạm dừng", "3. Ghi vào \"da-chay-xong.txt\"");
                Assert.Equal(("Sắp chạy bước 2/5", "Ghi vào \"da-chay-xong.txt\""), f.BodyText);
                Assert.Equal(StripeOf(f), Color.FromArgb(255, 185, 0));
                Assert.Equal(["⏭ Bước tiếp", "▶ Chạy tiếp", "■ Dừng"], VisibleButtons(f));
                f.SetPaused(null, null);
                Assert.Equal(["⏸ Tạm dừng", "■ Dừng"], VisibleButtons(f));
                f.Apply(p with { Step = 2, StepText = "Mở notepad", StepStarted = DateTime.Now });
                Assert.Equal(("Bước 3/5 · 0:00", ""), (f.BodyText.StepLine, f.Detail));   // sang bước mới → bỏ dòng chi tiết của bước trước
                Assert.Equal("● ĐANG CHẠY", f.HeaderText);

                f.Apply(p with { Ok = false, Message = "Không thấy nút \"Lưu\"", FailedStep = 3 });
                Assert.Equal("✖ KHÔNG HOÀN THÀNH", f.HeaderText);
                Assert.StartsWith("Lỗi ở bước 3/5 · 1:1", f.BodyText.StepLine);
                f.Apply(p with { Ok = false, Message = "Đã dừng" });
                Assert.Equal("■ ĐÃ DỪNG", f.HeaderText);
                f.Apply(p with { Ok = true });
                Assert.Equal(("✔ HOÀN THÀNH", Color.FromArgb(108, 203, 95)), (f.HeaderText, StripeOf(f)));

                // Lần chạy mới → xóa trạng thái cũ.
                Assert.True(f.Apply(p with { JobId = Guid.NewGuid(), IsTest = true }));
                Assert.Equal(("● ĐANG CHẠY THỬ", ""), (f.HeaderText, f.Detail));
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
    }

    [Theory]
    [InlineData(5, "0:05")]
    [InlineData(75, "1:15")]
    [InlineData(3725, "1:02:05")]
    public void FormatsElapsed(int seconds, string expected) => Assert.Equal(expected, RunOverlay.Elapsed(TimeSpan.FromSeconds(seconds)));

    /// <summary>Các nút đang hiện, theo thứ tự từ trái sang phải.</summary>
    private static string[] VisibleButtons(Form f) =>
        f.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Where(b => b.Visible).OrderBy(b => b.Left).Select(b => b.Text).ToArray();

    /// <summary>Màu dải bên trái (theo trạng thái) — vẽ ngoài màn hình, không chụp màn hình thật.</summary>
    private static Color StripeOf(Form f)
    {
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
        var c = bmp.GetPixel(1, f.Height / 2);
        return Color.FromArgb(c.R, c.G, c.B);
    }
}
