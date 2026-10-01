using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services.Data;
using ScheduleApp.Services.Engine;
using ScheduleApp.Vision;

namespace ScheduleApp.Services;

/// <summary>Thực thi từng loại bước thao tác trong flow (các bước điều khiển luồng do <see cref="FlowEngine"/> xử lý).</summary>
public static class StepExecutor
{
    private const int FindWindowTimeoutMs = 10_000;

    /// <param name="s">Bước đã được thay {{biến}}.</param>
    /// <param name="job">Công việc chứa bước (flow con dùng tên của chính nó).</param>
    public static async Task ExecuteAsync(ActionStep s, Job job, FlowContext ctx)
    {
        var ct = ctx.Ct;
        switch (s.Type)
        {
            case StepType.LaunchApp:
                Launch(s);
                break;

            case StepType.Reminder:
                await ctx.Ui.ShowReminderAsync(string.IsNullOrWhiteSpace(s.Target) ? job.Name : s.Target, s.Text, s.WaitForUser, ct);
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
                var (x, y) = await ToScreenAsync(s, s.X, s.Y, ct);
                InputSimulator.Click(x, y, s.Button, s.DoubleClick);
                break;
            }

            case StepType.MouseScroll:
            {
                if (!string.IsNullOrWhiteSpace(s.Target) || s.X != 0 || s.Y != 0)
                {
                    int x = s.X, y = s.Y;
                    if (!string.IsNullOrWhiteSpace(s.Target) && x == 0 && y == 0)
                    {
                        // Không nhập tọa độ: cuộn ở giữa cửa sổ.
                        var h = await WindowHelper.WaitForAsync(s.Target, FindWindowTimeoutMs, ct);
                        WindowHelper.FocusKeepingPopups(h);
                        var r = WindowHelper.GetRect(h);
                        InputSimulator.MoveTo(r.Left + r.Width / 2, r.Top + r.Height / 2);
                    }
                    else
                    {
                        (x, y) = await ToScreenAsync(s, x, y, ct);
                        InputSimulator.MoveTo(x, y);
                    }
                    await Task.Delay(80, ct);
                }
                InputSimulator.Scroll(s.Count);
                break;
            }

            case StepType.MouseDrag:
            {
                var (x1, y1) = await ToScreenAsync(s, s.X, s.Y, ct);
                var (x2, y2) = await ToScreenAsync(s, s.X2, s.Y2, ct, focus: false);
                InputSimulator.Drag(x1, y1, x2, y2, s.Button, ct);
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
            {
                var output = await RunCommandAsync(s.Target, s.DelayMs, logOutput: true, ct);
                ctx.Vars["lastOutput"] = output;
                if (!string.IsNullOrWhiteSpace(s.Variable)) ctx.SetVar(s.Variable, output);
                break;
            }

            case StepType.CloseApp:
                await CloseAppAsync(s, ct);
                break;

            case StepType.ClickImage or StepType.WaitForImage or StepType.ClickText or StepType.WaitForText:
                await LocateOnScreenAsync(s, ct);
                break;

            case StepType.SetVariable:
                await SetVariableAsync(s, job, ctx);
                break;

            case StepType.LogMessage:
                Log.Info("   📝 " + s.Text);
                break;

            case StepType.ClickElement:
                await ClickElementAsync(s, ct);
                break;

            case StepType.SetElementText:
            {
                if (!string.IsNullOrWhiteSpace(s.Target)) WindowHelper.FocusKeepingPopups(await WindowHelper.WaitForAsync(s.Target, FindWindowTimeoutMs, ct));
                var e = await UiElementFinder.WaitAsync(s.Target, s.Text, s.DelayMs, ct);
                UiElementFinder.LogFound(e);
                await UiElementFinder.SetTextAsync(e, s.Arguments, ct);
                break;
            }

            case StepType.WaitForElement:
                UiElementFinder.LogFound(await UiElementFinder.WaitAsync(s.Target, s.Text, s.DelayMs, ct));
                break;

            case StepType.Browser:
                await BrowserClient.ExecuteAsync(s, (name, value) =>
                {
                    ctx.SetVar(name, value);
                    return Task.CompletedTask;
                }, ct);
                break;

            case StepType.Dynamics:
                await D365Client.ExecuteAsync(s, ctx);
                break;

            case StepType.Assert:
                await ConditionEvaluator.AssertAsync(s, ctx);
                break;

            case StepType.WriteData:
                WriteData(s, ctx);
                break;

            case StepType.HttpRequest:
                await HttpRequestAsync(s, ctx);
                break;

            case StepType.AskAi:
                await AskAiAsync(s, ctx);
                break;

            case StepType.Notify:
            {
                string? shot = null;
                if (s.Force) shot = ErrorScreenshots.CaptureAlways(job.Name, "thong-bao");
                var title = string.IsNullOrWhiteSpace(s.Target) ? $"🔔 {job.Name}" : s.Target;
                if (!NotificationService.AnyChannelEnabled)
                    throw new InvalidOperationException("Chưa bật kênh thông báo nào (⚙ Cài đặt → Thông báo).");
                var errors = await NotificationService.SendAsync(title, s.Text, shot);
                if (errors.Count > 0) throw new InvalidOperationException("Gửi thông báo lỗi: " + string.Join("; ", errors));
                Log.Info("      Đã gửi thông báo.");
                break;
            }

            default:
                throw new InvalidOperationException($"Bước \"{ActionStep.TypeNames[s.Type]}\" không chạy được ở đây.");
        }
    }

    // ───────────────────────────── Ghi Excel / CSV ─────────────────────────────

    private static void WriteData(ActionStep s, FlowContext ctx)
    {
        var path = Environment.ExpandEnvironmentVariables(s.Target.Trim().Trim('"'));
        if (path.Length == 0) throw new InvalidOperationException("Chưa nhập file Excel / CSV.");
        // Text chưa thay biến (xem FlowContext.ExpandStep) — thay riêng từng ô để giá trị có xuống dòng vẫn đúng.
        var values = TabularWriter.ParseAssignments(s.Text, ctx.Expand);
        int row = TabularWriter.Write(path, s.Arguments, s.DataAction, s.RowRef, values);
        ctx.Vars["lastRow"] = row.ToString(CultureInfo.InvariantCulture);
        Log.Info($"      Đã ghi dòng {row} của \"{Path.GetFileName(path)}\": {Truncate(string.Join(", ", values.Select(v => $"{v.Key}={Log.Redact(v.Value)}")))}");
    }

    // ───────────────────────────── Gọi API ─────────────────────────────

    private static async Task HttpRequestAsync(ActionStep s, FlowContext ctx)
    {
        var conn = ApiClient.FindConnection(s.Connection);
        var method = string.IsNullOrWhiteSpace(s.Method) ? "GET" : s.Method;
        var r = await ApiClient.SendAsync(method, s.Target, conn, s.Headers, s.Text, s.DelayMs, ctx.Ct);
        ctx.Vars["http.status"] = r.Status.ToString(CultureInfo.InvariantCulture);
        ctx.Vars["http.body"] = r.Body;
        Log.Info($"      {method.ToUpperInvariant()} {r.Url} → {r.Status}");
        if (!r.IsSuccess && !s.Force)
            throw new InvalidOperationException($"API trả về {r.Status}: {ApiClient.Short(ExtractApiError(r.Body))}");

        if (string.IsNullOrWhiteSpace(s.Variable)) return;
        var value = string.IsNullOrWhiteSpace(s.Arguments) || !r.IsSuccess ? r.Body : JsonPath.Select(r.Body, s.Arguments);
        ctx.SetVar(s.Variable, value);
        Log.Info($"      {{{{{s.Variable.Trim()}}}}} = \"{Truncate(Log.Redact(value)).Replace("\r", "").Replace("\n", " ⏎ ")}\"");
    }

    /// <summary>Thông điệp lỗi trong JSON (Dynamics 365 / OData: error.message) hoặc nguyên nội dung.</summary>
    private static string ExtractApiError(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == System.Text.Json.JsonValueKind.String) return e.GetString() ?? body;
                if (e.TryGetProperty("message", out var m)) return m.GetString() ?? body;
            }
            if (root.TryGetProperty("message", out var msg)) return msg.GetString() ?? body;
        }
        catch (System.Text.Json.JsonException) { }
        return body;
    }

    // ───────────────────────────── Hỏi AI ─────────────────────────────

    private static async Task AskAiAsync(ActionStep s, FlowContext ctx)
    {
        byte[]? image = null;
        if (s.Force)
        {
            var window = IntPtr.Zero;
            if (!string.IsNullOrWhiteSpace(s.Target))
            {
                window = await WindowHelper.WaitForAsync(s.Target, FindWindowTimeoutMs, ctx.Ct);
                WindowHelper.FocusKeepingPopups(window);
                await Task.Delay(300, ctx.Ct);
            }
            using var shot = ScreenCapture.Capture(ScreenLocator.AreaOf(window));
            using var ms = new MemoryStream();
            shot.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            image = ms.ToArray();
        }
        Log.Info("      Đang hỏi AI…");
        var answer = await AiClient.AskAsync(s.Text, image, s.DelayMs, ctx.Ct);
        ctx.Vars["ai.answer"] = answer;
        if (!string.IsNullOrWhiteSpace(s.Variable)) ctx.SetVar(s.Variable, answer);
        Log.Info($"      AI trả lời: \"{Truncate(answer).Replace("\r", "").Replace("\n", " ⏎ ")}\"");
    }

    // ───────────────────────────── Gán biến ─────────────────────────────

    private static async Task SetVariableAsync(ActionStep s, Job job, FlowContext ctx)
    {
        var ct = ctx.Ct;
        if (string.IsNullOrWhiteSpace(s.Variable)) throw new InvalidOperationException("Chưa nhập tên biến.");

        string value;
        switch (s.VarSource)
        {
            case VarSource.Value:
                value = s.Text;
                break;
            case VarSource.Calc:
                value = Calculate(s.Text);
                break;
            case VarSource.Clipboard:
                value = ClipboardHelper.TryGetText() ?? "";
                break;
            case VarSource.Command:
                value = await RunCommandAsync(s.Target, Math.Max(1000, s.DelayMs), logOutput: false, ct);
                break;
            case VarSource.ScreenText:
            {
                var window = IntPtr.Zero;
                if (!string.IsNullOrWhiteSpace(s.Target))
                {
                    window = await WindowHelper.WaitForAsync(s.Target, FindWindowTimeoutMs, ct);
                    WindowHelper.FocusKeepingPopups(window);
                    await Task.Delay(200, ct);
                }
                using var shot = ScreenCapture.Capture(ScreenLocator.AreaOf(window));
                value = await ScreenOcr.ReadTextAsync(shot);
                break;
            }
            case VarSource.Element:
            {
                var e = await UiElementFinder.WaitAsync(s.Target, s.Text, Math.Max(1000, s.DelayMs), ct);
                value = UiElementFinder.GetValue(e);
                break;
            }
            case VarSource.AskUser:
            {
                var title = string.IsNullOrWhiteSpace(s.Target) ? job.Name : s.Target;
                value = await ctx.Ui.PromptAsync(title, s.Text, s.Arguments, s.Force, ct)
                        ?? throw new OperationCanceledException("Người dùng đã hủy nhập giá trị.");
                if (s.Force) Log.Mask(value);
                break;
            }
            case VarSource.File:
            {
                var path = Environment.ExpandEnvironmentVariables(s.Target.Trim().Trim('"'));
                if (!File.Exists(path)) throw new FileNotFoundException($"Không tìm thấy file \"{path}\".");
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8, true);
                value = await reader.ReadToEndAsync(ct);
                break;
            }
            case VarSource.ListAdd:
            {
                // Danh sách = mỗi phần tử một dòng; dùng với "Lặp: mỗi dòng văn bản" và {{ds:count}}, {{ds:join(, )}}…
                var current = ctx.Vars.GetValueOrDefault(s.Variable.Trim()) ?? "";
                value = current.Length == 0 ? s.Text : current.TrimEnd('\r', '\n') + "\n" + s.Text;
                break;
            }
            case VarSource.Split:
            {
                var sep = s.Arguments.Length == 0 ? "," : s.Arguments.Replace("\\n", "\n").Replace("\\t", "\t");
                value = string.Join("\n", s.Text.Split(sep, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                break;
            }
            case VarSource.JsonPath:
                value = JsonPath.Select(s.Text, s.Arguments);
                break;
            default:
                value = "";
                break;
        }

        // Trích một phần bằng regex (nhóm 1 nếu có, nếu không lấy cả đoạn khớp).
        bool extract = s.UsesRegex && !string.IsNullOrWhiteSpace(s.Arguments);
        if (extract)
        {
            var m = Regex.Match(value, s.Arguments, RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(2));
            if (!m.Success) throw new InvalidOperationException($"Regex \"{s.Arguments}\" không khớp với nội dung đọc được: \"{Truncate(value)}\".");
            value = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
        }

        ctx.SetVar(s.Variable, value.TrimEnd('\r', '\n'));
        Log.Info($"      {{{{{s.Variable.Trim()}}}}} = \"{Truncate(value).Replace("\r", "").Replace("\n", " ⏎ ")}\"");
    }

    /// <summary>Tính biểu thức số học (+ - * / %, ngoặc, so sánh) — vd "{{dem}} + 1" sau khi đã thay biến.</summary>
    public static string Calculate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) throw new InvalidOperationException("Chưa nhập phép tính.");
        object? result;
        try
        {
            using var table = new DataTable { Locale = CultureInfo.InvariantCulture };
            result = table.Compute(expression, null);
        }
        catch (Exception ex) when (ex is EvaluateException or SyntaxErrorException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException($"Phép tính \"{expression}\" không hợp lệ: {ex.Message}");
        }
        return result switch
        {
            null or DBNull => "",
            double d => Math.Round(d, 10).ToString("G15", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => result.ToString() ?? ""
        };
    }

    // ───────────────────────────── Chuột / màn hình ─────────────────────────────

    /// <summary>Đổi tọa độ của bước sang tọa độ màn hình (cộng góc cửa sổ khi có cửa sổ đích).</summary>
    private static async Task<(int X, int Y)> ToScreenAsync(ActionStep s, int x, int y, CancellationToken ct, bool focus = true)
    {
        if (string.IsNullOrWhiteSpace(s.Target)) return (x, y);
        var h = await WindowHelper.WaitForAsync(s.Target, FindWindowTimeoutMs, ct);
        if (focus) WindowHelper.FocusKeepingPopups(h);
        var rect = WindowHelper.GetRect(h);
        return (x + rect.Left, y + rect.Top);
    }

    /// <summary>
    /// Click phần tử UI. Bước do trình ghi macro tạo còn giữ tọa độ lúc ghi (so với cửa sổ đích):
    /// không tìm thấy phần tử (tên đổi, phần tử không hỗ trợ UI Automation…) thì click theo tọa độ đó thay vì dừng flow.
    /// </summary>
    private static async Task ClickElementAsync(ActionStep s, CancellationToken ct)
    {
        bool hasTarget = !string.IsNullOrWhiteSpace(s.Target);
        if (hasTarget) WindowHelper.FocusKeepingPopups(await WindowHelper.WaitForAsync(s.Target, FindWindowTimeoutMs, ct));
        System.Windows.Automation.AutomationElement e;
        try
        {
            e = await UiElementFinder.WaitAsync(s.Target, s.Text, s.DelayMs, ct);
        }
        catch (TimeoutException ex) when (hasTarget && s.HasRecordedPoint)
        {
            var (x, y) = await ToScreenAsync(s, s.X, s.Y, ct);
            Log.Warn($"      {ex.Message} → click theo tọa độ lúc ghi ({s.X}, {s.Y}).");
            InputSimulator.Click(x, y, s.Button, s.DoubleClick);
            return;
        }
        UiElementFinder.LogFound(e);
        await UiElementFinder.ClickAsync(e, s.Button, s.DoubleClick, ct);
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
            WindowHelper.FocusKeepingPopups(await WindowHelper.WaitForAsync(s.Target, FindWindowTimeoutMs, ct));
            return;
        }
        // Không có cửa sổ đích: tránh gõ nhầm vào chính ScheduleApp.
        if (WindowHelper.BelongsToThisApp(Win32.GetForegroundWindow()))
            throw new InvalidOperationException(
                "Cửa sổ đang được chọn là ScheduleApp. Hãy nhập \"Cửa sổ đích\" hoặc thêm bước \"Kích hoạt cửa sổ\" trước bước này.");
    }

    // ───────────────────────────── Lệnh / tiến trình ─────────────────────────────

    /// <summary>Chạy lệnh cmd ẩn; trả về output (timeout 0 = không chờ, trả chuỗi rỗng).</summary>
    private static async Task<string> RunCommandAsync(string command, int timeoutMs, bool logOutput, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("Chưa nhập lệnh.");
        bool wait = timeoutMs > 0;
        // cmd.exe chỉ đọc code page lúc khởi động, nên lệnh phải chạy trong một cmd con khởi động SAU chcp 65001
        // thì output tiếng Việt mới đúng UTF-8.
        var psi = new ProcessStartInfo("cmd.exe", $"/d /c chcp 65001>nul & cmd /d /s /c \"{command}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = wait,
            RedirectStandardError = wait,
            StandardOutputEncoding = wait ? Encoding.UTF8 : null,
            StandardErrorEncoding = wait ? Encoding.UTF8 : null
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Không khởi chạy được cmd.exe.");
        if (!wait) return "";

        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"Lệnh chưa chạy xong sau {ActionStep.FormatMs(timeoutMs)}.");
        }

        var output = (await stdout).Trim();
        var error = (await stderr).Trim();
        if (logOutput && output.Length > 0) Log.Info("      " + Truncate(output).Replace("\r", "").Replace("\n", "\n      "));
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Lệnh trả về mã {proc.ExitCode}. {Truncate(error)}");
        return output;
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
