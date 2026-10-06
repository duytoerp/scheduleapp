using System.Drawing;
using System.Reflection;
using System.Text.Json.Nodes;
using ScheduleApp.Models;
using ScheduleApp.Recording;
using ScheduleApp.Services;
using ScheduleApp.UI;
using ScheduleApp.Vision;

namespace ScheduleApp.Tests;

/// <summary>Ảnh cả cửa sổ lúc ghi click: lưu file JPEG (thu nhỏ), gắn vào bước, hiện trong form sửa bước, không gửi cho AI, dọn ảnh bỏ đi.</summary>
public class RecordedShotsTests
{
    private static Bitmap Window(int w, int h)
    {
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.FillRectangle(Brushes.SteelBlue, 0, 0, w, 40);
        return bmp;
    }

    [Fact]
    public void SaveShrinksLargeWindowsAndMovesTheClickWithIt()
    {
        using var big = Window(3200, 1800);
        var (name, click) = RecordedShots.Save(big, new Point(1600, 900));
        Assert.True(RecordedShots.IsValidName(name));
        Assert.Equal(new Point(800, 450), click);                     // thu nửa → điểm click cũng nửa
        using (var loaded = RecordedShots.Load(name))
        {
            Assert.NotNull(loaded);
            Assert.Equal(new Size(RecordedShots.MaxSide, 900), loaded!.Size);
        }
        // File không bị giữ khóa sau khi đọc.
        File.Delete(Path.Combine(RecordedShots.Dir, name));

        using var small = Window(800, 500);
        var (smallName, smallClick) = RecordedShots.Save(small, new Point(10, 20));
        Assert.Equal(new Point(10, 20), smallClick);                  // nhỏ hơn giới hạn → giữ nguyên cỡ
        using var again = RecordedShots.Load(smallName);
        Assert.Equal(new Size(800, 500), again!.Size);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"..\..\secrets.jpg")]
    [InlineData(@"C:\Windows\x.jpg")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF.jpg")]
    [InlineData("0123456789abcdef0123456789abcdef.png")]
    public void ForeignNamesFromImportedJobsAreNeverOpened(string? name)
    {
        Assert.False(RecordedShots.IsValidName(name));
        Assert.Null(RecordedShots.Load(name));
    }

    [Fact]
    public void MissingFileJustShowsNothing() =>
        Assert.Null(RecordedShots.Load("00000000000000000000000000000000.jpg"));

    [Fact]
    public void CleanupKeepsShotsStillUsedByJobsOrOldVersions()
    {
        var data = Path.Combine(Path.GetTempPath(), "sa-shots-" + Guid.NewGuid().ToString("N"));
        var shots = Path.Combine(data, "recorded-shots");
        Directory.CreateDirectory(Path.Combine(data, "versions", "job1"));
        Directory.CreateDirectory(shots);
        try
        {
            string N(char c) => new string(c, 32) + ".jpg";
            var now = DateTime.UtcNow;
            foreach (var c in "abcde") File.WriteAllBytes(Path.Combine(shots, N(c)), [1]);
            foreach (var c in "abcd") File.SetLastWriteTimeUtc(Path.Combine(shots, N(c)), now.AddDays(-30));
            File.WriteAllText(Path.Combine(shots, "notes.txt"), "x");   // file lạ: không đụng tới
            File.WriteAllText(Path.Combine(data, "jobs.json"), $$"""[{"Steps":[{"ContextShot":"{{N('a')}}"}]}]""");
            File.WriteAllText(Path.Combine(data, "versions", "job1", "20261001.json"), $$"""{"ContextShot":"{{N('b')}}"}""");

            Assert.Equal(2, RecordedShots.Cleanup(shots, data, now));
            Assert.True(File.Exists(Path.Combine(shots, N('a'))));     // công việc đang dùng
            Assert.True(File.Exists(Path.Combine(shots, N('b'))));     // phiên bản cũ còn dùng
            Assert.False(File.Exists(Path.Combine(shots, N('c'))));
            Assert.False(File.Exists(Path.Combine(shots, N('d'))));
            Assert.True(File.Exists(Path.Combine(shots, N('e'))));     // mới ghi (chưa kịp lưu công việc) → chưa xóa
            Assert.True(File.Exists(Path.Combine(shots, "notes.txt")));
        }
        finally { Directory.Delete(data, true); }
    }

    [Fact]
    public void RecorderAttachesTheWindowShotToClicksAndDrags()
    {
        var recorder = new MacroRecorder();
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var pressed = typeof(MacroRecorder).GetField("_pressed", flags)!;
        var up = typeof(MacroRecorder).GetMethod("OnButtonUp", flags)!;

        void Gesture(Point down, Point release, Size window)
        {
            var view = new MacroRecorder.WindowShot(Window(window.Width, window.Height), down);
            pressed.SetValue(recorder, new MacroRecorder.Press(down, MouseButtonKind.Left, IntPtr.Zero, IntPtr.Zero, "", Environment.TickCount64,
                null, null, view));
            up.Invoke(recorder, [release]);
        }

        Gesture(new Point(300, 200), new Point(300, 200), new Size(1000, 700));
        Gesture(new Point(100, 100), new Point(400, 100), new Size(3200, 1600));
        var steps = recorder.Stop();

        Assert.Equal([StepType.MouseClick, StepType.MouseDrag], steps.Select(s => s.Type));
        Assert.Equal(new Point(300, 200), new Point(steps[0].ContextClickX, steps[0].ContextClickY));
        Assert.Equal(new Point(50, 50), new Point(steps[1].ContextClickX, steps[1].ContextClickY));   // ảnh 3200 px thu còn 1600
        foreach (var s in steps)
        {
            Assert.True(RecordedShots.IsValidName(s.ContextShot));
            Assert.True(File.Exists(Path.Combine(RecordedShots.Dir, s.ContextShot!)));
        }
    }

    [Fact]
    public void ShotNameNeverGoesToAiButSurvivesItsEdits()
    {
        using var img = Window(400, 300);
        var (name, _) = RecordedShots.Save(img, new Point(5, 5));
        List<ActionStep> current = [new() { Type = StepType.MouseClick, X = 5, Y = 5, ContextShot = name, ContextClickX = 5, ContextClickY = 6 }];
        var compact = FlowGenerator.Compact(current[0], 0);
        Assert.DoesNotContain(name, compact.ToJsonString());
        Assert.Null(compact[nameof(ActionStep.ContextClickX)]);

        var gen = new FlowGenerator(new FlowGenerator.Context { JobName = "x", Steps = current });
        compact["X"] = 9;
        var r = gen.Parse(new JsonObject { ["name"] = "x", ["summary"] = "", ["steps"] = new JsonArray(compact.DeepClone()) }, FlowGenerator.Mode.Replace);
        Assert.Equal(9, r.Steps[0].X);
        Assert.Equal(name, r.Steps[0].ContextShot);
        Assert.Equal(6, r.Steps[0].ContextClickY);
    }

    [Fact]
    public void StepEditorShowsTheShotAndCanDropIt()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                using var img = Window(1000, 600);
                var (name, click) = RecordedShots.Save(img, new Point(700, 300));
                var step = new ActionStep { Type = StepType.MouseClick, X = 700, Y = 300, ContextShot = name, ContextClickX = click.X, ContextClickY = click.Y };
                var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                ActionStep Build(StepEditorForm f) => (ActionStep)typeof(StepEditorForm).GetMethod("BuildStep", flags)!.Invoke(f, null)!;
                T Field<T>(StepEditorForm f, string n) => (T)typeof(StepEditorForm).GetField(n, flags)!.GetValue(f)!;

                using (var f = new StepEditorForm(step) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) })
                {
                    f.Show();
                    Application.DoEvents();
                    var pic = Field<PictureBox>(f, "_picContext");
                    Assert.True(pic.Visible);
                    Assert.Equal(new Size(1000, 600), pic.Image!.Size);
                    var marker = ContextShotViewer.ToView(pic, click)!.Value;
                    Assert.InRange(marker.X, pic.ClientSize.Width * 0.6f, pic.ClientSize.Width * 0.8f);   // 700 / 1000 bề ngang
                    Assert.Equal(name, Build(f).ContextShot);

                    Field<Button>(f, "_btnContextClear").PerformClick();
                    Assert.False(pic.Visible);
                    Assert.Null(Build(f).ContextShot);
                }

                // Bước không có ảnh (hoặc file đã bị xóa) → không có dòng "Ảnh lúc ghi".
                using var plain = new StepEditorForm(new ActionStep { Type = StepType.MouseClick, ContextShot = "11111111111111111111111111111111.jpg" })
                    { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
                plain.Show();
                Application.DoEvents();
                Assert.False(Field<PictureBox>(plain, "_picContext").Visible);
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception(error.ToString());
    }
}
