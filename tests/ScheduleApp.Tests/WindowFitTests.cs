using System.Drawing;
using ScheduleApp.Models;
using ScheduleApp.UI;

namespace ScheduleApp.Tests;

/// <summary>Cửa sổ tự vừa màn hình từng máy: chiếm cùng tỉ lệ màn hình, thu cho vừa màn nhỏ, nhớ vị trí theo tỉ lệ (không theo pixel).</summary>
public class WindowFitTests
{
    private static readonly Rectangle Laptop = new(0, 0, 1366, 728);        // 1366×768, thanh tác vụ 40 px
    private static readonly Rectangle FullHd = new(0, 0, 1920, 1032);
    private static readonly Rectangle Wide = new(-2560, 0, 2560, 1400);    // màn thứ hai bên trái

    [Fact]
    public void LargeWindowsTakeTheSameShareOfEveryScreen()
    {
        var share = new SizeF(0.8f, 0.88f);
        var min = new Size(980, 680);
        var hd = WindowFit.Compute(new Size(1200, 860), min, FullHd, share, FullHd);
        Assert.Equal(new Size(1536, 908), hd.Size);
        Assert.Equal(new Point(192, 62), hd.Location);                       // giữa màn hình

        var wide = WindowFit.Compute(new Size(1200, 860), min, Wide, share, Wide);
        Assert.Equal(new Size(2048, 1232), wide.Size);                        // màn lớn → cửa sổ lớn theo cùng tỉ lệ
        Assert.True(Wide.Contains(wide));

        // Màn nhỏ: không nhỏ hơn kích thước tối thiểu, nhưng vẫn nằm gọn trong màn hình.
        var laptop = WindowFit.Compute(new Size(1200, 860), min, Laptop, share, Laptop);
        Assert.Equal(new Size(1093, 680), laptop.Size);
        Assert.True(Laptop.Contains(laptop));
    }

    [Fact]
    public void FixedSizeWindowsShrinkOnlyWhenTheScreenIsTooSmall()
    {
        var keep = WindowFit.Compute(new Size(820, 660), new Size(740, 560), FullHd, SizeF.Empty, FullHd);
        Assert.Equal(new Size(820, 660), keep.Size);

        // Thiết kế 1200×860 (đã nhân 150% DPI = 1800×1290) trên màn 1920×1032 → thu cho vừa, chừa lề.
        var shrunk = WindowFit.Compute(new Size(1800, 1290), new Size(1470, 1020), FullHd, SizeF.Empty, FullHd);
        Assert.Equal(new Size(1800, 1016), shrunk.Size);
        Assert.True(FullHd.Contains(shrunk));
        Assert.Equal(new Size(1350, 712), WindowFit.ClampMinimum(new Size(1470, 1020), Laptop));
        Assert.Equal(new Size(740, 560), WindowFit.ClampMinimum(new Size(740, 560), Laptop));
    }

    [Fact]
    public void DialogIsCenteredOnItsOwnerButStaysOnScreen()
    {
        var owner = new Rectangle(1500, 600, 400, 300);                       // cửa sổ cha sát góc phải dưới
        var r = WindowFit.Compute(new Size(820, 660), Size.Empty, FullHd, SizeF.Empty, owner);
        Assert.True(FullHd.Contains(r));
        Assert.Equal(new Point(1920 - 820, 1032 - 660), r.Location);
        Assert.Equal(new Rectangle(0, 0, 300, 200), WindowFit.Inside(new Rectangle(-20000, -50, 300, 200), FullHd));
    }

    [Fact]
    public void RememberedLayoutFollowsTheScreenNotThePixels()
    {
        // Cửa sổ chiếm nửa phải màn Full HD → mở trên laptop vẫn chiếm nửa phải.
        var saved = WindowFit.Capture(new Rectangle(960, 0, 960, 1032), maximized: false, @"\\.\DISPLAY1", FullHd);
        Assert.Equal((0.5, 0.0, 0.5, 1.0), (saved.X, saved.Y, saved.W, saved.H));
        var (laptop, max) = WindowFit.Restore(saved, [(@"\\.\DISPLAY1", Laptop)], new Size(400, 300))!.Value;
        Assert.Equal(new Rectangle(683, 0, 683, 728), laptop);
        Assert.False(max);

        // Màn hình đã nhớ không còn cắm → dùng màn hình chính (đầu danh sách), vẫn nằm gọn trong đó.
        var (onPrimary, _) = WindowFit.Restore(saved, [(@"\\.\DISPLAY9", FullHd), (@"\\.\DISPLAY3", Wide)], new Size(400, 300))!.Value;
        Assert.True(FullHd.Contains(onPrimary));

        // Kích thước tối thiểu lớn hơn phần đã nhớ → nới ra nhưng không vượt màn hình.
        var tiny = new WindowLayout { Screen = @"\\.\DISPLAY1", X = 0.9, Y = 0.9, W = 0.1, H = 0.1, Maximized = true };
        var (grown, maximized) = WindowFit.Restore(tiny, [(@"\\.\DISPLAY1", Laptop)], new Size(980, 680))!.Value;
        Assert.Equal(new Size(980, 680), grown.Size);
        Assert.True(Laptop.Contains(grown));
        Assert.True(maximized);

        Assert.Null(WindowFit.Restore(null, [(@"\\.\DISPLAY1", Laptop)], Size.Empty));
        Assert.Null(WindowFit.Restore(new WindowLayout(), [(@"\\.\DISPLAY1", Laptop)], Size.Empty));
    }

    [Fact]
    public void ManuallyPlacedFormsAreLeftAlone()
    {
        // Form tự đặt vị trí (thanh gỡ lỗi, cửa sổ ngoài màn hình khi kiểm thử) không bị kéo vào giữa màn hình.
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                using var f = new HistoryForm([], null) { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
                f.Show();
                Assert.Equal(new Point(-20000, -20000), f.Location);
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
    }
}
