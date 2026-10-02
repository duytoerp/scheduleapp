using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;
using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Recording;
using ScheduleApp.Vision;
using Xunit.Abstractions;

namespace ScheduleApp.Tests;

public class ClickAnchorTests(ITestOutputHelper output)
{
    private static (Bitmap Shot, Dictionary<string, Rectangle> Parts) Scene(int dx = 0, int dy = 0, string? hot = null, int width = 760, int height = 520)
    {
        Application.EnableVisualStyles();
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var parts = new Dictionary<string, Rectangle>();
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        using var font = new Font("Segoe UI", 9f);

        void Button(string name, string text, Rectangle r)
        {
            r.Offset(dx, dy);
            ButtonRenderer.DrawButton(g, r, text, font, false, name == hot ? PushButtonState.Hot : PushButtonState.Normal);
            parts[name] = r;
        }
        void Text(string name, string text, Rectangle r)
        {
            r.Offset(dx, dy);
            TextRenderer.DrawText(g, text, font, r, Color.Black, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            parts[name] = r;
        }

        Button("luu", "Lưu", new(20, 20, 80, 28));
        Button("xoa", "Xóa", new(106, 20, 80, 28));
        Button("in", "In", new(192, 20, 80, 28));
        Button("excel", "Xuất Excel", new(278, 20, 100, 28));
        for (int i = 0; i < 6; i++)
        {
            Text($"nhan{i}", $"Đơn hàng #{1001 + i} — Khách {(char)('A' + i)}", new(20, 70 + i * 36, 220, 28));
            Button($"sua{i}", "Sửa", new(246, 70 + i * 36, 64, 28));
        }
        Text("lblTen", "Tên khách hàng:", new(20, 300, 120, 24));
        var box = new Rectangle(146 + dx, 300 + dy, 220, 24);
        using (var pen = new Pen(Color.FromArgb(122, 122, 122))) g.DrawRectangle(pen, box);
        parts["oTen"] = box;
        return (bmp, parts);
    }

    private static Point Center(Rectangle r) => ClickAnchor.Center(r);

    /// <summary>Ghi click tại <paramref name="click"/> trên ảnh lúc ghi rồi tìm lại trên ảnh lúc chạy như khi phát lại.</summary>
    private static (ActionStep Step, ClickAnchor.Choice Choice, MatchResult? Match, Point? ClickAt) RecordAndReplay(
        Bitmap recorded, Point click, Rectangle? element, Bitmap replay, IReadOnlyCollection<Rectangle>? covered = null)
    {
        var choice = ClickAnchor.Choose(recorded, click, element, 1.0, covered ?? []);
        Assert.NotNull(choice);
        var step = new ActionStep { Type = StepType.MouseClick, Target = "exe:demo", X = click.X, Y = click.Y };
        ClickAnchor.Apply(step, recorded, choice, 1.0);
        using var template = ScreenCapture.FromBase64Png(step.ImageData);
        // Vị trí lúc ghi (so với cửa sổ) chỉ dùng để chọn giữa các chỗ giống nhau.
        var expected = new Point(click.X - step.ImageOffsetX, click.Y - step.ImageOffsetY);
        var m = ClickAnchor.Pick(ImageMatcher.FindAll(replay, template), step.Confidence / 100.0, expected);
        return (step, choice, m, m == null ? null : Center(m.Bounds) + new Size(step.ImageOffsetX, step.ImageOffsetY));
    }

    [Fact]
    public void MovedButtonIsFoundAndClickedAtTheSameSpot()
    {
        var (a, pa) = Scene(hot: "excel");
        // Bố cục dời chỗ trong cửa sổ to hơn: tọa độ cũ rơi vào chỗ khác, hình mẫu vẫn tìm ra nút.
        var (b, pb) = Scene(dx: 137, dy: 41, width: 1000, height: 700);
        var click = Center(pa["excel"]) + new Size(-20, 4);
        var (step, choice, m, at) = RecordAndReplay(a, click, pa["excel"], b);

        Assert.True(choice.FromElement);
        Assert.Equal(Rectangle.Inflate(pa["excel"], 2, 2), choice.Bounds);
        Assert.Equal(new Point(-20, 4), choice.Offset);
        Assert.Equal(ClickAnchor.DefaultConfidence, step.Confidence);
        Assert.True(step.HasImageAnchor);
        Assert.NotNull(m);
        Assert.Equal(click + new Size(137, 41), at);
        Assert.True(pb["excel"].Contains(at!.Value));
        Assert.False(pa["excel"].Contains(at.Value));
    }

    [Fact]
    public void ReplayMapsBackToScreenCoordinates()
    {
        // Lúc ghi: click vào "Xuất Excel", lệch trái chữ 20px (tọa độ theo cửa sổ).
        var (a, pa) = Scene(hot: "excel");
        var click = Center(pa["excel"]) + new Size(-20, 4);
        var choice = ClickAnchor.Choose(a, click, pa["excel"], 1.0, [])!;
        var step = new ActionStep { Type = StepType.MouseClick, Target = "exe:demo", X = click.X, Y = click.Y };
        ClickAnchor.Apply(step, a, choice, 1.0);
        using var template = ScreenCapture.FromBase64Png(step.ImageData);

        // Lúc chạy: cửa sổ ở (900, 50) trên màn hình, bố cục bên trong xê dịch (137, 41).
        var (b, _) = Scene(dx: 137, dy: 41, width: 1000, height: 700);
        var window = new Point(900, 50);
        var r = ScreenLocator.MatchImage(step, b, window, template, window + new Size(step.X, step.Y));
        Assert.True(r.Found, r.Detail);
        Assert.Equal(window + new Size(click.X + 137, click.Y + 41), r.ClickPoint);
    }

    [Fact]
    public void ReplayPicksTheRecordedRowAmongIdenticalButtons()
    {
        // Hình mẫu chỉ có nút "Sửa" (giống hệt ở mọi dòng, vd tự chụp) → chọn dòng gần vị trí lúc ghi nhất.
        var (a, pa) = Scene();
        using var template = ScreenCapture.Crop(a, Rectangle.Inflate(pa["sua4"], 2, 2));
        var origin = new Point(50, 60);
        var at = origin + (Size)Center(pa["sua4"]);
        var step = new ActionStep
        {
            Type = StepType.MouseClick, Target = "exe:demo", X = at.X, Y = at.Y, ImageData = "x",
            ImageWidth = template.Width, ImageHeight = template.Height, Confidence = ClickAnchor.DefaultConfidence
        };
        var r = ScreenLocator.MatchImage(step, a, origin, template, at);
        Assert.True(r.Found, r.Detail);
        Assert.Equal(at, r.ClickPoint);
        Assert.Contains("6 chỗ giống nhau", r.Detail);

        var lower = ScreenLocator.MatchImage(step, a, origin, template, origin + (Size)Center(pa["sua1"]) + new Size(0, 10));
        Assert.Equal(origin + (Size)Center(pa["sua1"]), lower.ClickPoint);
    }

    [Fact]
    public void ScaledTemplateScalesTheClickOffset()
    {
        // Ghi ở 100%, chạy ở 150%: hình mẫu được phóng 1,5× → độ lệch điểm click cũng 1,5×.
        using var template = new Bitmap(60, 30);
        using (var g = Graphics.FromImage(template))
        {
            g.Clear(Color.White);
            using var font = new Font("Segoe UI", 11f, FontStyle.Bold);
            g.DrawRectangle(Pens.DarkBlue, 1, 1, 57, 27);
            TextRenderer.DrawText(g, "OK 7", font, new Rectangle(0, 0, 60, 30), Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        using var shot = new Bitmap(400, 200);
        using (var g = Graphics.FromImage(shot))
        {
            g.Clear(Color.White);
            g.DrawImage(template, new Rectangle(100, 50, 60, 30));
        }
        var step = new ActionStep { Type = StepType.MouseClick, ImageData = "x", ImageWidth = 40, ImageHeight = 20, ImageOffsetX = 10, ImageOffsetY = -4 };
        var r = ScreenLocator.MatchImage(step, shot, new Point(-1920, 0), template, null);
        Assert.True(r.Found, r.Detail);
        Assert.Equal(new Point(-1920 + 100 + 30 + 15, 50 + 15 - 6), r.ClickPoint);
    }

    [Fact]
    public void HoveredButtonStillMatchesItsNormalState()
    {
        // Lúc ghi nút đang được rê chuột (hover); lúc chạy nút ở trạng thái thường và đã dời chỗ.
        var (a, pa) = Scene(hot: "luu");
        var (b, pb) = Scene(dx: 137, dy: 41, width: 1000, height: 700);
        var (_, _, m, at) = RecordAndReplay(a, Center(pa["luu"]), pa["luu"], b);
        Assert.NotNull(m);
        output.WriteLine($"hover → thường: {m.Score:0.000}");
        Assert.True(pb["luu"].Contains(at!.Value));

        // Các nút cùng kiểu khác chữ không bị nhận nhầm.
        using var template = ScreenCapture.Crop(a, Rectangle.Inflate(pa["luu"], 2, 2));
        var others = ImageMatcher.FindAll(b, template).Where(x => !x.Bounds.IntersectsWith(pb["luu"])).ToList();
        Assert.All(others, x => Assert.True(x.Score < ClickAnchor.AmbiguousScore, $"{x.Bounds} {x.Score:0.000}"));
    }

    [Fact]
    public void RepeatedButtonsTakeTheirRowAsContext()
    {
        var (a, pa) = Scene();
        var click = Center(pa["sua3"]);
        var (_, choice, m, at) = RecordAndReplay(a, click, pa["sua3"], a);

        // Nút "Sửa" giống hệt nhau ở mọi dòng → hình mẫu lấy rộng ra cho tới khi phân biệt được dòng này.
        Assert.False(choice.FromElement);
        Assert.True(choice.Bounds.Contains(pa["sua3"]));
        Assert.NotNull(m);
        Assert.Equal(click, at);
        using var template = ScreenCapture.Crop(a, choice.Bounds);
        var others = ImageMatcher.FindAll(a, template).Where(x => ImageMatcher.Overlap(x.Bounds, choice.Bounds) < 0.5);
        Assert.All(others, x => Assert.True(x.Score < ClickAnchor.AmbiguousScore));
    }

    [Fact]
    public void RecorderAttachesImagesInTheBackground()
    {
        var area = new Rectangle(1000, 300, 760, 520); // vùng chụp quanh điểm click trên màn hình
        Point Screen(Point p) => new(p.X + area.X, p.Y + area.Y);
        var root = new IntPtr(0x1234);
        MacroRecorder.Press Press(Point click, Rectangle? element, Bitmap shot) => new(
            click, MouseButtonKind.Left, root, root, "exe:demo", 0,
            element is { } e ? Task.FromResult<UiElementFinder.CapturedElement?>(
                new(root, "Name=Xuất Excel; ControlType=Button", "", new Rectangle(Screen(e.Location), e.Size))) : null,
            new MacroRecorder.Snapshot(shot, area, 1.0, []));

        // Nút nhận diện được → "Click phần tử UI", hình mẫu đúng khung nút làm dự phòng.
        var (a, pa) = Scene(hot: "excel");
        var element = new ActionStep { Type = StepType.MouseClick, Target = "exe:demo", X = 400, Y = 50 };
        var plain = new ActionStep { Type = StepType.MouseClick, Target = "exe:demo", X = 300, Y = 200 };
        var blank = new ActionStep { Type = StepType.MouseClick, Target = "exe:demo", X = 600, Y = 300 };
        using var recorder = new MacroRecorder();
        recorder.Recognize(element, Press(Screen(Center(pa["excel"])), pa["excel"], a));
        // Không nhận diện được phần tử → vẫn "Click chuột", kèm hình mẫu, chờ hình tối đa 5 giây.
        var (b, pb) = Scene();
        recorder.Recognize(plain, Press(Screen(Center(pb["sua3"])), null, b));
        // Vùng trống → giữ tọa độ.
        var (c, _) = Scene();
        recorder.Recognize(blank, Press(Screen(new Point(580, 300)), null, c));
        recorder.Stop();

        Assert.Equal(StepType.ClickElement, element.Type);
        Assert.Equal("Name=Xuất Excel; ControlType=Button", element.Text);
        Assert.True(element.HasImageAnchor);
        Assert.Equal((104, 32, 0, 0), (element.ImageWidth, element.ImageHeight, element.ImageOffsetX, element.ImageOffsetY));
        Assert.Equal(5_000, element.DelayMs);

        Assert.Equal(StepType.MouseClick, plain.Type);
        Assert.True(plain.HasImageAnchor);
        Assert.Equal(ClickAnchor.DefaultTimeoutMs, plain.DelayMs);
        Assert.Contains("theo hình mẫu", plain.Describe());

        Assert.False(blank.HasImageAnchor);
        Assert.Equal(1000, blank.DelayMs);
    }

    [Fact]
    public void BlankAreaKeepsCoordinates()
    {
        var (a, _) = Scene();
        Assert.Null(ClickAnchor.Choose(a, new Point(580, 300), null, 1.0, []));
        Assert.Null(ClickAnchor.Choose(a, new Point(5000, 300), null, 1.0, []));
    }

    [Fact]
    public void CoveredAreaIsLeftOut()
    {
        // Tooltip / cửa sổ khác che một phần nút lúc ghi → không đưa phần đó vào hình mẫu.
        var (a, pa) = Scene();
        var tooltip = new Rectangle(95, 45, 150, 24);
        var choice = ClickAnchor.Choose(a, Center(pa["luu"]), pa["luu"], 1.0, [tooltip]);
        Assert.NotNull(choice);
        Assert.False(choice.FromElement);
        Assert.False(choice.Bounds.IntersectsWith(tooltip));
        Assert.Null(ClickAnchor.Choose(a, Center(pa["luu"]), pa["luu"], 1.0, [new Rectangle(0, 0, a.Width, a.Height)]));
    }

    [Fact]
    public void NearEdgeKeepsTheClickOffset()
    {
        var (a, _) = Scene(dx: -10, dy: -14);
        // Click sát góc trên trái của ảnh chụp: vùng hình mẫu bị đẩy vào trong, điểm click lệch khỏi tâm.
        var click = new Point(12, 8);
        var choice = ClickAnchor.Choose(a, click, null, 1.0, []);
        Assert.NotNull(choice);
        Assert.Equal(0, choice.Bounds.X);
        Assert.Equal(0, choice.Bounds.Y);
        Assert.Equal(click, Center(choice.Bounds) + new Size(choice.Offset));
    }

    [Fact]
    public void PickPrefersTheRecordedSpotAmongEqualMatches()
    {
        List<MatchResult> matches =
        [
            new(new Rectangle(400, 100, 40, 20), 0.96),
            new(new Rectangle(100, 100, 40, 20), 0.95),
            new(new Rectangle(700, 100, 40, 20), 0.70)
        ];
        Assert.Equal(400, ClickAnchor.Pick(matches, 0.8, null)!.Bounds.X);
        Assert.Equal(100, ClickAnchor.Pick(matches, 0.8, new Point(125, 105))!.Bounds.X);
        Assert.Equal(400, ClickAnchor.Pick(matches, 0.8, new Point(690, 105))!.Bounds.X); // 0,70 không đạt ngưỡng
        Assert.Equal(400, ClickAnchor.Pick([matches[0], new(new Rectangle(100, 100, 40, 20), 0.85)], 0.8, new Point(125, 105))!.Bounds.X);
        Assert.Null(ClickAnchor.Pick(matches, 0.97, new Point(125, 105)));
        Assert.Null(ClickAnchor.Pick([], 0.8, null));
    }

    [Fact]
    public void StepKeepsAnchorInJsonAndDescription()
    {
        var step = new ActionStep
        {
            Type = StepType.MouseClick, Target = "exe:demo", X = 300, Y = 40, ImageData = "iVBORw0KGgo=",
            ImageWidth = 84, ImageHeight = 32, ImageScale = 1.25, ImageOffsetX = -20, ImageOffsetY = 4
        };
        Assert.True(step.HasImageAnchor);
        Assert.True(step.UsesScreen);
        Assert.Contains("theo hình mẫu 84×32", step.Describe());
        Assert.Contains("dự phòng (300, 40)", step.Describe());

        var json = System.Text.Json.JsonSerializer.Serialize(step, JsonDefaults.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<ActionStep>(json, JsonDefaults.Options)!;
        Assert.Equal((-20, 4), (back.ImageOffsetX, back.ImageOffsetY));
        Assert.True(back.HasImageAnchor);
        Assert.DoesNotContain("ImageOffset", System.Text.Json.JsonSerializer.Serialize(new ActionStep(), JsonDefaults.Options));

        var element = new ActionStep { Type = StepType.ClickElement, Target = "exe:demo", Text = "Name=Lưu", X = 5, Y = 6, ImageData = "x" };
        Assert.Contains("dự phòng: hình mẫu, (5, 6)", element.Describe());
        Assert.False(new ActionStep { Type = StepType.ClickImage, ImageData = "x" }.HasImageAnchor);
        Assert.False(new ActionStep { Type = StepType.MouseClick }.HasImageAnchor);
        Assert.Contains("tại (0, 0)", new ActionStep { Type = StepType.MouseClick }.Describe());
    }
}
