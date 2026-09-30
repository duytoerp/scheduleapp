using System.Diagnostics;
using System.Text;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Vision;

namespace ScheduleApp.Services;

/// <summary>Thực thi từng loại bước trong flow.</summary>
public static class StepExecutor
{
    private const int FindWindowTimeoutMs = 10_000;

    public static async Task ExecuteAsync(ActionStep s, Job job, IUserNotifier ui, CancellationToken ct)
    {
        switch (s.Type)
        {
            case StepType.LaunchApp:
                Launch(s);
                break;

            case StepType.Reminder:
                await ui.ShowReminderAsync(string.IsNullOrWhiteSpace(s.Target) ? job.Name : s.Target, s.Text, s.WaitForUser, ct);
                break;

            case StepType.Wait:
                await Task.Delay(Math.Max(0, s.DelayMs), ct);
                break;

            case StepType.WaitForWindow:
                await WindowHelper.WaitForAsync(s.Target, s.DelayMs, ct);
                break;

            case StepType.FocusWindow:
                WindowHelper.Focus(await WindowHelper.WaitForAsync(s.Target, s.DelayMs, ct));
                break;

            case StepType.MouseClick:
            {
                int x = s.X, y = s.Y;
                if (!string.IsNullOrWhiteSpace(s.Target))
                {
                    var h = await WindowHelper.WaitForAsync(s.Target, FindWindowTimeoutMs, ct);
                    WindowHelper.Focus(h);
                    var rect = WindowHelper.GetRect(h);
                    x += rect.Left;
                    y += rect.Top;
                }
                InputSimulator.Click(x, y, s.Button, s.DoubleClick);
                break;
            }

            case StepType.TypeText:
                await FocusTargetAsync(s, ct);
                await TypeTextAsync(s, ct);
                break;

            case StepType.KeyPress:
                await FocusTargetAsync(s, ct);
                InputSimulator.SendKeys(s.Text, ct);
                break;

            case StepType.RunCommand:
                await RunCommandAsync(s, ct);
                break;

            case StepType.CloseApp:
                await CloseAppAsync(s, ct);
                break;

            case StepType.ClickImage or StepType.WaitForImage or StepType.ClickText or StepType.WaitForText:
                await LocateOnScreenAsync(s, ct);
                break;
        }
    }

    private static async Task LocateOnScreenAsync(ActionStep s, CancellationToken ct)
    {
        var result = await ScreenLocator.WaitAsync(s, ct);
        if (!result.Found)
        {
            var what = s.IsImageStep ? "hình mẫu" : $"chữ \"{s.Text}\"";
            throw new TimeoutException($"Không tìm thấy {what} sau {ActionStep.FormatMs(s.DelayMs)} — {result.Detail}.");
        }
        Log.Info($"      Tìm thấy tại ({result.Bounds.X}, {result.Bounds.Y}) — {result.Detail}");
        if (s.Type is StepType.ClickImage or StepType.ClickText)
            InputSimulator.Click(result.ClickPoint.X, result.ClickPoint.Y, s.Button, s.DoubleClick);
    }

    private static void Launch(ActionStep s)
    {
        if (string.IsNullOrWhiteSpace(s.Target)) throw new InvalidOperationException("Chưa nhập ứng dụng cần mở.");
        var file = Environment.ExpandEnvironmentVariables(s.Target.Trim().Trim('"'));
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = Environment.ExpandEnvironmentVariables(s.Arguments ?? ""),
            UseShellExecute = true
        };
        if (File.Exists(file)) psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        Process.Start(psi);
    }

    /// <summary>
    /// Gõ từng phím, hoặc dán qua clipboard khi có bộ gõ tiếng Việt (UniKey/EVKey… xử lý lại cả phím do
    /// ứng dụng gửi vào theo kiểu Telex/VNI nên làm mất/đổi chữ, vd "gõ" thành "õ").
    /// </summary>
    private static async Task TypeTextAsync(ActionStep s, CancellationToken ct)
    {
        string? ime = s.TypeMode == TypeMode.Auto ? ClipboardHelper.RunningVietnameseIme() : null;
        if (s.TypeMode == TypeMode.Keys || (s.TypeMode == TypeMode.Auto && ime == null))
        {
            InputSimulator.TypeText(s.Text, ct);
            return;
        }

        Log.Info(ime != null ? $"      Dán qua clipboard (phát hiện bộ gõ {ime})." : "      Dán qua clipboard.");
        var previous = ClipboardHelper.TryGetText();
        ClipboardHelper.SetText(s.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        InputSimulator.SendKeys("Ctrl+V", ct);
        await Task.Delay(300, ct); // ứng dụng đích đọc clipboard bất đồng bộ
        if (previous != null) ClipboardHelper.SetText(previous);
    }

    private static async Task FocusTargetAsync(ActionStep s, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(s.Target))
        {
            WindowHelper.Focus(await WindowHelper.WaitForAsync(s.Target, FindWindowTimeoutMs, ct));
            return;
        }
        // Không có cửa sổ đích: tránh gõ nhầm vào chính ScheduleApp.
        if (WindowHelper.BelongsToThisApp(Win32.GetForegroundWindow()))
            throw new InvalidOperationException(
                "Cửa sổ đang được chọn là ScheduleApp. Hãy nhập \"Cửa sổ đích\" hoặc thêm bước \"Kích hoạt cửa sổ\" trước bước này.");
    }

    private static async Task RunCommandAsync(ActionStep s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.Target)) throw new InvalidOperationException("Chưa nhập lệnh.");
        bool wait = s.DelayMs > 0;
        // cmd.exe chỉ đọc code page lúc khởi động, nên lệnh phải chạy trong một cmd con khởi động SAU chcp 65001
        // thì output tiếng Việt mới đúng UTF-8.
        var psi = new ProcessStartInfo("cmd.exe", $"/d /c chcp 65001>nul & cmd /d /s /c \"{s.Target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = wait,
            RedirectStandardError = wait,
            StandardOutputEncoding = wait ? Encoding.UTF8 : null,
            StandardErrorEncoding = wait ? Encoding.UTF8 : null
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Không khởi chạy được cmd.exe.");
        if (!wait) return;

        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(s.DelayMs);
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Lệnh chưa chạy xong sau {ActionStep.FormatMs(s.DelayMs)}.");
        }

        var output = (await stdout).Trim();
        var error = (await stderr).Trim();
        if (output.Length > 0) Log.Info("      " + Truncate(output).Replace("\r", "").Replace("\n", "\n      "));
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Lệnh trả về mã {proc.ExitCode}. {Truncate(error)}");
    }

    private static async Task CloseAppAsync(ActionStep s, CancellationToken ct)
    {
        var name = s.Target.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0) throw new InvalidOperationException("Chưa nhập tên tiến trình.");

        var procs = Process.GetProcessesByName(name);
        if (procs.Length == 0)
        {
            Log.Info($"      Không có tiến trình \"{name}\" nào đang chạy.");
            return;
        }

        int withoutWindow = 0;
        foreach (var p in procs)
        {
            using (p)
            {
                try
                {
                    if (s.Force) { p.Kill(true); continue; }
                    // Nhiều ứng dụng (Notepad Win11, Chrome…) có tiến trình phụ không có cửa sổ — bỏ qua.
                    if (!p.CloseMainWindow())
                    {
                        withoutWindow++;
                        continue;
                    }
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(5000);
                    try { await p.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Log.Warn($"      \"{name}\" chưa đóng sau 5 giây (có thể đang hỏi lưu file).");
                    }
                }
                catch (InvalidOperationException)
                {
                    // Tiến trình đã thoát.
                }
            }
        }
        if (!s.Force && withoutWindow == procs.Length)
            Log.Warn($"      \"{name}\" không có cửa sổ nào để đóng — bật \"Buộc đóng\" để kill tiến trình.");
    }

    private static string Truncate(string s) => s.Length > 500 ? s[..500] + "…" : s;
}
