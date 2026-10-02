using System.Text;
using ScheduleApp.Models;
using ScheduleApp.Services;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>Thời lượng video / nhạc: đọc từ file, tính tổng danh sách phát, hiện trong trình soạn và khi chạy.</summary>
public class MediaDurationTests
{
    /// <summary>File WAV im lặng có độ dài biết trước (PCM 8 kHz, mono, 16 bit) — không phụ thuộc file có sẵn trên máy.</summary>
    internal static string Wav(string path, double seconds)
    {
        const int rate = 8000;
        int samples = (int)(rate * seconds);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2); w.Write(new byte[samples * 2]);
        return path;
    }

    private static (string Long, string Short) TwoWavs(string? dir = null)
    {
        dir ??= NewDir();
        return (Wav(Path.Combine(dir, "dai.wav"), 3.0), Wav(Path.Combine(dir, "ngan.wav"), 1.2));
    }

    private static void Near(double expectedSeconds, TimeSpan? actual) =>
        Assert.InRange(actual?.TotalSeconds ?? -1, expectedSeconds - 0.1, expectedSeconds + 0.1);

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(11_725, "0:12")]
    [InlineData(65_000, "1:05")]
    [InlineData(3_723_400, "1:02:03")]
    public void FormatsDurations(int ms, string expected) => Assert.Equal(expected, ActionStep.FormatDuration(ms));

    [Fact]
    public async Task ReadsDurationFromFile()
    {
        var (longWav, shortWav) = TwoWavs();
        Near(3.0, await MediaInfo.GetDurationAsync(longWav));
        Near(3.0, await MediaInfo.FromMetadataAsync(longWav));
        // File không có metadata thời lượng → mở thử bằng trình phát.
        Near(1.2, await MediaInfo.FromPlayerAsync(shortWav));

        Assert.Null(await MediaInfo.GetDurationAsync(@"C:\khong-co\video.mp4"));
        var text = Path.Combine(NewDir(), "ghi-chu.txt");
        File.WriteAllText(text, "không phải video");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await MediaInfo.GetDurationAsync(text));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "file không phải video không được chờ trình phát tới hết giờ");

        // File đổi nội dung → đọc lại, không lấy kết quả cũ trong bộ nhớ đệm.
        Wav(longWav, 2.0);
        File.SetLastWriteTimeUtc(longWav, DateTime.UtcNow.AddMinutes(1));
        Near(2.0, await MediaInfo.GetDurationAsync(longWav));
    }

    [Fact]
    public async Task PlanAddsUpTheWholePlayList()
    {
        var folder = Directory.CreateDirectory(Path.Combine(NewDir(), "thu muc")).FullName;
        Wav(Path.Combine(folder, "10.wav"), 3.0);
        Wav(Path.Combine(folder, "2.wav"), 1.2);
        var first = Wav(Path.Combine(NewDir(), "mo dau.wav"), 3.0);
        var expand = MediaInfo.ExpanderFor([new VariableDef { Name = "thuMuc", Value = folder }, new VariableDef { Name = "ngauNhien", Value = "=chuso(3)" }]);

        var plan = await MediaInfo.AnalyzeAsync([$"\"{first}\"", "{{thuMuc}}"], expand);
        Assert.Equal(["mo dau.wav", "2.wav", "10.wav"], plan.Entries.Select(e => Path.GetFileName(e.Path!)));
        Assert.Equal((3, 0), (plan.FileCount, plan.UnknownCount));
        Assert.True(plan.Complete);
        Near(7.2, plan.Total);
        Assert.Equal("0:07", ActionStep.FormatDuration(plan.Total));
        Assert.InRange(MediaInfo.ToMs(plan), 7_100, 7_300);

        var text = UI.StepEditorForm.DescribePlan(plan);
        Assert.StartsWith("Tổng thời lượng: 0:07 · 3 file", text);
        Assert.Contains("1. mo dau.wav — 0:03", text);
        Assert.Contains("2. 2.wav — 0:01", text);

        // File / thư mục chưa có, biến chỉ biết khi chạy (chưa khai báo, ngẫu nhiên, clipboard, ngày giờ) → tổng chưa chắc → không lưu.
        foreach (var line in new[] { @"D:\khong-co\a.mp4", "{{khongCo}}\\a.mp4", "{{ngauNhien}}.mp4", "{{clipboard}}", folder + "\\{{today:yyyy}}\\..\\2.wav" })
        {
            var partial = await MediaInfo.AnalyzeAsync([first, line], expand);
            Assert.False(partial.Complete, line);
            Assert.Equal(0, MediaInfo.ToMs(partial));
        }
        var missing = await MediaInfo.AnalyzeAsync([first, @"D:\khong-co\a.mp4"], expand);
        Assert.StartsWith("Tổng thời lượng: ít nhất 0:03 · 1 file", UI.StepEditorForm.DescribePlan(missing));
        Assert.Contains("✖ D:\\khong-co\\a.mp4 — không thấy file / thư mục", UI.StepEditorForm.DescribePlan(missing));

        // Danh sách chỉ có biến: không phải lỗi, chỉ là chưa tính được.
        var onlyVariable = UI.StepEditorForm.DescribePlan(await MediaInfo.AnalyzeAsync(["{{video}}"], expand));
        Assert.StartsWith("Thời lượng: tính khi chạy", onlyVariable);
        Assert.Contains("• {{video}} — dùng biến — tính khi chạy", onlyVariable);
    }

    [Fact]
    public async Task StepsRememberTheirDuration()
    {
        var (longWav, shortWav) = TwoWavs();
        var one = new ActionStep { Type = StepType.PlayMedia, Text = longWav, Force = true };
        var two = new ActionStep { Type = StepType.PlayMedia, Text = $"{longWav}\n{shortWav}" };
        var unknown = new ActionStep { Type = StepType.PlayMedia, Text = "{{video}}", MediaDurationMs = 1234 };
        var other = new ActionStep { Type = StepType.LogMessage, Text = longWav };
        Assert.True(await MediaInfo.FillDurationsAsync([one, two, unknown, other]));
        Assert.InRange(one.MediaDurationMs, 2_900, 3_100);
        Assert.InRange(two.MediaDurationMs, 4_100, 4_300);
        Assert.Equal(0, unknown.MediaDurationMs);                       // giá trị cũ không còn đúng → bỏ
        Assert.Equal(0, other.MediaDurationMs);
        Assert.False(await MediaInfo.FillDurationsAsync([one, two]));    // không đổi gì

        Assert.Equal("Phát \"dai.wav\" (0:03) · toàn màn hình", one.Describe());
        Assert.StartsWith("Phát lần lượt 2 mục (tổng 0:04): dai.wav → ngan.wav", two.Describe());
        Assert.Equal("Phát \"{{video}}\"", unknown.Describe());
        Assert.Equal("Phát lần lượt 2 mục: Video → D:", new ActionStep { Type = StepType.PlayMedia, Text = "\"D:\\Video\\\"\nD:\\" }.Describe());

        var json = System.Text.Json.JsonSerializer.Serialize(one, JsonDefaults.Options);
        Assert.Contains("\"MediaDurationMs\"", json);
        Assert.Equal(one.MediaDurationMs, System.Text.Json.JsonSerializer.Deserialize<ActionStep>(json, JsonDefaults.Options)!.MediaDurationMs);
        Assert.DoesNotContain("MediaDurationMs", System.Text.Json.JsonSerializer.Serialize(other, JsonDefaults.Options));
        Assert.False(FlowGenerator.Compact(one).ContainsKey(nameof(ActionStep.MediaDurationMs)));
    }

    [Fact]
    public void CommentLinesAreNotExpandedAtRunTime()
    {
        var ctx = new Services.Engine.FlowContext(new Job(), new FakeUi(), Services.Engine.RunOptions.Default, _ => null, CancellationToken.None);
        ctx.Vars["thuMuc"] = @"D:\Video";
        var step = new ActionStep { Type = StepType.PlayMedia, Text = "# cũ: {{khongCo}}\\a.mp4\n{{thuMuc}}\\b.mp4\n\n" };
        Assert.Equal(@"D:\Video\b.mp4", ctx.ExpandStep(step).Text);
    }

    [Fact]
    public async Task AppendKeepsTheFileEncoding()
    {
        var file = Path.Combine(NewDir(), "nhat-ky.txt");
        File.WriteAllText(file, "Dòng đầu", new UnicodeEncoding(false, true));     // UTF-16 như Notepad cũ, chưa có xuống dòng
        var (r, _) = await RunAsync(new Job { Steps = [S(StepType.WriteData, s => { s.DataAction = DataAction.AppendText; s.Target = file; s.Text = "Đã chạy xong"; })] });
        Assert.True(r.Ok, r.Message);
        var bytes = File.ReadAllBytes(file);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);                       // vẫn một BOM UTF-16 ở đầu
        Assert.Equal("Dòng đầu\r\nĐã chạy xong\r\n", File.ReadAllText(file));
    }

    [LiveFact]
    public async Task PlayStepLogsTotalAndSetsVariables()
    {
        var (longWav, shortWav) = TwoWavs();
        var logs = new List<string>();
        Action<string> onLog = l => { lock (logs) logs.Add(l); };
        Log.Written += onLog;
        // Cửa sổ phát ngoài màn hình, tắt tiếng.
        MediaPlayback.TestBounds = new System.Windows.Rect(SystemInformation.VirtualScreen.Right + 400, 0, 320, 180);
        try
        {
            // Cùng một file hai lần liên tiếp vẫn phát đủ hai lần (không bị bỏ qua sau 20 giây).
            var job = new Job { Steps = [S(StepType.PlayMedia, s => s.Text = $"{longWav}\n{shortWav}\n{shortWav}\n# {{{{khongCo}}}}")] };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (r, ctx) = await RunAsync(job);
            Assert.True(r.Ok, r.Message);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"phát mất {sw.Elapsed.TotalSeconds:0} giây");
            Assert.Equal("3", ctx.Vars["media.played"]);
            Assert.Equal("0:05", ctx.Vars["media.duration"]);
            Assert.Equal("5", ctx.Vars["media.seconds"]);
            List<string> lines;
            lock (logs) lines = [.. logs];
            Assert.Contains(lines, l => l.Contains("Phát 3 file — tổng 0:05") && l.Contains("dự kiến xong khoảng"));
            Assert.Contains(lines, l => l.Contains("dai.wav (0:03) → ngan.wav (0:01) → ngan.wav (0:01)"));
            Assert.Contains(lines, l => l.Contains("▶ 1/3 · dai.wav · 0:03"));
            Assert.Contains(lines, l => l.Contains("Đã phát xong 3/3 file (tổng 0:05)"));
            Assert.DoesNotContain(lines, l => l.Contains("không mở được"));
        }
        finally
        {
            MediaPlayback.TestBounds = null;
            Log.Written -= onLog;
        }
    }
}
