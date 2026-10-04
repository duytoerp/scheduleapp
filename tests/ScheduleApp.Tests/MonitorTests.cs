using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services;
using ScheduleApp.UI;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>Phát video ở màn hình được chọn khi máy có nhiều màn hình.</summary>
public class MonitorTests
{
    // Màn hình chính 1920×1080, màn hình 2 bên phải (thanh tác vụ dưới), màn hình 3 phía trên.
    private static readonly Display One = new(1, new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1040), true);
    private static readonly Display Two = new(2, new Rectangle(1920, 0, 2560, 1440), new Rectangle(1920, 0, 2560, 1400), false);
    private static readonly Display Three = new(3, new Rectangle(0, -900, 1600, 900), new Rectangle(0, -900, 1600, 900), false);
    private static readonly Display[] Three_ = [One, Two, Three];

    [Fact]
    public void PicksTheChosenScreenAndFallsBackToPrimaryWhenItIsUnplugged()
    {
        Assert.Equal((One, (string?)null), Displays.Pick(Displays.Primary, Three_, new Point(3000, 500)));
        Assert.Equal((Two, (string?)null), Displays.Pick(2, Three_, new Point(10, 10)));
        Assert.Equal((Two, (string?)null), Displays.Pick(Displays.UnderMouse, Three_, new Point(3000, 500)));
        Assert.Equal((Three, (string?)null), Displays.Pick(Displays.UnderMouse, Three_, new Point(100, -100)));
        Assert.Equal(One, Displays.Pick(Displays.UnderMouse, Three_, new Point(-99999, 0)).Display);   // chuột ngoài mọi màn hình

        var (display, warning) = Displays.Pick(4, Three_, Point.Empty);                                   // rút máy chiếu
        Assert.Equal(One, display);
        Assert.Equal("không thấy màn hình 4 (máy đang có 3 màn hình) — phát ở màn hình chính", warning);
        Assert.Throws<InvalidOperationException>(() => Displays.Pick(0, [], Point.Empty));
    }

    [Fact]
    public void LabelsAndPlayerRects()
    {
        Assert.Equal("Màn hình 1 — 1920×1080 (chính)", Displays.Label(One, Three_));
        Assert.Equal("Màn hình 2 — 2560×1440, bên phải", Displays.Label(Two, Three_));
        Assert.Equal("Màn hình 3 — 1600×900, phía trên", Displays.Label(Three, Three_));

        Assert.Equal(Two.Bounds, Displays.PlayerRect(Two, fullscreen: true));
        var window = Displays.PlayerRect(Two, fullscreen: false);
        Assert.True(Two.WorkingArea.Contains(window));
        Assert.Equal((1706, 933), (window.Width, window.Height));
        Assert.InRange(window.X + window.Width / 2 - (Two.WorkingArea.X + Two.WorkingArea.Width / 2), -1, 1);

        // Máy thật: luôn có ít nhất màn hình chính, số không trùng.
        var all = Displays.All();
        Assert.NotEmpty(all);
        Assert.Single(all, d => d.Primary);
        Assert.Equal(all.Count, all.Select(d => d.Number).Distinct().Count());
    }

    [Fact]
    public void StepRemembersTheScreen()
    {
        var step = new ActionStep { Type = StepType.PlayMedia, Text = @"D:\a.mp4", Force = true, Monitor = 2 };
        Assert.Equal("Phát \"a.mp4\" · toàn màn hình · màn hình 2", step.Describe());
        Assert.EndsWith("· màn hình đang có chuột", new ActionStep { Type = StepType.PlayMedia, Text = "a.mp4", Monitor = -1 }.Describe());
        Assert.Equal(2, JsonSerializer.Deserialize<ActionStep>(JsonSerializer.Serialize(step, JsonDefaults.Options), JsonDefaults.Options)!.Monitor);
        // Mặc định (màn hình chính) không ghi vào file → công việc cũ không đổi.
        Assert.DoesNotContain("Monitor", JsonSerializer.Serialize(new ActionStep { Type = StepType.PlayMedia }, JsonDefaults.Options));
    }

    [Fact]
    public void EditorOffersScreensAndKeepsAnUnpluggedChoice()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var step = new ActionStep { Type = StepType.PlayMedia, Text = "", Monitor = 7 };
                using var f = new StepEditorForm(step) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
                f.Show();
                var combo = (ComboBox)typeof(StepEditorForm).GetField("_cboMonitor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f)!;
                Assert.True(combo.Visible);
                var items = combo.Items.Cast<string>().ToList();
                Assert.Equal("Màn hình chính", items[0]);
                Assert.StartsWith("Màn hình đang có chuột", items[1]);
                Assert.Equal(Displays.All().Count + 3, items.Count);                       // + màn hình 7 chưa cắm
                Assert.StartsWith("Màn hình 7 (chưa cắm", combo.Text);

                var build = typeof(StepEditorForm).GetMethod("BuildStep", BindingFlags.NonPublic | BindingFlags.Instance)!;
                Assert.Equal(7, ((ActionStep)build.Invoke(f, null)!).Monitor);
                combo.SelectedIndex = 1;
                Assert.Equal(Displays.UnderMouse, ((ActionStep)build.Invoke(f, null)!).Monitor);
                combo.SelectedIndex = 2;
                Assert.Equal(Displays.All()[0].Number, ((ActionStep)build.Invoke(f, null)!).Monitor);
                f.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception(error.ToString());
    }

    [DllImport("user32.dll")] private static extern bool EnumWindows(Win32.EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Trình phát đặt đúng khung pixel được yêu cầu (dùng khung ngoài màn hình, cửa sổ thường, tắt tiếng — không hiện gì lên màn hình thật).</summary>
    [Fact]
    public async Task PlayerOpensExactlyWhereTheScreenRectSays()
    {
        var dir = NewDir();
        var clip = MediaDurationTests.Wav(Path.Combine(dir, "a.wav"), 2.5);
        var target = new Rectangle(-30000, -30000, 640, 360);
        var saved = MediaPlayback.TestBounds;
        MediaPlayback.TestBounds = null;
        try
        {
            var play = MediaPlayback.PlayAsync([clip], fullscreen: false, volume: 0, CancellationToken.None, screen: target);
            Rectangle? seen = null;
            for (int i = 0; i < 100 && seen == null && !play.IsCompleted; i++)
            {
                await Task.Delay(50);
                EnumWindows((h, _) =>
                {
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid == Environment.ProcessId && WindowHelper.GetTitle(h).StartsWith("ScheduleApp — phát", StringComparison.Ordinal) ||
                        pid == Environment.ProcessId && WindowHelper.GetTitle(h).Contains("a.wav", StringComparison.Ordinal))
                    {
                        GetWindowRect(h, out var r);
                        seen = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            Assert.Equal(target, seen);
            var result = await play.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1, result.Played);
        }
        finally
        {
            MediaPlayback.TestBounds = saved;
        }
    }
}
