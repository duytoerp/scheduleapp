using System.Drawing;
using System.Reflection;
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
    public void FindsTheChosenScreenByIdWhenItsNumberChanges()
    {
        Display one = One with { Id = "GSM5BB2#UID4352" }, two = Two with { Id = "GSM5BB2#UID4353" }, three = Three with { Id = "SAM7094#UID4355" };
        Display[] all = [one, two, three];

        // Cắm lại / đổi dock: màn hình lúc chọn là số 2 nay mang số 3 → vẫn phát ở đúng màn hình đó, kèm ghi chú số mới.
        Assert.Equal((three, (string?)null, "màn hình đã chọn nay là màn hình 3 (lúc chọn là màn hình 2)"),
            Displays.Pick(2, "SAM7094#UID4355", all, Point.Empty));
        Assert.Equal((two, (string?)null, (string?)null), Displays.Pick(2, "gsm5bb2#uid4353", all, Point.Empty));    // không phân biệt hoa thường
        Assert.Equal(three, Displays.FindById("sam7094#UID4355", all));
        Assert.Null(Displays.FindById(null, all));
        Assert.Null(Displays.FindById("", all));
        Assert.Null(Displays.FindById("SAM7094#UID4355", Three_));                                       // màn hình không đọc được mã

        // Không thấy mã (rút ra / cắm sang cổng khác) → theo số, kèm cảnh báo; không có cả số đó → màn hình chính.
        var (display, warning, note) = Displays.Pick(2, "DEL40B6#UID9999", all, Point.Empty);
        Assert.Equal(two, display);
        Assert.Equal("không thấy màn hình đã chọn (đã rút hoặc cắm sang cổng khác?) — phát ở màn hình 2 hiện có", warning);
        Assert.Null(note);
        (display, warning, _) = Displays.Pick(4, "DEL40B6#UID9999", all, Point.Empty);
        Assert.Equal(one, display);
        Assert.Equal("không thấy màn hình 4 (máy đang có 3 màn hình) — phát ở màn hình chính", warning);

        // Bước cũ chưa có mã, màn hình chính, màn hình đang có chuột: như trước, không dùng mã.
        Assert.Equal((two, (string?)null, (string?)null), Displays.Pick(2, null, all, Point.Empty));
        Assert.Equal((one, (string?)null, (string?)null), Displays.Pick(Displays.Primary, "SAM7094#UID4355", all, Point.Empty));
        Assert.Equal(two, Displays.Pick(Displays.UnderMouse, "SAM7094#UID4355", all, new Point(3000, 500)).Display);
    }

    [Fact]
    public void MonitorIdComesFromEdidAndPort()
    {
        // Giá trị QueryDisplayConfig trả về cho màn hình LG (EDID hãng "GSM", mẫu 5BB2) cắm cổng 4353 — giống DISPLAY\GSM5BB2\…&UID4353.
        Assert.Equal("GSM5BB2#UID4353", Displays.MonitorKey(0x6D1E, 0x5BB2, edidValid: true, "LG ULTRAGEAR", 4353));
        Assert.Equal("SAM7094#UID4352", Displays.MonitorKey(0x2D4C, 0x7094, edidValid: true, null, 4352));
        Assert.Equal("00001234#UID5", Displays.MonitorKey(0, 0x1234, edidValid: true, null, 5));             // mã hãng hỏng → giữ dạng số
        // Không có EDID (màn hình ảo…) → tên màn hình; không có cả tên → chỉ cổng.
        Assert.Equal("Virtual Display#UID7", Displays.MonitorKey(0, 0, edidValid: false, " Virtual Display ", 7));
        Assert.Equal("UID7", Displays.MonitorKey(0, 0, edidValid: false, "", 7));

        // Máy thật: mã (nếu đọc được) không trùng giữa các màn hình.
        var ids = Displays.All().Select(d => d.Id).OfType<string>().ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void PlayerTakesTheKeyboardOnlyWhereTheUserIsWorking()
    {
        var mainWindow = new Rectangle(-8, -8, 1936, 1056);         // cửa sổ phóng to ở màn hình chính (kể cả viền ẩn)
        var onMain = new Point(500, 500);
        // Phát ở máy chiếu trong lúc người dùng gõ ở màn hình chính → không lấy phím (Space / Esc / → vẫn vào ứng dụng của họ).
        Assert.False(Displays.TakesFocus(Two.Bounds, onMain, mainWindow, screenCount: 2));
        Assert.False(Displays.TakesFocus(Two.Bounds, onMain, null, screenCount: 3));
        Assert.False(Displays.TakesFocus(Two.Bounds, onMain, Rectangle.Empty, screenCount: 2));
        Assert.False(Displays.TakesFocus(Two.Bounds, onMain, new Rectangle(1500, 100, 800, 600), screenCount: 2));   // phần lớn ở màn hình chính
        // Máy một màn hình, chuột hoặc cửa sổ đang dùng ở màn hình phát → lấy phím (Esc / Space / → dùng được ngay).
        Assert.True(Displays.TakesFocus(One.Bounds, onMain, mainWindow, screenCount: 1));
        Assert.True(Displays.TakesFocus(Two.Bounds, onMain, mainWindow, screenCount: 1));
        Assert.True(Displays.TakesFocus(Two.Bounds, new Point(3000, 500), mainWindow, screenCount: 2));
        Assert.True(Displays.TakesFocus(Two.Bounds, onMain, new Rectangle(2000, 100, 800, 600), screenCount: 2));
        Assert.True(Displays.TakesFocus(One.Bounds, onMain, null, screenCount: 2));
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
        var step = new ActionStep { Type = StepType.PlayMedia, Text = @"D:\a.mp4", Force = true, Monitor = 2, MonitorId = "GSM5BB2#UID4353" };
        Assert.Equal("Phát \"a.mp4\" · toàn màn hình · màn hình 2", step.Describe());
        Assert.EndsWith("· màn hình đang có chuột", new ActionStep { Type = StepType.PlayMedia, Text = "a.mp4", Monitor = -1 }.Describe());
        var copy = JsonSerializer.Deserialize<ActionStep>(JsonSerializer.Serialize(step, JsonDefaults.Options), JsonDefaults.Options)!;
        Assert.Equal((2, "GSM5BB2#UID4353"), (copy.Monitor, copy.MonitorId));
        // Mặc định (màn hình chính, chưa có mã) không ghi vào file → công việc cũ không đổi.
        Assert.DoesNotContain("Monitor", JsonSerializer.Serialize(new ActionStep { Type = StepType.PlayMedia }, JsonDefaults.Options));
        // Công việc cũ chỉ có số màn hình vẫn đọc được như trước.
        var old = JsonSerializer.Deserialize<ActionStep>("""{ "Type": "PlayMedia", "Text": "a.mp4", "Monitor": 2 }""", JsonDefaults.Options)!;
        Assert.Equal((2, (string?)null), (old.Monitor, old.MonitorId));
    }

    [Fact]
    public void EditorOffersScreensAndKeepsAnUnpluggedChoice()
    {
        OnSta(() =>
        {
            using var f = Editor(new ActionStep { Type = StepType.PlayMedia, Text = "", Monitor = 99 });
            var combo = MonitorCombo(f);
            Assert.True(combo.Visible);
            var items = combo.Items.Cast<string>().ToList();
            Assert.Equal("Màn hình chính", items[0]);
            Assert.StartsWith("Màn hình đang có chuột", items[1]);
            Assert.Equal(Displays.All().Count + 3, items.Count);                       // + màn hình 99 chưa cắm
            Assert.StartsWith("Màn hình 99 (chưa cắm", combo.Text);

            Assert.Equal((99, (string?)null), Choice(f));
            combo.SelectedIndex = 1;
            Assert.Equal((Displays.UnderMouse, (string?)null), Choice(f));
            combo.SelectedIndex = 2;
            var first = Displays.All()[0];
            Assert.Equal((first.Number, first.Id), Choice(f));                           // chọn màn hình số → lưu cả mã của nó
            f.Close();
        });
    }

    [Fact]
    public void EditorFindsTheChosenScreenByIdAndKeepsAnUnpluggedOne()
    {
        var saved = Displays.TestDisplays;
        Displays.TestDisplays = [One with { Id = "A" }, Two with { Id = "B" }, Three with { Id = "C" }];
        try
        {
            OnSta(() =>
            {
                // Lúc chọn là màn hình 2, nay màn hình đó (mã C) mang số 3 → chọn sẵn đúng màn hình đó, lưu lại số mới.
                using (var f = Editor(new ActionStep { Type = StepType.PlayMedia, Monitor = 2, MonitorId = "C" }))
                {
                    Assert.Equal("Màn hình 3 — 1600×900, phía trên", MonitorCombo(f).Text);
                    Assert.Equal(5, MonitorCombo(f).Items.Count);
                    Assert.Equal((3, "C"), Choice(f));
                    MonitorCombo(f).SelectedIndex = 3;
                    Assert.Equal((2, "B"), Choice(f));
                    MonitorCombo(f).SelectedIndex = 0;
                    Assert.Equal((Displays.Primary, (string?)null), Choice(f));
                }
                // Màn hình đã chọn không cắm nhưng có màn hình khác mang số đó → giữ lựa chọn (cả mã), ghi rõ tạm phát ở đâu.
                using (var f = Editor(new ActionStep { Type = StepType.PlayMedia, Monitor = 2, MonitorId = "Z" }))
                {
                    Assert.Equal("Màn hình đã chọn trước đây (chưa cắm — tạm phát ở màn hình 2)", MonitorCombo(f).Text);
                    Assert.Equal(6, MonitorCombo(f).Items.Count);
                    Assert.Equal((2, "Z"), Choice(f));
                }
                // Bước cũ chưa có mã → theo số, lưu thêm mã của màn hình đó.
                using (var f = Editor(new ActionStep { Type = StepType.PlayMedia, Monitor = 2 }))
                {
                    Assert.Equal("Màn hình 2 — 2560×1440, bên phải", MonitorCombo(f).Text);
                    Assert.Equal((2, "B"), Choice(f));
                }
            });
        }
        finally
        {
            Displays.TestDisplays = saved;
        }
    }

    /// <summary>
    /// Cửa sổ thường: đặt đúng giữa màn hình được chọn (màn hình giả ngoài màn hình thật, tắt tiếng — không hiện gì lên màn hình thật;
    /// máy "hai màn hình" giả nên trình phát cũng không lấy bàn phím của người đang chạy kiểm thử).
    /// </summary>
    [Fact]
    public async Task PlayerOpensExactlyWhereTheScreenRectSays()
    {
        var clip = MediaDurationTests.Wav(Path.Combine(NewDir(), "a.wav"), 2.5);
        var screen = new Display(99, new Rectangle(-30000, -30000, 960, 540), new Rectangle(-30000, -30000, 960, 540), false);
        var (savedBounds, savedDisplays) = (MediaPlayback.TestBounds, Displays.TestDisplays);
        MediaPlayback.TestBounds = null;
        Displays.TestDisplays = [One, screen];
        try
        {
            var play = MediaPlayback.PlayAsync([clip], fullscreen: false, volume: 0, CancellationToken.None, display: screen);
            var expected = Displays.PlayerRect(screen, fullscreen: false);
            Assert.Equal(new Rectangle(-29840, -29910, 640, 360), expected);
            Assert.Equal(expected, (await WaitForPlayer("a.wav", expected, play)).Rect);
            var result = await play.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1, result.Played);
        }
        finally
        {
            (MediaPlayback.TestBounds, Displays.TestDisplays) = (savedBounds, savedDisplays);
        }
    }

    /// <summary>
    /// Toàn màn hình ở màn hình khác (vd máy chiếu): phủ đúng cả màn hình đó (kể cả chỗ thanh tác vụ), không giành bàn phím của màn hình
    /// đang làm việc và ghi nhật ký cách điều khiển (màn hình giả ngoài màn hình thật — không hiện gì lên màn hình thật).
    /// </summary>
    [Fact]
    public async Task FullscreenPlayerCoversTheChosenScreenWithoutTakingTheKeyboard()
    {
        var clip = MediaDurationTests.Wav(Path.Combine(NewDir(), "b.wav"), 2.5);
        var projector = new Display(99, new Rectangle(-30000, -30000, 1280, 720), new Rectangle(-30000, -30000, 1280, 680), false, "TEST#UID99");
        var logs = new List<string>();
        Action<string> onLog = l => { lock (logs) logs.Add(l); };
        var (savedBounds, savedDisplays) = (MediaPlayback.TestBounds, Displays.TestDisplays);
        MediaPlayback.TestBounds = null;
        Displays.TestDisplays = [One, projector];
        Log.Written += onLog;
        try
        {
            var play = MediaPlayback.PlayAsync([clip], fullscreen: true, volume: 0, CancellationToken.None, display: projector);
            var (window, seen) = await WaitForPlayer("b.wav", projector.Bounds, play);
            Assert.Equal(projector.Bounds, seen);
            Assert.NotEqual(window, Win32.GetForegroundWindow());
            var result = await play.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1, result.Played);
            lock (logs) Assert.Contains(logs, l => l.Contains("bấm vào video để dùng Esc / Space / →; Ctrl+Shift+Q vẫn dừng mọi flow"));
        }
        finally
        {
            Log.Written -= onLog;
            (MediaPlayback.TestBounds, Displays.TestDisplays) = (savedBounds, savedDisplays);
        }
    }

    /// <summary>Bước "Phát video / nhạc" đưa đúng màn hình đã chọn (theo mã, rồi theo số) cho trình phát — không mặc định màn hình chính.</summary>
    [Fact]
    public async Task PlayStepSendsTheChosenScreenToThePlayer()
    {
        var clip = MediaDurationTests.Wav(Path.Combine(NewDir(), "c.wav"), 0.3);
        Display one = One with { Id = "A" }, two = Two with { Id = "B" }, three = Three with { Id = "C" };
        var logs = new List<string>();
        Action<string> onLog = l => { lock (logs) logs.Add(l); };
        var (savedBounds, savedDisplays) = (MediaPlayback.TestBounds, Displays.TestDisplays);
        // Máy "ba màn hình" giả; cửa sổ phát ngoài màn hình thật, tắt tiếng.
        MediaPlayback.TestBounds = new System.Windows.Rect(-30000, -30000, 320, 180);
        Displays.TestDisplays = [one, two, three];
        Log.Written += onLog;
        try
        {
            foreach (var (monitor, id, expected, logged) in new (int, string?, Display, string)[]
            {
                (2, null, two, "Phát ở Màn hình 2 — 2560×1440, bên phải."),
                (3, "B", two, "Phát ở Màn hình 2 — 2560×1440, bên phải — màn hình đã chọn nay là màn hình 2 (lúc chọn là màn hình 3)."),
                (7, null, one, "không thấy màn hình 7 (máy đang có 3 màn hình) — phát ở màn hình chính"),
                (Displays.Primary, null, one, "Phát ở Màn hình 1 — 1920×1080 (chính)."),
            })
            {
                MediaPlayback.LastDisplay = null;
                lock (logs) logs.Clear();
                var (r, _) = await RunAsync(new Job { Steps = [S(StepType.PlayMedia, s => { s.Text = clip; s.Monitor = monitor; s.MonitorId = id; })] });
                Assert.Equal(expected, MediaPlayback.LastDisplay);
                lock (logs) Assert.Contains(logs, l => l.Contains(logged));
                Assert.True(r.Ok, r.Message);
            }
        }
        finally
        {
            Log.Written -= onLog;
            (MediaPlayback.TestBounds, Displays.TestDisplays) = (savedBounds, savedDisplays);
        }
    }

    /// <summary>Chạy trên luồng STA riêng (form WinForms), ném lại lỗi nếu có.</summary>
    private static void OnSta(Action action)
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

    private static StepEditorForm Editor(ActionStep step)
    {
        var f = new StepEditorForm(step) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
        f.Show();
        return f;
    }

    private static ComboBox MonitorCombo(StepEditorForm f) =>
        (ComboBox)typeof(StepEditorForm).GetField("_cboMonitor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f)!;

    /// <summary>Màn hình (số, mã) mà form sẽ lưu vào bước.</summary>
    private static (int Monitor, string? Id) Choice(StepEditorForm f)
    {
        var s = (ActionStep)typeof(StepEditorForm).GetMethod("BuildStep", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f, null)!;
        return (s.Monitor, s.MonitorId);
    }

    /// <summary>Cửa sổ phát của tiến trình này (tiêu đề "ScheduleApp — phát…" hoặc có tên file) và khung pixel thật của nó.</summary>
    private static (IntPtr Window, Rectangle Rect) FindPlayer(string file)
    {
        (IntPtr Window, Rectangle Rect) found = (IntPtr.Zero, Rectangle.Empty);
        Win32.EnumWindows((h, _) =>
        {
            Win32.GetWindowThreadProcessId(h, out uint pid);
            var title = WindowHelper.GetTitle(h);
            if (pid == Environment.ProcessId && (title.StartsWith("ScheduleApp — phát", StringComparison.Ordinal) || title.Contains(file, StringComparison.Ordinal))
                && Win32.GetWindowRect(h, out var r))
                found = (h, Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Chờ cửa sổ phát tới đúng khung <paramref name="expected"/> (tối đa 5 giây, hoặc tới khi phát xong).</summary>
    private static async Task<(IntPtr Window, Rectangle Rect)> WaitForPlayer(string file, Rectangle expected, Task playing)
    {
        (IntPtr Window, Rectangle Rect) seen = (IntPtr.Zero, Rectangle.Empty);
        for (int i = 0; i < 100 && seen.Rect != expected && !playing.IsCompleted; i++)
        {
            await Task.Delay(50);
            var now = FindPlayer(file);
            if (now.Window != IntPtr.Zero) seen = now;
        }
        return seen;
    }
}
