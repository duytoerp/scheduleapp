using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.UI;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Ảnh video trên nút "Phát video / nhạc" ở sơ đồ: không thay biến chỉ biết khi chạy / mỗi lần một khác
/// (clipboard, bí mật, công thức, ngày giờ, ngẫu nhiên, guid) và không thay lại biến mỗi lần vẽ.
/// </summary>
public class CanvasMediaTests
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
        if (error != null) throw new Exception(error.Message, error);
    }

    private static ActionStep Media(string text) => S(StepType.PlayMedia, s => s.Text = text);

    private static int MediaThumbCount(FlowDesigner d) =>
        ((System.Collections.ICollection)typeof(FlowDesigner).GetField("_mediaThumbs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(d)!).Count;

    [Theory]
    [InlineData("{{clipboard}}")]
    [InlineData("{{ clipboard }}")]
    [InlineData(@"D:\QC\clip{{random:1-5}}.mp4")]
    [InlineData(@"D:\QC\clip{{random}}.mp4")]
    [InlineData("{{=chon(a.mp4; b.mp4)}}")]
    [InlineData(@"D:\QC\{{guid}}.mp4")]
    [InlineData("{{secret:matkhau}}")]
    [InlineData(@"D:\QC\{{today}}.mp4")]
    [InlineData(@"D:\QC\{{today-1:ddMM}}.mp4")]
    [InlineData(@"D:\QC\{{now:HHmmss}}.mp4")]
    [InlineData(@"D:\QC\{{time}}.mp4")]
    [InlineData(@"D:\QC\{{yesterday}}.mp4")]
    [InlineData(@"D:\QC\{{tomorrow+2}}.mp4")]
    public void RuntimeOnlyLinesAreRecognised(string line) => Assert.True(FlowDesigner.IsRuntimeOnlyMediaLine(line));

    [Theory]
    [InlineData("{{video1}}")]
    [InlineData(@"D:\QC\{{thuMuc}}\a.mp4")]
    [InlineData(@"{{env:USERPROFILE}}\Videos\a.mp4")]
    [InlineData(@"D:\QC\{{ten:upper}}.mp4")]
    [InlineData(@"D:\QC\a.mp4")]
    public void FixedLinesAreNotRuntimeOnly(string line) => Assert.False(FlowDesigner.IsRuntimeOnlyMediaLine(line));

    [Fact]
    public void RuntimeOnlyLinesAreNeverExpandedAndShowTheNormalIcon()
    {
        Sta(() =>
        {
            int calls = 0;
            var steps = new List<ActionStep>
            {
                Media("{{clipboard}}"),
                Media(@"D:\QC\clip{{random:1-5}}.mp4"),
                Media("{{=chon(a.mp4; b.mp4)}}"),
                Media(@"D:\QC\{{guid}}.mp4"),
                Media("{{secret:matkhau}}"),
                Media(@"D:\QC\{{now:HHmmss}}.mp4")
            };
            using var d = new FlowDesigner { Size = new Size(1200, 600) };
            d.ExpandMediaLine = line => { calls++; return line; };
            d.SetSteps(steps);
            for (int round = 0; round < 5; round++)
                foreach (var s in steps)
                {
                    Assert.Null(d.FirstMediaLine(s));
                    Assert.Null(d.MediaThumbnail(s));
                }
            Assert.Equal(0, calls);
            Assert.Equal(0, MediaThumbCount(d));   // không có lượt tải ảnh nào
        });
    }

    [Fact]
    public void ExpandedLineIsCachedUntilTheLineOrTheVariablesChange()
    {
        Sta(() =>
        {
            int calls = 0;
            var dir = NewDir();
            var step = Media("{{video1}}");
            using var d = new FlowDesigner { Size = new Size(1200, 600) };
            var first = MediaInfo.ExpanderFor([new VariableDef { Name = "video1", Value = Path.Combine(dir, "a.mp4") }]);
            d.ExpandMediaLine = line => { calls++; return first(line); };
            d.SetSteps([step]);

            for (int i = 0; i < 10; i++) Assert.Equal(Path.Combine(dir, "a.mp4"), d.FirstMediaLine(step));
            Assert.Equal(1, calls);

            // Sửa danh sách phát → thay lại một lần.
            step.Text = @"{{video1}}" + "\n" + @"D:\khac.mp4";
            Assert.Equal(Path.Combine(dir, "a.mp4"), d.FirstMediaLine(step));
            step.Text = "# ghi chú\n{{video1}}";
            Assert.Equal(Path.Combine(dir, "a.mp4"), d.FirstMediaLine(step));
            Assert.Equal(1, calls);                // dòng đầu (đã bỏ ghi chú) không đổi → không thay lại
            step.Text = @"{{video1}}\b";
            Assert.Equal(Path.Combine(dir, "a.mp4") + @"\b", d.FirstMediaLine(step));
            Assert.Equal(2, calls);

            // Bảng Biến đổi → hàm thay mới → dòng tính lại theo giá trị mới.
            d.ExpandMediaLine = MediaInfo.ExpanderFor([new VariableDef { Name = "video1", Value = Path.Combine(dir, "b.mp4") }]);
            Assert.Equal(Path.Combine(dir, "b.mp4") + @"\b", d.FirstMediaLine(step));
        });
    }

    [Fact]
    public void ExpanderErrorsDoNotBreakPainting()
    {
        Sta(() =>
        {
            var steps = new List<ActionStep> { Media("{{a}}"), Media("{{b}}"), Media("{{c}}"), Media("{{chuaCo}}"), S(StepType.Wait) };
            using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000), Size = new Size(1200, 500) };
            var d = new FlowDesigner { Dock = DockStyle.Fill };
            int calls = 0;
            d.ExpandMediaLine = line =>
            {
                calls++;
                return line switch
                {
                    "{{a}}" => throw new FormatException("định dạng sai"),
                    "{{b}}" => throw new OverflowException("số quá lớn"),
                    "{{c}}" => throw new System.ComponentModel.Win32Exception(5),
                    _ => MediaInfo.ExpanderFor([])(line)
                };
            };
            form.Controls.Add(d);
            form.Show();
            d.SetSteps(steps);
            using var bmp = new Bitmap(d.ClientSize.Width, d.ClientSize.Height);
            for (int i = 0; i < 3; i++)
            {
                d.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size));
                Application.DoEvents();
            }
            Assert.All(steps.Take(4), s => Assert.Null(d.FirstMediaLine(s)));
            Assert.Equal(4, calls);                // mỗi dòng thay đúng một lần dù vẽ nhiều lần
            form.Close();
        });
    }
}
