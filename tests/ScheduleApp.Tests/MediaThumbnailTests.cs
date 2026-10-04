using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.UI;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Ảnh thu nhỏ video như Explorer: dải ảnh trong trình soạn bước "Phát video / nhạc" (thứ tự, thời lượng, kéo đổi thứ tự, bỏ, thêm),
/// nút chọn video khi thiết lập mẫu và ảnh video trên nút ở sơ đồ. Dùng video mp4 thật do Windows tạo từ một khung hình màu.
/// </summary>
public class MediaThumbnailTests
{
    private static readonly Color Red = Color.FromArgb(220, 30, 30), Blue = Color.FromArgb(30, 60, 220), Green = Color.FromArgb(30, 170, 60);

    /// <summary>Video mp4 thật (H.264, do Windows Media Foundation mã hóa) có một khung hình màu nền + ô vuông trắng ở giữa.</summary>
    private static async Task<string> MakeVideoAsync(string dir, string name, Color color, double seconds = 2)
    {
        var png = Path.Combine(dir, name + ".png");
        using (var b = new Bitmap(320, 180))
        using (var g = Graphics.FromImage(b))
        {
            g.Clear(color);
            g.FillRectangle(Brushes.White, 130, 60, 60, 60);
            b.Save(png, ImageFormat.Png);
        }
        var image = await Windows.Storage.StorageFile.GetFileFromPathAsync(png);
        var composition = new Windows.Media.Editing.MediaComposition();
        composition.Clips.Add(await Windows.Media.Editing.MediaClip.CreateFromImageFileAsync(image, TimeSpan.FromSeconds(seconds)));
        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(dir);
        var file = await folder.CreateFileAsync(name + ".mp4", Windows.Storage.CreationCollisionOption.ReplaceExisting);
        await composition.RenderToFileAsync(file, Windows.Media.Editing.MediaTrimmingPreference.Precise);
        File.Delete(png);
        return file.Path;
    }

    private static bool Near(Color c, Color expected, int tolerance = 60) =>
        Math.Abs(c.R - expected.R) <= tolerance && Math.Abs(c.G - expected.G) <= tolerance && Math.Abs(c.B - expected.B) <= tolerance;

    /// <summary>
    /// Chạy trên luồng giao diện riêng như trong ứng dụng: có WindowsFormsSynchronizationContext (phần sau await quay về luồng giao diện)
    /// và báo lỗi nếu có chỗ chạm vào control từ luồng khác.
    /// </summary>
    private static void Sta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            bool strict = Control.CheckForIllegalCrossThreadCalls;
            Control.CheckForIllegalCrossThreadCalls = true;
            try
            {
                UiContext();
                action();
            }
            catch (Exception ex) { error = ex; }
            finally { Control.CheckForIllegalCrossThreadCalls = strict; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception(error.ToString());
    }

    /// <summary>
    /// Application.DoEvents() không nằm trong Application.Run nên khi xong lại gỡ SynchronizationContext của WinForms —
    /// đặt lại để thao tác kế tiếp (gửi chuột / phím, sửa ô) chạy như trong ứng dụng thật.
    /// </summary>
    private static void UiContext()
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
    }

    /// <summary>Chờ trên luồng giao diện (vẫn xử lý thông điệp để các tác vụ nền quay về được).</summary>
    private static void PumpUntil(Func<bool> condition, int timeoutMs = 20_000)
    {
        var until = DateTime.Now.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.Now < until)
        {
            Application.DoEvents();
            UiContext();
            Thread.Sleep(15);
        }
        Assert.True(condition(), "hết giờ chờ");
    }

    private static T Field<T>(object o, string name) =>
        (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_KEYDOWN = 0x0100, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, MK_LBUTTON = 1;
    private static IntPtr XY(Point p) => (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));

    /// <summary>Kéo chuột trái thật (thông điệp chuột tới cửa sổ của control) từ <paramref name="from"/> tới <paramref name="to"/>.</summary>
    private static void Drag(Control c, Point from, Point to)
    {
        SendMessage(c.Handle, WM_LBUTTONDOWN, MK_LBUTTON, XY(from));
        for (int i = 1; i <= 6; i++)
            SendMessage(c.Handle, WM_MOUSEMOVE, MK_LBUTTON, XY(new Point(from.X + (to.X - from.X) * i / 6, from.Y + (to.Y - from.Y) * i / 6)));
        SendMessage(c.Handle, WM_LBUTTONUP, IntPtr.Zero, XY(to));
    }

    private static Point Center(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    /// <summary>Nhấn chuột trái trên ô <paramref name="tile"/> rồi kéo tới <paramref name="to"/> (chưa thả).</summary>
    private static void Press(MediaStrip strip, int tile, Point to)
    {
        var from = Center(strip.BoxRect(tile));
        SendMessage(strip.Handle, WM_LBUTTONDOWN, MK_LBUTTON, XY(from));
        for (int i = 1; i <= 6; i++)
            SendMessage(strip.Handle, WM_MOUSEMOVE, MK_LBUTTON, XY(new Point(from.X + (to.X - from.X) * i / 6, from.Y + (to.Y - from.Y) * i / 6)));
    }

    private static void Release(Control c, Point at) => SendMessage(c.Handle, WM_LBUTTONUP, IntPtr.Zero, XY(at));

    /// <summary>Nhấp (không kéo) để chọn ô.</summary>
    private static void Click(MediaStrip strip, int tile)
    {
        SendMessage(strip.Handle, WM_LBUTTONDOWN, MK_LBUTTON, XY(Center(strip.BoxRect(tile))));
        Release(strip, Center(strip.BoxRect(tile)));
    }

    /// <summary>Phím có Ctrl / Shift (gửi thông điệp phím không kèm được trạng thái phím Ctrl thật).</summary>
    private static void Key(Control c, Keys keys) =>
        typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(c, [new KeyEventArgs(keys)]);

    private static bool InputKey(Control c, Keys key) =>
        (bool)c.GetType().GetMethod("IsInputKey", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(c, [key])!;

    private static void PumpFor(int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until)
        {
            Application.DoEvents();
            UiContext();
            Thread.Sleep(15);
        }
    }

    /// <summary>Danh sách phát dựng sẵn (file không cần có thật): mỗi phần tử là một dòng; dòng nhiều file = thư mục "thu muc{dòng}".</summary>
    private static MediaInfo.Plan PlanOf(string dir, IEnumerable<string[]> lines) =>
        new(lines.SelectMany((files, item) =>
        {
            var line = files.Length > 1 ? Path.Combine(dir, "thu muc" + item) : Path.Combine(dir, files[0]);
            return files.Select(f => new MediaInfo.Entry(line, files.Length > 1 ? Path.Combine(line, f) : line, null, null) { Item = item });
        }).ToList(), TimeSpan.Zero, false);

    [Fact]
    public async Task ThumbnailIsTheVideoFrameLikeExplorer()
    {
        var dir = NewDir();
        var video = await MakeVideoAsync(dir, "do", Red);
        using (var thumb = await MediaThumbnails.GetAsync(video, 256))
        {
            Assert.NotNull(thumb);
            Assert.False(thumb.IsIcon);
            Assert.Equal(256, Math.Max(thumb.Image.Width, thumb.Image.Height));
            Assert.InRange(thumb.Image.Width / (double)thumb.Image.Height, 1.7, 1.85);              // giữ tỉ lệ 16:9 của video
            Assert.True(Near(thumb.Image.GetPixel(8, 8), Red), "góc phải là màu nền của khung hình");
            Assert.True(Near(thumb.Image.GetPixel(thumb.Image.Width / 2, thumb.Image.Height / 2), Color.White), "giữa là ô vuông trắng");
        }

        // File nhạc không có ảnh bìa → biểu tượng của loại file (như Explorer); không có file → null.
        var wav = MediaDurationTests.Wav(Path.Combine(dir, "nhac.wav"), 1);
        using (var icon = await MediaThumbnails.GetAsync(wav, 256))
        {
            Assert.NotNull(icon);
            Assert.True(icon.IsIcon);
        }
        Assert.Null(await MediaThumbnails.GetAsync(Path.Combine(dir, "khong-co.mp4"), 256));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaThumbnails.GetAsync(video, 256, cts.Token));
    }

    [Fact]
    public void PlaylistTextMovesAndRemovesWholeLinesKeepingComments()
    {
        const string text = "# nhạc mở đầu\r\nA.mp4\r\n\r\nB.mp4\nC\\thu muc\r\n# cuối\r\nD.mp4";
        Assert.Equal("# nhạc mở đầu\r\nB.mp4\r\n\r\nC\\thu muc\r\nA.mp4\r\n# cuối\r\nD.mp4", PlaylistText.Move(text, 0, 2));
        Assert.Equal("# nhạc mở đầu\r\nD.mp4\r\n\r\nA.mp4\r\nB.mp4\r\n# cuối\r\nC\\thu muc", PlaylistText.Move(text, 3, 0));
        Assert.Equal(text, PlaylistText.Move(text, 1, 1));
        Assert.Equal(text, PlaylistText.Move(text, 9, 0));
        Assert.Equal("# nhạc mở đầu\r\nA.mp4\r\n\r\nB.mp4\r\n# cuối\r\nD.mp4", PlaylistText.Remove(text, 2));
        Assert.Equal(text, PlaylistText.Remove(text, 4));
        Assert.Equal("A.mp4\r\nX.mp4\r\nY\\", PlaylistText.Append("A.mp4\r\n\r\n", ["X.mp4", "Y\\"]));
        Assert.Equal("X.mp4", PlaylistText.Append("", ["X.mp4"]));

        // Kéo thả từ Explorer: chỉ nhận file video / nhạc và thư mục, theo thứ tự tên.
        var dir = NewDir();
        foreach (var f in new[] { "10.mp4", "2.mp4", "ghi-chu.txt" }) File.WriteAllText(Path.Combine(dir, f), "");
        var data = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(dir, "10.mp4"), Path.Combine(dir, "ghi-chu.txt"), dir, Path.Combine(dir, "2.mp4") });
        Assert.Equal([Path.Combine(dir, "2.mp4"), Path.Combine(dir, "10.mp4"), dir], PlaylistText.DroppedPaths(data));
        Assert.Empty(PlaylistText.DroppedPaths(new DataObject(DataFormats.Text, "abc")));
    }

    [Fact]
    public async Task StripShowsFramesOrderDurationsAndProblemsAndDragReorders()
    {
        var dir = NewDir();
        var red = await MakeVideoAsync(dir, "a-do", Red);
        var folder = Directory.CreateDirectory(Path.Combine(dir, "thu muc")).FullName;
        await MakeVideoAsync(folder, "1-xanh", Blue);
        await MakeVideoAsync(folder, "2-la", Green, 3);
        var expand = MediaInfo.ExpanderFor([new VariableDef { Name = "video", Value = "" }]);
        var plan = await MediaInfo.AnalyzeAsync([red, folder, Path.Combine(dir, "khong-co.mp4"), "{{clipboard}}"], expand);
        Assert.Equal([0, 1, 1, 2, 3], plan.Entries.Select(e => e.Item));

        Sta(() =>
        {
            using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(900, 260) };
            var strip = new MediaStrip { Dock = DockStyle.Top };
            form.Controls.Add(strip);
            form.Show();
            var moves = new List<(int, int)>();
            var removed = new List<int>();
            var played = new List<string>();
            strip.MoveRequested += (a, b) => moves.Add((a, b));
            strip.RemoveRequested += removed.Add;
            strip.PlayRequested += played.Add;

            strip.SetPlan(plan);
            PumpUntil(() => strip.PendingThumbnails == 0);
            Assert.Equal(5, strip.RealCount);
            Assert.Equal("1. a-do.mp4 · 0:02", strip.TileText(0));
            Assert.Equal("2. 1-xanh.mp4 · 0:02", strip.TileText(1));
            Assert.Equal("3. 2-la.mp4 · 0:03", strip.TileText(2));
            Assert.EndsWith("khong-co.mp4 — không thấy file / thư mục", strip.TileText(3));
            Assert.Equal("{{clipboard}} — " + MediaInfo.VariableProblem, strip.TileText(4));
            Assert.Contains("2. 1-xanh.mp4 (0:02)", strip.AccessibleDescription);

            // Ảnh thật của từng video nằm đúng ô (vẽ ra ảnh, không chụp màn hình).
            using (var bmp = new Bitmap(strip.Width, strip.Height))
            {
                strip.DrawToBitmap(bmp, new Rectangle(Point.Empty, strip.Size));
                Color At(int tile, double fx) { var b = strip.BoxRect(tile); return bmp.GetPixel(b.X + (int)(b.Width * fx), b.Y + b.Height / 2); }
                Assert.True(Near(At(0, 0.12), Red), $"ô 1: {At(0, 0.12)}");
                Assert.True(Near(At(1, 0.12), Blue), $"ô 2: {At(1, 0.12)}");
                Assert.True(Near(At(2, 0.12), Green), $"ô 3: {At(2, 0.12)}");
                Assert.True(Near(At(0, 0.5), Color.White), "giữa khung hình là ô trắng");
            }
            Assert.False(strip.ThumbnailOf(0)!.IsIcon);
            Assert.Null(strip.ThumbnailOf(3));

            // Kéo ảnh đỏ (mục 0) tới sau ô thứ 4 (dòng lỗi) → mục 0 thành vị trí 2 (thư mục là một dòng).
            Drag(strip, Center(strip.BoxRect(0)), new Point(strip.BoxRect(3).Right + 4, strip.BoxRect(3).Y + 20));
            Assert.Equal([(0, 2)], moves);
            // Thả giữa hai ô của cùng thư mục = thả sau thư mục.
            Assert.Equal(1, strip.TargetIndex(0, 2));
            Assert.Equal(1, strip.TargetIndex(2, 1));                               // trước thư mục = ngay sau mục 0
            Assert.Equal(0, strip.TargetIndex(2, 0));
            Assert.Equal(3, strip.TargetIndex(0, 5));
            // Chọn ô 2 rồi Enter (như nhấp đúp) = phát thử file đó; Delete = bỏ dòng đang chọn (ô 2 thuộc thư mục → bỏ cả dòng thư mục).
            SendMessage(strip.Handle, WM_LBUTTONDOWN, MK_LBUTTON, XY(Center(strip.BoxRect(1))));
            SendMessage(strip.Handle, WM_LBUTTONUP, IntPtr.Zero, XY(Center(strip.BoxRect(1))));
            SendMessage(strip.Handle, WM_KEYDOWN, (IntPtr)Keys.Enter, IntPtr.Zero);
            Assert.Equal([Path.Combine(folder, "1-xanh.mp4")], played);
            Assert.Single(moves);                                                     // nhấp không kéo → không đổi thứ tự
            SendMessage(strip.Handle, WM_KEYDOWN, (IntPtr)Keys.Delete, IntPtr.Zero);
            Assert.Equal([1], removed);

            strip.SetPlan(null);
            Assert.Equal(0, strip.RealCount);
            form.Close();
        });
    }

    [Fact]
    public async Task StepEditorShowsThumbnailsAndEditsThePlaylistFromThem()
    {
        var dir = NewDir();
        var red = await MakeVideoAsync(dir, "Video 01", Red);
        var blue = await MakeVideoAsync(dir, "video 02", Blue);
        Sta(() =>
        {
            var step = new ActionStep { Type = StepType.PlayMedia, Force = true, Text = $"# mở đầu\n{red}\n{blue}" };
            using var f = new StepEditorForm(step) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
            f.Show();
            var strip = Field<MediaStrip>(f, "_mediaStrip");
            var text = Field<TextBox>(f, "_txtText");
            var info = Field<Label>(f, "_lblMediaInfo");
            PumpUntil(() => strip.RealCount == 2 && strip.PendingThumbnails == 0);
            Assert.True(strip.Visible);
            Assert.Equal("Tổng thời lượng: 0:04 · 2 file", info.Text);                  // từng file nằm ở dải ảnh
            Assert.Equal(["1. Video 01.mp4 · 0:02", "2. video 02.mp4 · 0:02"], [strip.TileText(0), strip.TileText(1)]);
            Assert.True(strip.Right <= f.ClientSize.Width && strip.Width > f.LogicalToDeviceUnits(500), $"dải rộng {strip.Width}, form {f.ClientSize.Width}");

            // Kéo ảnh thứ 2 lên trước → đổi đúng dòng trong ô văn bản (ghi chú giữ chỗ).
            Drag(strip, Center(strip.BoxRect(1)), new Point(strip.BoxRect(0).X + 6, strip.BoxRect(0).Y + 20));
            Assert.Equal($"# mở đầu\r\n{blue}\r\n{red}", text.Text);
            PumpUntil(() => strip.RealCount == 2 && strip.TileText(0).StartsWith("1. video 02.mp4"));

            // Chọn ảnh rồi Delete → bỏ dòng đó.
            SendMessage(strip.Handle, WM_LBUTTONDOWN, MK_LBUTTON, XY(Center(strip.BoxRect(0))));
            SendMessage(strip.Handle, WM_LBUTTONUP, IntPtr.Zero, XY(Center(strip.BoxRect(0))));
            SendMessage(strip.Handle, WM_KEYDOWN, (IntPtr)Keys.Delete, IntPtr.Zero);
            Assert.Equal($"# mở đầu\r\n{red}", text.Text);
            PumpUntil(() => strip.RealCount == 1);

            // Thêm (nút ＋ Thêm file… / kéo thả từ Explorer dùng chung) → thêm vào cuối, hiện ngay ảnh mới.
            f.AddMediaPaths([blue]);
            PumpUntil(() => strip.RealCount == 2 && strip.PendingThumbnails == 0);
            Assert.Equal($"# mở đầu\r\n{red}\r\n{blue}", text.Text);
            Assert.False(strip.ThumbnailOf(1)!.IsIcon);
            Assert.True(Near(strip.ThumbnailOf(1)!.Image.GetPixel(6, 6), Blue));

            // Gõ tay đường dẫn sai → ô đỏ ngay trong dải.
            text.Text += "\r\nD:\\khong-co\\c.mp4";
            PumpUntil(() => strip.RealCount == 3);
            Assert.EndsWith("— không thấy file / thư mục", strip.TileText(2));
            Assert.Contains("1 dòng lỗi", info.Text);

            // Loại thao tác khác: không hiện dải.
            Field<ComboBox>(f, "_cboType").SelectedIndex = Array.IndexOf(
                (StepType[])typeof(StepEditorForm).GetField("Types", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!, StepType.Wait);
            Assert.False(strip.Visible);
            f.Close();
        });
    }

    [Fact]
    public async Task TemplateSetupChoosesVideoWithThumbnailAndDuration()
    {
        var dir = NewDir();
        var red = await MakeVideoAsync(dir, "video1", Red, 3);
        var resource = typeof(Job).Assembly.GetManifestResourceNames().Single(n => n.EndsWith("vi-du-mau.json"));
        using var stream = typeof(Job).Assembly.GetManifestResourceStream(resource)!;
        var job = JobStore.ImportJson(new StreamReader(stream).ReadToEnd()).Single(j => j.Name.StartsWith("15 · 15:30"));
        var items = TemplateSetup.Collect([job], remember: false);
        Assert.Equal(["video1", "video2"], items.Where(i => i.IsMediaFile).Select(i => i.Key));
        Assert.All(items.Where(i => i.Kind == TemplateSetup.ItemKind.Variable), i => Assert.True(i.IsMediaFile));
        // Biến chỉ là một phần của dòng (thư mục + tên file) hay dùng ở bước khác → ô nhập thường.
        var other = new Job
        {
            Variables = [new VariableDef { Name = "thuMuc", Value = "<thư mục>" }, new VariableDef { Name = "ten", Value = "<tên>" }],
            Steps = [S(StepType.PlayMedia, s => s.Text = "{{thuMuc}}\\a.mp4"), S(StepType.LogMessage, s => s.Text = "{{ten}}")]
        };
        Assert.DoesNotContain(TemplateSetup.Collect([other], remember: false), i => i.IsMediaFile);

        Sta(() =>
        {
            using var f = new TemplateSetupForm(items, "Thử", null) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
            f.Show();
            var inputs = Field<Dictionary<TemplateSetup.Item, TextBox>>(f, "_inputs");
            var previews = Field<Dictionary<TemplateSetup.Item, PictureBox>>(f, "_previews");
            var status = Field<Dictionary<TemplateSetup.Item, Label>>(f, "_status");
            var video1 = items.Single(i => i.Key == "video1");
            Assert.Equal(2, previews.Count);
            Assert.Equal(2, AllControls(f).OfType<Button>().Count(b => b.Text == "Chọn video…"));
            Assert.Equal(2, AllControls(f).OfType<Button>().Count(b => b.Text == "Thư mục…"));
            PumpUntil(() => status[video1].Text == "⚠ chưa chọn video");
            Assert.Null(previews[video1].Image);

            inputs[video1].Text = red;
            PumpUntil(() => previews[video1].Image != null);
            Assert.Equal("✔ 0:03", status[video1].Text);
            Assert.True(Near(((Bitmap)previews[video1].Image!).GetPixel(4, 4), Red));

            inputs[video1].Text = Path.Combine(dir, "khong-co.mp4");
            PumpUntil(() => status[video1].Text == "⚠ không thấy file");
            Assert.Null(previews[video1].Image);

            // Biến là cả thư mục (dòng thư mục = phát mọi video trong đó): số video + ảnh của video đầu tiên.
            inputs[video1].Text = dir;
            PumpUntil(() => status[video1].Text == "📁 thư mục: 1 video" && previews[video1].Image != null);
            Assert.True(Near(((Bitmap)previews[video1].Image!).GetPixel(4, 4), Red));
            inputs[video1].Text = Directory.CreateDirectory(Path.Combine(dir, "trong")).FullName;
            PumpUntil(() => status[video1].Text == "⚠ thư mục không có video");
            Assert.Null(previews[video1].Image);

            // Gõ liên tiếp: lần đọc của nội dung cũ bị hủy, chỉ kết quả của nội dung cuối được hiện; đóng khi đang đọc không lỗi.
            inputs[video1].Text = red;
            inputs[video1].Text = dir;
            PumpUntil(() => status[video1].Text == "📁 thư mục: 1 video");
            inputs[video1].Text = red;
            f.Close();
            PumpFor(300);
        });
    }

    [Fact]
    public async Task CanvasNodeShowsTheFirstVideoFrame()
    {
        var dir = NewDir();
        var blue = await MakeVideoAsync(dir, "dau-tien", Blue);
        Sta(() =>
        {
            var steps = new List<ActionStep>
            {
                S(StepType.PlayMedia, s => s.Text = "# ghi chú\n{{video1}}\nD:\\khac.mp4"),
                S(StepType.PlayMedia, s => s.Text = "{{chuaCo}}"),
                S(StepType.Wait)
            };
            using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(900, 500) };
            var designer = new FlowDesigner { Dock = DockStyle.Fill };
            designer.ExpandMediaLine = MediaInfo.ExpanderFor([new VariableDef { Name = "video1", Value = blue }]);
            form.Controls.Add(designer);
            form.Show();
            designer.SetSteps(steps);
            Assert.Null(designer.MediaThumbnail(steps[0]));                    // tải ở nền
            PumpUntil(() => designer.MediaThumbnail(steps[0]) != null);
            var image = designer.MediaThumbnail(steps[0])!;
            Assert.True(Near(image.GetPixel(4, 4), Blue));
            Assert.Null(designer.MediaThumbnail(steps[1]));                    // biến chưa có giá trị → biểu tượng thường
            Assert.Null(designer.MediaThumbnail(steps[2]));

            // Đổi danh sách phát → ảnh cũ bỏ, tải lại theo dòng mới.
            steps[0].Text = "";
            Assert.Null(designer.MediaThumbnail(steps[0]));
            form.Close();
        });
    }

    [Fact]
    public void StripSelectionFollowsMovedItemAndDropMarkerShowsWhereTheItemLands()
    {
        var dir = NewDir();
        Sta(() =>
        {
            using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(1000, 320) };
            var strip = new MediaStrip { Dock = DockStyle.Top };
            form.Controls.Add(strip);
            form.Show();
            // Như ô sửa bước: sửa danh sách rồi hiện lại danh sách mới.
            List<string[]> lines = [["A.mp4"], ["B.mp4"], ["C.mp4"]];
            var moves = new List<(int, int)>();
            strip.MoveRequested += (from, to) =>
            {
                moves.Add((from, to));
                var line = lines[from];
                lines.RemoveAt(from);
                lines.Insert(to, line);
                strip.SetPlan(PlanOf(dir, lines));
            };
            strip.RemoveRequested += item =>
            {
                lines.RemoveAt(item);
                strip.SetPlan(PlanOf(dir, lines));
            };
            strip.SetPlan(PlanOf(dir, lines));
            string Order() => string.Join(",", lines.Select(l => l[0][0]));
            string Selected() => strip.TileText(strip.SelectedTile);

            // Ctrl+→ hai lần: A đi tiếp hai chỗ (ô chọn theo A), không đổi qua đổi lại.
            Click(strip, 0);
            Key(strip, Keys.Control | Keys.Right);
            Key(strip, Keys.Control | Keys.Right);
            Assert.Equal("B,C,A", Order());
            Assert.Equal("3. A.mp4", Selected());
            Key(strip, Keys.Control | Keys.Left);
            Assert.Equal("B,A,C", Order());
            Assert.Equal("2. A.mp4", Selected());
            // Delete: chọn ảnh kề (mục sau; bỏ mục cuối thì mục trước).
            Key(strip, Keys.Delete);
            Assert.Equal("B,C", Order());
            Assert.Equal("2. C.mp4", Selected());
            Key(strip, Keys.Delete);
            Assert.Equal("1. B.mp4", Selected());

            // A | F1 F2 F3 (một dòng thư mục) | B
            lines = [["A.mp4"], ["F1.mp4", "F2.mp4", "F3.mp4"], ["B.mp4"]];
            strip.SetPlan(PlanOf(dir, lines));
            moves.Clear();
            var inFolder = new Point(strip.BoxRect(2).X - 3, strip.BoxRect(2).Y + 20);      // giữa F1 và F2
            // Kéo B vào giữa thư mục: B vẫn ở sau thư mục → không có vạch chèn, thả không đổi gì.
            Press(strip, 4, inFolder);
            Assert.Equal(-1, Field<int>(strip, "_dropBefore"));
            Release(strip, inFolder);
            Assert.Empty(moves);
            // Kéo A vào giữa thư mục: vạch chèn vẽ sau F3 — đúng chỗ A sẽ tới.
            Press(strip, 0, inFolder);
            Assert.Equal(4, Field<int>(strip, "_dropBefore"));
            Release(strip, inFolder);
            Assert.Equal([(0, 1)], moves);
            Assert.Equal("4. A.mp4", Selected());                                         // ô chọn theo ảnh vừa kéo

            // Esc khi đang kéo: hủy kéo (phím của dải, không để hộp thoại coi là "Hủy").
            moves.Clear();
            var start = new Point(strip.BoxRect(0).X + 6, strip.BoxRect(0).Y + 20);
            Press(strip, 4, start);
            Assert.True(InputKey(strip, Keys.Escape));
            SendMessage(strip.Handle, WM_KEYDOWN, (IntPtr)Keys.Escape, IntPtr.Zero);
            Release(strip, start);
            Assert.Empty(moves);
            Assert.False(InputKey(strip, Keys.Escape));                                    // không kéo → Esc vẫn là phím của hộp thoại
            // Thả xa ngoài dải: hủy kéo.
            var outside = new Point(strip.BoxRect(4).Right + 4, strip.Height + 200);
            Press(strip, 0, outside);
            Release(strip, outside);
            Assert.Empty(moves);

            // Thư mục nhiều file hơn số ô hiện: chữ "Bỏ" đếm cả thư mục và nói rõ bỏ cả dòng thư mục.
            var many = Enumerable.Range(1, 45).Select(i => $"{i:00}.mp4").ToArray();
            var plan = PlanOf(dir, [many, ["Z.mp4"]]);
            strip.SetPlan(plan);
            Assert.Equal(40, strip.RealCount);
            Assert.Equal(45, strip.FilesOfItem(0));
            Assert.Equal("✕ Bỏ cả dòng thư mục \"thu muc0\" khỏi danh sách (45 file)", strip.RemoveText(plan.Entries[0]));
            Assert.Equal("✕ Bỏ khỏi danh sách", strip.RemoveText(plan.Entries[45]));

            // Tên dài hơn hai dòng: bỏ bớt ở giữa, giữ phần cuối + đuôi file (hai file khác nhau ở cuối tên vẫn phân biệt được).
            const string name1 = "Bai giang mon Toan lop 5A hoc ky 2 tuan 12 tiet 3 phan 1.mp4";
            var caption1 = strip.Caption(name1);
            var caption2 = strip.Caption(name1.Replace("phan 1", "phan 2"));
            Assert.Contains("…", caption1);
            Assert.EndsWith("phan 1.mp4", caption1);
            Assert.EndsWith("phan 2.mp4", caption2);
            Assert.Equal("a.mp4", strip.Caption("a.mp4"));
            // Hai dòng tên nằm trên thanh cuộn, không bị che.
            var (nameBottom, scrollTop) = strip.CaptionLayout();
            Assert.True(nameBottom <= scrollTop, $"tên tới {nameBottom}, thanh cuộn từ {scrollTop}");
            form.Close();
        });
    }

    [Fact]
    public async Task StripCancelsThumbnailLoadsOfRemovedFiles()
    {
        var dir = NewDir();
        var red = await MakeVideoAsync(dir, "do", Red);
        var blue = await MakeVideoAsync(dir, "xanh", Blue);
        static MediaInfo.Plan One(string path) => new([new MediaInfo.Entry(path, path, null, null)], TimeSpan.Zero, false);
        Sta(() =>
        {
            using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(900, 260) };
            var strip = new MediaStrip { Dock = DockStyle.Top };
            form.Controls.Add(strip);
            form.Show();
            strip.SetPlan(One(red));
            Assert.Equal(1, strip.PendingThumbnails);
            // Ảnh đỏ chưa tải xong thì file đã bị bỏ → hủy ngay, không giữ chỗ tải, không đưa lại vào bộ nhớ.
            strip.SetPlan(One(blue));
            Assert.Equal(1, strip.PendingThumbnails);
            PumpUntil(() => strip.PendingThumbnails == 0);
            PumpFor(300);
            var thumbs = Field<Dictionary<string, MediaThumbnails.Thumbnail?>>(strip, "_thumbs");
            Assert.Equal([blue], thumbs.Keys.ToList());
            Assert.True(Near(strip.ThumbnailOf(0)!.Image.GetPixel(6, 6), Blue));

            // Đóng khi còn đang tải → không lỗi; ảnh tải xong sau đó bị bỏ.
            strip.SetPlan(One(red));
            Assert.Equal(1, strip.PendingThumbnails);
            form.Close();
            form.Dispose();
            Assert.True(strip.IsDisposed);
            Assert.Equal(0, strip.PendingThumbnails);
            PumpFor(500);
        });
    }

    [Fact]
    public async Task AnalysisTurnsUnreadableLinesIntoProblemTilesInsteadOfFailing()
    {
        var dir = NewDir();
        var red = await MakeVideoAsync(dir, "do", Red);
        // {{now:%}}: định dạng ngày sai (FormatException); "C:\Documents and Settings": thư mục có nhưng không liệt kê được.
        var lines = new List<string> { "{{now:%}}", red };
        var denied = Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!, "Documents and Settings");
        bool junction = Directory.Exists(denied);
        if (junction) lines.Add(denied);
        var plan = await MediaInfo.AnalyzeAsync(lines, MediaInfo.ExpanderFor([]));
        Assert.Equal(junction ? 3 : 2, plan.Entries.Count);
        Assert.Null(plan.Entries[0].Path);
        Assert.StartsWith("không đọc được: ", plan.Entries[0].Problem);
        Assert.Equal((1, red), (plan.Entries[1].Item, plan.Entries[1].Path));
        if (junction)
        {
            Assert.Equal(2, plan.Entries[2].Item);
            Assert.StartsWith("không đọc được: ", plan.Entries[2].Problem);
        }
        Assert.False(plan.Complete);
        Assert.Equal(0, MediaInfo.ToMs(plan));
    }

    [Fact]
    public void PreviewResultSkippedIsNotAnError()
    {
        Assert.Equal(("⏭ Đã bỏ qua phát thử \"a.mp4\".", true), StepEditorForm.DescribePreview(new PlaybackResult(0, [], false, Skipped: 1), @"D:\v\a.mp4"));
        Assert.Equal(("✔ Đã phát xong \"a.mp4\".", true), StepEditorForm.DescribePreview(new PlaybackResult(1, [], false), @"D:\v\a.mp4"));
        Assert.Equal(("■ Đã dừng phát thử.", true), StepEditorForm.DescribePreview(new PlaybackResult(0, [], true), @"D:\v\a.mp4"));
        Assert.Equal(("✖ Không phát được: hỏng", false), StepEditorForm.DescribePreview(new PlaybackResult(0, ["hỏng"], false), @"D:\v\a.mp4"));
    }

    [Fact]
    public async Task StepEditorIgnoresStripEditsOnAnOldPlanAndEscDoesNotDropEdits()
    {
        var dir = NewDir();
        var red = await MakeVideoAsync(dir, "do", Red);
        var blue = await MakeVideoAsync(dir, "xanh", Blue);
        Sta(() =>
        {
            var step = new ActionStep { Type = StepType.PlayMedia, Force = true, Text = $"{red}\n{blue}" };
            using var f = new StepEditorForm(step) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
            f.Show();
            var strip = Field<MediaStrip>(f, "_mediaStrip");
            var text = Field<TextBox>(f, "_txtText");
            PumpUntil(() => strip.RealCount == 2 && strip.PendingThumbnails == 0);

            // Dán thêm một file lên đầu; một lần tính cũ xong cho đúng nội dung mới nhưng dải vẫn hiện danh sách cũ (đỏ, xanh).
            var changed = $"{blue}\r\n{red}\r\n{blue}";
            text.Text = changed;
            typeof(StepEditorForm).GetField("_mediaPlanText", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(f, changed);
            Click(strip, 1);
            SendMessage(strip.Handle, WM_KEYDOWN, (IntPtr)Keys.Delete, IntPtr.Zero);
            Assert.Equal(changed, text.Text);                                              // không bỏ nhầm dòng đỏ
            PumpUntil(() => strip.RealCount == 3 && strip.PendingThumbnails == 0);
            Click(strip, 2);
            SendMessage(strip.Handle, WM_KEYDOWN, (IntPtr)Keys.Delete, IntPtr.Zero);
            Assert.Equal($"{blue}\r\n{red}", text.Text);
            PumpUntil(() => strip.RealCount == 2 && strip.TileText(1).StartsWith("2. do.mp4"));

            // Esc khi đang kéo ảnh: chỉ hủy kéo — hộp thoại không đóng, danh sách giữ nguyên.
            var after = new Point(strip.BoxRect(1).Right + 4, strip.BoxRect(1).Y + 20);
            Press(strip, 0, after);
            var esc = Message.Create(strip.Handle, WM_KEYDOWN, (IntPtr)Keys.Escape, IntPtr.Zero);
            Assert.False(strip.PreProcessMessage(ref esc));
            SendMessage(strip.Handle, WM_KEYDOWN, (IntPtr)Keys.Escape, IntPtr.Zero);
            Release(strip, after);
            Assert.Equal($"{blue}\r\n{red}", text.Text);
            Assert.Equal(DialogResult.None, f.DialogResult);

            // Esc khi đang phát thử: dừng phát thử, không bấm "Hủy".
            var previewField = typeof(StepEditorForm).GetField("_previewCts", BindingFlags.NonPublic | BindingFlags.Instance)!;
            using var preview = new CancellationTokenSource();
            previewField.SetValue(f, preview);
            esc = Message.Create(strip.Handle, WM_KEYDOWN, (IntPtr)Keys.Escape, IntPtr.Zero);
            Assert.True(strip.PreProcessMessage(ref esc));
            Assert.True(preview.IsCancellationRequested);
            Assert.Equal(DialogResult.None, f.DialogResult);
            previewField.SetValue(f, null);
            // Không phát thử: Esc = "Hủy" như cũ.
            esc = Message.Create(strip.Handle, WM_KEYDOWN, (IntPtr)Keys.Escape, IntPtr.Zero);
            Assert.True(strip.PreProcessMessage(ref esc));
            Assert.Equal(DialogResult.Cancel, f.DialogResult);
            f.Close();
        });
    }

    private static IEnumerable<Control> AllControls(Control c) => c.Controls.Cast<Control>().SelectMany(x => new[] { x }.Concat(AllControls(x)));
}
