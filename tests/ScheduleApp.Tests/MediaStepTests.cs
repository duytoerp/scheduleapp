using ScheduleApp.Models;
using ScheduleApp.Services;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>Bước "Phát video / nhạc" và ghi file văn bản (vd báo "đã chạy xong" rồi mở bằng Notepad).</summary>
public class MediaStepTests
{
    [Fact]
    public void PlayListIsReadInOrder()
    {
        var dir = NewDir();
        var folder = Directory.CreateDirectory(Path.Combine(dir, "thu muc")).FullName;
        foreach (var f in new[] { "10.mp4", "2.mp4", "1.wmv", "ghi-chu.txt" }) File.WriteAllText(Path.Combine(folder, f), "");
        var clip = Path.Combine(dir, "Clip 1.mp4");
        File.WriteAllText(clip, "");

        var step = new ActionStep
        {
            Type = StepType.PlayMedia, Force = true,
            Text = $"# danh sách phát\n\"{clip}\"\n\n{folder}\nD:\\khong-co\\video.mp4\n"
        };
        Assert.Equal(3, step.MediaLines.Count);
        Assert.Equal("Phát lần lượt 3 mục: Clip 1.mp4 → thu muc → video.mp4 · toàn màn hình", step.Describe());
        Assert.True(step.UsesScreen);
        Assert.True(ActionStep.CreateDefault(StepType.PlayMedia).Force);

        var (files, missing) = MediaPlayback.Resolve(step.MediaLines);
        // Thư mục: chỉ file video / nhạc, sắp theo tên như Explorer (2 trước 10).
        Assert.Equal(["Clip 1.mp4", "1.wmv", "2.mp4", "10.mp4"], files.Select(Path.GetFileName));
        Assert.Equal([@"D:\khong-co\video.mp4"], missing);

        Environment.SetEnvironmentVariable("SA_VIDEO_DIR", dir);
        Assert.Equal(clip, MediaPlayback.Resolve([@"'%SA_VIDEO_DIR%\Clip 1.mp4'"]).Files.Single());
    }

    [Fact]
    public async Task WritesTextFileForNotepad()
    {
        var file = Path.Combine(NewDir(), "bao cao", "da-chay-xong.txt");
        var job = new Job
        {
            Variables = [new VariableDef { Name = "ten", Value = "Clip 1" }],
            Steps =
            [
                S(StepType.WriteData, s => { s.DataAction = DataAction.WriteText; s.Target = file; s.Text = "ĐÃ CHẠY XONG\n{{ten}} — {{today}}"; }),
                S(StepType.WriteData, s => { s.DataAction = DataAction.AppendText; s.Target = file; s.Text = "Dòng thêm"; }),
            ]
        };
        Assert.True(job.Steps[0].IsTextWrite);
        Assert.StartsWith("Ghi vào \"", job.Steps[0].Describe());
        var (r, _) = await RunAsync(job);
        Assert.True(r.Ok, r.Message);
        Assert.Equal($"ĐÃ CHẠY XONG\r\nClip 1 — {DateTime.Today:dd/MM/yyyy}\r\nDòng thêm\r\n", File.ReadAllText(file));

        // Ghi lại → thay nội dung cũ; thêm vào file không kết thúc bằng xuống dòng → sang dòng mới.
        File.WriteAllText(file, "cũ");
        (r, _) = await RunAsync(new Job { Steps = [job.Steps[1]] });
        Assert.True(r.Ok, r.Message);
        Assert.Equal("cũ\r\nDòng thêm\r\n", File.ReadAllText(file));
        (r, _) = await RunAsync(new Job { Variables = job.Variables, Steps = [job.Steps[0]] });
        Assert.Equal($"ĐÃ CHẠY XONG\r\nClip 1 — {DateTime.Today:dd/MM/yyyy}\r\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task PlayStepFailsClearlyWithoutFiles()
    {
        var (r, _) = await RunAsync(new Job { Steps = [S(StepType.PlayMedia, s => s.Text = "# chỉ có ghi chú")] });
        Assert.False(r.Ok);
        Assert.Contains("Chưa có file video", r.Message);

        (r, _) = await RunAsync(new Job { Steps = [S(StepType.PlayMedia, s => s.Text = @"D:\khong-co\a.mp4")] });
        Assert.False(r.Ok);
        Assert.Contains(@"D:\khong-co\a.mp4", r.Message);
    }

    [LiveFact]
    public async Task PlaysVideosOneAfterAnother()
    {
        // File âm thanh tự tạo, độ dài biết trước (2 giây + 1,5 giây).
        var dir = NewDir();
        string[] videos = [MediaDurationTests.Wav(Path.Combine(dir, "a.wav"), 2.0), MediaDurationTests.Wav(Path.Combine(dir, "b.wav"), 1.5)];
        var logs = new List<string>();
        Action<string> onLog = l => { lock (logs) logs.Add(l); };
        Log.Written += onLog;
        try
        {
            // Cửa sổ ngoài màn hình, tắt tiếng — không làm phiền người đang dùng máy.
            var offscreen = new System.Windows.Rect(SystemInformation.VirtualScreen.Right + 400, 0, 480, 270);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = await MediaPlayback.PlayAsync([videos[0], @"C:\khong-co\x.mp4", videos[1]], fullscreen: false, volume: 0, CancellationToken.None, offscreen)
                .WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(2, r.Played);
            Assert.False(r.StoppedByUser);
            Assert.Single(r.Problems);                   // file không có bị bỏ qua
            Assert.True(sw.Elapsed > TimeSpan.FromSeconds(3.5 * 0.9), $"phải phát hết cả hai file, chỉ mất {sw.Elapsed.TotalSeconds:0.0} giây");
            Assert.Equal([0, 2], r.PlayedIndexes!);
            List<string> started;
            lock (logs) started = logs.Where(l => l.Contains("▶ ")).ToList();
            Assert.Equal(3, started.Count);
            Assert.Contains("1/3 · " + Path.GetFileName(videos[0]), started[0]);
            Assert.Contains("3/3 · " + Path.GetFileName(videos[1]), started[2]);

            // Flow bị dừng giữa chừng → đóng trình phát ngay.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var cancel = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                MediaPlayback.PlayAsync(videos, false, 0, cts.Token, offscreen).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.True(cancel.Elapsed < TimeSpan.FromSeconds(3), $"dừng sau {cancel.Elapsed.TotalSeconds:0.0} giây");
        }
        finally
        {
            Log.Written -= onLog;
        }
    }
}
