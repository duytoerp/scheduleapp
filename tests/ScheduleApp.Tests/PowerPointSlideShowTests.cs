using ScheduleApp.Models;
using ScheduleApp.Native;
using static ScheduleApp.Tests.TestSupport;

namespace ScheduleApp.Tests;

/// <summary>
/// Đang trình chiếu PowerPoint → "Thu nhỏ cửa sổ" (cửa sổ đang dùng) phải ẩn đúng cửa sổ trình chiếu, và "Kích hoạt cửa sổ"
/// mở lại vẫn đang trình chiếu, đúng slide cũ, toàn màn hình. Chiếm màn hình vài giây nên chỉ chạy khi đặt SCHEDULEAPP_POWERPOINT_TEST=1.
/// </summary>
public class PowerPointSlideShowTests
{
    private const int MsoTrue = -1, PpLayoutBlank = 12, PpSlideShowRunning = 1;

    [PowerPointFact]
    public async Task SlideShowIsHiddenAndComesBackStillPresentingTheSameSlide()
    {
        dynamic app = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
        dynamic? pres = null;
        try
        {
            pres = app.Presentations.Add(MsoTrue);
            for (int i = 1; i <= 3; i++) pres.Slides.Add(i, PpLayoutBlank);
            dynamic show = pres.SlideShowSettings.Run();
            show.View.GotoSlide(2);
            // Cửa sổ trình chiếu: lớp "screenClass", không có thanh tiêu đề (popup).
            var slideShow = IntPtr.Zero;
            for (int i = 0; i < 40 && slideShow == IntPtr.Zero; i++)
            {
                await Task.Delay(250);
                slideShow = WindowHelper.GetOpenWindows()
                    .FirstOrDefault(w => w.ProcessName.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase) && WindowHelper.GetClassName(w.Handle) == "screenClass")
                    ?.Handle ?? IntPtr.Zero;
            }
            Assert.NotEqual(IntPtr.Zero, slideShow);
            Assert.True(WindowHelper.IsPopup(slideShow));
            await Task.Delay(1000);
            WindowHelper.Focus(slideShow);
            Assert.Equal(slideShow, WindowHelper.ActiveUserWindow());

            var (r, ctx) = await RunAsync(new Job { Steps = [S(StepType.MinimizeWindow, s => s.Variable = "ungDungTruoc")] });
            Assert.True(r.Ok, r.Message);
            Assert.Equal(WindowHelper.HandleRef(slideShow), ctx.Vars["ungDungTruoc"]);
            Assert.True(Win32.IsIconic(slideShow));
            Assert.Equal(1, (int)app.SlideShowWindows.Count);   // vẫn đang trình chiếu, chỉ bị ẩn

            await Task.Delay(1000);
            (r, _) = await RunAsync(new Job
            {
                Variables = [new VariableDef { Name = "ungDungTruoc", Value = ctx.Vars["ungDungTruoc"] }],
                Steps = [S(StepType.FocusWindow, s => { s.Target = "{{ungDungTruoc}}"; s.DelayMs = 5000; })]
            });
            Assert.True(r.Ok, r.Message);
            await Task.Delay(500);

            Assert.False(Win32.IsIconic(slideShow));
            Assert.Equal(slideShow, Win32.GetForegroundWindow());
            Assert.Equal(1, (int)app.SlideShowWindows.Count);
            Assert.Equal(PpSlideShowRunning, (int)show.View.State);
            Assert.Equal(2, (int)show.View.CurrentShowPosition);
            var screen = Screen.FromHandle(slideShow).Bounds;
            Assert.Equal(screen, WindowHelper.GetRect(slideShow));   // vẫn toàn màn hình

            show.View.Exit();
        }
        finally
        {
            if (pres != null)
            {
                pres.Saved = MsoTrue;
                pres.Close();
            }
            if ((int)app.Presentations.Count == 0) app.Quit();
        }
    }
}

public sealed class PowerPointFactAttribute : FactAttribute
{
    public PowerPointFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCHEDULEAPP_POWERPOINT_TEST") != "1")
            Skip = "Cần PowerPoint và màn hình trống vài giây — đặt SCHEDULEAPP_POWERPOINT_TEST=1.";
        else if (Type.GetTypeFromProgID("PowerPoint.Application") == null)
            Skip = "Máy chưa cài PowerPoint.";
    }
}
