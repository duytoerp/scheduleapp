using System.Drawing;
using System.Windows.Forms;
using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Recording;

namespace ScheduleApp.Tests;

/// <summary>Trình ghi macro và phát lại click trong popup — dùng form đặt ngoài màn hình, không đụng chuột/bàn phím thật.</summary>
public class RecorderTests
{
    private static Win32.KBDLLHOOKSTRUCT Key(int vk) => new() { vkCode = (uint)vk };

    [Fact]
    public void LoneWinKeyIsRecorded()
    {
        using var recorder = new MacroRecorder();
        recorder.OnKey(Key(0x5B));
        recorder.OnKey(Key(0x5B)); // lặp phím khi giữ
        recorder.OnKeyUp(Key(0x5B));
        var steps = recorder.Stop();
        var step = Assert.Single(steps);
        Assert.Equal(StepType.KeyPress, step.Type);
        Assert.Equal("Win", step.Text);
        Assert.Equal("", step.Target);
        InputSimulator.Validate(step.Text);
    }

    [Fact]
    public void WinComboDoesNotAddLoneWin()
    {
        using var recorder = new MacroRecorder();
        recorder.OnKey(Key(0x5B));
        recorder.OnKey(Key('E'));
        recorder.OnKey(Key(0x5B)); // lặp phím sau khi đã bấm E
        recorder.OnKeyUp(Key(0x5B));
        Assert.DoesNotContain(recorder.Stop(), s => s is { Type: StepType.KeyPress, Text: "Win" });
    }

    [Fact]
    public void RecordedElementClickFallsBackToPoint()
    {
        var step = new ActionStep { Type = StepType.ClickElement, Target = "exe:msedge", Text = "Name=Lưu; ControlType=Button", X = 120, Y = 48 };
        Assert.True(step.HasRecordedPoint);
        Assert.Contains("dự phòng (120, 48)", step.Describe());
        Assert.False(new ActionStep { Type = StepType.ClickElement, Target = "exe:msedge", Text = "Name=Lưu" }.HasRecordedPoint);
        Assert.False(new ActionStep { Type = StepType.ClickElement, Text = "Name=Lưu", X = 5, Y = 5 }.HasRecordedPoint);
    }

    [LiveFact]
    public void PopupOfAppIsSearchedAndRecordedAgainstMainWindow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunPopup(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void RunPopup()
    {
        var offscreen = new Point(SystemInformation.VirtualScreen.Right + 300, SystemInformation.VirtualScreen.Top);
        var main = new Form
        {
            Text = "Cửa sổ chính thử", StartPosition = FormStartPosition.Manual, Location = offscreen,
            Size = new Size(400, 300), ShowInTaskbar = false
        };
        main.Controls.Add(new TextBox { Name = "txtDiaChi", Location = new Point(10, 10), Width = 300 });
        main.Show();
        // Giống danh sách gợi ý của thanh địa chỉ: cửa sổ riêng, không viền, thuộc về cửa sổ chính.
        var popup = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Location = offscreen + new Size(10, 40), Size = new Size(300, 120), ShowInTaskbar = false
        };
        popup.Controls.Add(new Button { Name = "btnGoiY", Text = "Gợi ý A", Location = new Point(5, 5), Width = 200 });
        popup.Show(main);
        Application.DoEvents();

        T Bg<T>(Func<T> f)
        {
            var t = Task.Run(f);
            while (!t.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
            return t.GetAwaiter().GetResult();
        }

        try
        {
            Assert.True(WindowHelper.IsPopup(popup.Handle));
            Assert.False(WindowHelper.IsPopup(main.Handle));
            Assert.Contains(popup.Handle, WindowHelper.ProcessPopups(main.Handle));

            // Chrome/Edge không đưa popup (gợi ý thanh địa chỉ) vào cây UIA của cửa sổ chính → phải tìm riêng trong popup.
            Assert.NotNull(Bg(() => UiElementFinder.Find(popup.Handle, "AutomationId=btnGoiY")));
            Assert.NotNull(Bg(() => UiElementFinder.FindWithPopups(main.Handle, "AutomationId=btnGoiY")));
            Assert.NotNull(Bg(() => UiElementFinder.FindWithPopups(main.Handle, "Name=Gợi ý A; ControlType=Button")));
            Assert.NotNull(Bg(() => UiElementFinder.FindWithPopups(main.Handle, "AutomationId=txtDiaChi")));
            Assert.Null(Bg(() => UiElementFinder.FindWithPopups(main.Handle, "AutomationId=khongCo")));

            // Click trong popup được ghi theo cửa sổ chính (popup biến mất sau khi click, lần sau mở ở vị trí khác).
            using var recorder = new MacroRecorder();
            var (window, target) = recorder.ResolveTarget(popup.Handle);
            Assert.Equal(main.Handle, window);
            Assert.StartsWith("exe:", target);
            Assert.Equal((main.Handle, target), recorder.ResolveTarget(main.Handle));

            // Taskbar → tọa độ màn hình.
            var tray = FindByClass("Shell_TrayWnd");
            if (tray != IntPtr.Zero) Assert.Equal((IntPtr.Zero, ""), recorder.ResolveTarget(tray));
        }
        finally
        {
            popup.Close();
            main.Close();
            popup.Dispose();
            main.Dispose();
        }
    }

    private static IntPtr FindByClass(string className)
    {
        IntPtr found = IntPtr.Zero;
        Win32.EnumWindows((h, _) =>
        {
            if (WindowHelper.GetClassName(h) != className) return true;
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
