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
            {
                var title = string.IsNullOrWhiteSpace(s.Target) ? job.Name : s.Target;
                // Chờ bấm OK có thể rất lâu (vắng máy cả cuối tuần): trong lúc chờ nhường lượt chạy cho công việc khác.
                if (s.WaitForUser) await ctx.RunWithoutInputGateAsync(() => ctx.Ui.ShowReminderAsync(title, s.Text, true, ct));
                else await ctx.Ui.ShowReminderAsync(title, s.Text, false, ct);
                break;
            }

            case StepType.Wait:
                await Task.Delay(Math.Max(0, s.DelayMs), ct);
                break;

            case StepType.WaitForWindow:
                await WindowHelper.WaitForAsync(s.Target, s.DelayMs, ct);
                break;

            case StepType.FocusWindow:
                WindowHelper.Focus(await WindowHelper.WaitForAsync(s.Target, s.DelayMs, ct));
                break;

            case StepType.MinimizeWindow:
            {
                var h = string.IsNullOrWhiteSpace(s.Target) ? WindowHelper.ActiveUserWindow() : await WindowHelper.WaitForAsync(s.Target, s.DelayMs, ct);
                if (h == IntPtr.Zero)
                {
                    // Màn hình nền / mọi cửa sổ đã thu nhỏ / chỉ có ScheduleApp: không có gì để ẩn — không phải lỗi, flow chạy tiếp.
                    // Biến = "" → bước mở lại sau đó (trong "Nếu cửa sổ {{biến}} đang mở") được bỏ qua.
                    Log.Info("      Không có cửa sổ ứng dụng nào đang mở — không cần thu nhỏ.");
                    ctx.Vars["lastWindow"] = "";
                    if (!string.IsNullOrWhiteSpace(s.Variable)) ctx.SetVar(s.Variable, "");
                    break;
                }
                var title = WindowHelper.GetTitle(h);
                Log.Info($"      Thu nhỏ \"{title}\".");
                if (!await WindowHelper.MinimizeAsync(h, ct))
                    Log.Warn($"      \"{title}\" không phản hồi — có thể chưa thu nhỏ được (vẫn nhớ cửa sổ để mở lại).");
                // Nhớ đúng cửa sổ này (handle) — bước "Kích hoạt cửa sổ" với {{lastWindow}} / biến đã chọn sẽ mở lại nó.
                var handle = WindowHelper.HandleRef(h);
                ctx.Vars["lastWindow"] = handle;
                if (!string.IsNullOrWhiteSpace(s.Variable)) ctx.SetVar(s.Variable, handle);
                break;
            }

            case StepType.MouseClick:
            {
                var (x, y) = await ToScreenAsync(s, s.X, s.Y, ct);
                if (s.HasImageAnchor)
                {
                    var found = await FindAnchorAsync(s, new Point(x, y), s.DelayMs, ct);
                    if (found != null) (x, y) = (found.Value.X, found.Value.Y);
                    else Log.Warn($"      → click theo tọa độ lúc ghi ({s.X}, {s.Y}).");
                }
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

            case StepType.PlayMedia:
                await PlayMediaAsync(s, ctx);
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
                // Bí mật trong tiêu đề / nội dung bị che trước khi gửi; ảnh do bước yêu cầu rõ nên gửi ở mọi kênh hỗ trợ ảnh.
                var errors = await NotificationService.SendAsync(Log.Redact(title), Log.Redact(s.Text), shot, requested: shot != null);
                if (errors.Count > 0) throw new InvalidOperationException(Log.Redact("Gửi thông báo lỗi: " + string.Join("; ", errors)));
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
        if (s.IsTextWrite)
        {
            WriteText(s, ctx, path);
            return;
        }
        if (path.Length == 0) throw new InvalidOperationException("Chưa nhập file Excel / CSV.");
        // Text chưa thay biến (xem FlowContext.ExpandStep) — thay riêng từng ô để giá trị có xuống dòng vẫn đúng.
        var values = TabularWriter.ParseAssignments(s.Text, ctx.Expand);
        int row = TabularWriter.Write(path, s.Arguments, s.DataAction, s.RowRef, values);
        ctx.Vars["lastRow"] = row.ToString(CultureInfo.InvariantCulture);
        Log.Info($"      Đã ghi dòng {row} của \"{Path.GetFileName(path)}\": {Truncate(string.Join(", ", values.Select(v => $"{v.Key}={Log.Redact(v.Value)}")))}");
    }

    /// <summary>Ghi / thêm nội dung vào file văn bản (UTF-8, tạo thư mục nếu chưa có) — vd báo cáo để mở bằng Notepad.</summary>
    private static void WriteText(ActionStep s, FlowContext ctx, string path)
    {
        if (path.Length == 0) throw new InvalidOperationException("Chưa nhập file văn bản.");
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var content = ctx.Expand(s.Text).Replace("\r\n", "\n").Replace("\n", Environment.NewLine) + Environment.NewLine;
        var utf8 = new UTF8Encoding(false);
        if (s.DataAction == DataAction.WriteText)
        {
            File.WriteAllText(full, content, utf8);
        }
        else
        {
            // File có sẵn chưa kết thúc bằng xuống dòng → thêm xuống dòng để nội dung mới bắt đầu ở dòng riêng.
            // Giữ đúng mã hóa của file có sẵn (UTF-16 của Notepad cũ, UTF-8 có BOM…); file mới / rỗng → UTF-8.
            Encoding encoding = utf8;
            bool needBreak = false;
            if (File.Exists(full) && new FileInfo(full).Length > 0)
            {
                using var reader = new StreamReader(full, utf8, detectEncodingFromByteOrderMarks: true);
                var existing = reader.ReadToEnd();
                needBreak = !existing.EndsWith('\n');
                encoding = reader.CurrentEncoding switch
                {
                    UnicodeEncoding u => new UnicodeEncoding(u.CodePage == 1201, byteOrderMark: false),
                    UTF32Encoding u => new UTF32Encoding(u.CodePage == 12001, byteOrderMark: false),
                    _ => utf8
                };
            }
            File.AppendAllText(full, (needBreak ? Environment.NewLine : "") + content, encoding);
        }
        Log.Info($"      Đã {(s.DataAction == DataAction.WriteText ? "ghi" : "thêm")} {content.Length} ký tự vào \"{full}\": " +
                 Truncate(Log.Redact(content.Trim())).Replace("\r", "").Replace("\n", " ⏎ "));
    }

    // ───────────────────────────── Phát video ─────────────────────────────

    /// <summary>Phát lần lượt danh sách video / nhạc bằng trình phát của ScheduleApp; phát hết mới sang bước sau.</summary>
    private static async Task PlayMediaAsync(ActionStep s, FlowContext ctx)
    {
        var lines = s.MediaLines;
        if (lines.Count == 0) throw new InvalidOperationException("Chưa có file video nào trong danh sách.");
        // Text đã được thay biến (ExpandStep) → mọi dòng đều là đường dẫn.
        var plan = await MediaInfo.AnalyzeAsync(lines, expand: null, ctx.Ct);
        foreach (var e in plan.Entries.Where(e => e.Path == null)) Log.Warn($"      Bỏ qua — {e.Problem}: {e.Line}");
        var playable = plan.Entries.Where(e => e.Path != null).ToList();
        if (playable.Count == 0)
            throw new InvalidOperationException("Không thấy file video nào: " + string.Join("; ", plan.Entries.Select(e => e.Line)));
        var files = playable.Select(e => e.Path!).ToList();

        int volume = int.TryParse(s.Arguments.Trim().TrimEnd('%'), out int v) ? v : 100;
        Log.Info($"      Phát {files.Count} file — tổng {ActionStep.FormatDuration(plan.Total)}" +
                 (plan.UnknownCount > 0 ? $" + {plan.UnknownCount} file chưa rõ thời lượng" : "") +
                 $", dự kiến xong khoảng {DateTime.Now + plan.Total:HH:mm:ss}{(s.Force ? " · toàn màn hình" : "")}. Esc: dừng · →: sang file kế · Space: tạm dừng.");
        Log.Info("      " + Truncate(string.Join(" → ", playable.Select(e =>
            Path.GetFileName(e.Path) + (e.Duration is { } d ? $" ({ActionStep.FormatDuration(d)})" : "")))));
        var (display, warning, note) = Displays.Pick(s.Monitor, s.MonitorId);
        if (warning != null) Log.Warn("      " + warning);
        else if (note != null || Displays.All().Count > 1) Log.Info($"      Phát ở {Displays.Label(display, Displays.All())}{(note == null ? "" : " — " + note)}.");
        var r = await MediaPlayback.PlayAsync(files, s.Force, volume, ctx.Ct, durations: playable.Select(e => e.Duration).ToList(), display: display);
        foreach (var p in r.Problems) Log.Warn("      Bỏ qua — " + p);
        // Thời lượng thực đã phát: chỉ các file phát hết (không tính file bỏ qua / lỗi).
        var played = TimeSpan.FromTicks((r.PlayedIndexes ?? []).Sum(i => playable[i].Duration?.Ticks ?? 0));
        ctx.Vars["media.played"] = r.Played.ToString(CultureInfo.InvariantCulture);
        ctx.Vars["media.duration"] = ActionStep.FormatDuration(played);
        ctx.Vars["media.seconds"] = ((long)Math.Round(played.TotalSeconds, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        if (r.StoppedByUser) throw new InvalidOperationException($"Đã dừng phát giữa chừng sau {r.Played}/{files.Count} file.");
        // Người dùng chủ động bấm → bỏ qua hết thì không phải lỗi.
        if (r.Played == 0 && r.Skipped == 0) throw new InvalidOperationException("Không phát được file nào: " + string.Join("; ", r.Problems));
        Log.Info($"      ✔ Đã phát xong {r.Played}/{files.Count} file (tổng {ActionStep.FormatDuration(played)})" +
                 (r.Skipped > 0 ? $", bỏ qua {r.Skipped} file (→)." : "."));
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
                if (s.Force) Log.MaskGuessed(value);
                // Người dùng tự gõ \\máy\thư-mục → các bước sau được mở đường dẫn tới máy đó.
                else ctx.TrustServersIn(value);
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
        // Giá trị nhập dạng ẩn không bao giờ hiện trong nhật ký (kể cả mã PIN ngắn không được che toàn cục).
        var shown = s.VarSource == VarSource.AskUser && s.Force ? "***" : Truncate(value).Replace("\r", "").Replace("\n", " ⏎ ");
        Log.Info($"      {{{{{s.Variable.Trim()}}}}} = \"{shown}\"");
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
    /// Click phần tử UI. Bước do trình ghi macro tạo còn giữ hình mẫu và tọa độ lúc ghi (so với cửa sổ đích):
    /// không tìm thấy phần tử (tên đổi, phần tử không hỗ trợ UI Automation…) thì tìm theo hình mẫu, rồi tới tọa độ, thay vì dừng flow.
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
        catch (TimeoutException ex) when (hasTarget && (s.HasRecordedPoint || s.HasImageAnchor))
        {
            Point? recorded = null;
            if (s.HasRecordedPoint)
            {
                var (x, y) = await ToScreenAsync(s, s.X, s.Y, ct);
                recorded = new Point(x, y);
            }
            Point? at = null;
            if (s.HasImageAnchor)
            {
                Log.Warn($"      {ex.Message} → tìm theo hình mẫu.");
                at = await FindAnchorAsync(s, recorded, 2_000, ct);
            }
            if (at == null)
            {
                if (recorded == null) throw;
                Log.Warn($"      {(s.HasImageAnchor ? "" : ex.Message + " ")}→ click theo tọa độ lúc ghi ({s.X}, {s.Y}).");
                at = recorded;
            }
            InputSimulator.Click(at.Value.X, at.Value.Y, s.Button, s.DoubleClick);
            return;
        }
        UiElementFinder.LogFound(e);
        await UiElementFinder.ClickAsync(e, s.Button, s.DoubleClick, ct);
    }

    /// <summary>
    /// Click kèm hình mẫu: tìm lại chỗ cần click theo hình ảnh (cửa sổ di chuyển, đổi kích thước, bố cục xê dịch vẫn đúng).
    /// null = không thấy sau <paramref name="timeoutMs"/> (đã ghi log, người gọi dùng tọa độ lúc ghi nếu có).
    /// </summary>
    /// <param name="recorded">Điểm click theo tọa độ lúc ghi (tọa độ màn hình), nếu có.</param>
    private static async Task<Point?> FindAnchorAsync(ActionStep s, Point? recorded, int timeoutMs, CancellationToken ct)
    {
        var window = string.IsNullOrWhiteSpace(s.Target) ? IntPtr.Zero : WindowHelper.Find(s.Target);
        var result = await ScreenLocator.WaitAnchorAsync(s, window, recorded, timeoutMs, ct);
        if (!result.Found)
        {
            Log.Warn($"      Không thấy hình mẫu sau {ActionStep.FormatMs(timeoutMs)} — {result.Detail}.");
            return null;
        }
        var p = result.ClickPoint;
        bool moved = recorded is { } r && (Math.Abs(p.X - r.X) > 2 || Math.Abs(p.Y - r.Y) > 2);
        Log.Info($"      🖼 Thấy hình mẫu — {result.Detail}" +
                 (moved ? $"; đã dời ({p.X - recorded!.Value.X:+0;-0}, {p.Y - recorded.Value.Y:+0;-0}) px so với lúc ghi" : ""));
        return p;
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
        // Chữ dán có thể là mật khẩu ({{secret:…}}) → không lưu vào Lịch sử clipboard / clipboard đám mây, dọn ngay sau khi dán.
        await ClipboardHelper.PasteTemporarilyAsync(s.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            () => InputSimulator.SendKeys("Ctrl+V", ct), ct);
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
        // thì output tiếng Việt mới đúng UTF-8. Lệnh được bọc thêm một cặp nháy ngoài → phải "trung hòa" cho lớp
        // cmd NGOÀI (xem EscapeForOuterCmd) để dữ liệu nằm trong dấu nháy không bị hiểu thành lệnh. Dữ liệu tự chứa dấu nháy
        // ở lớp trong do VariableExpander.ExpandCommand chặn ({{x}} có dấu nháy → lỗi, {{x:cmd}} bỏ dấu nháy).
        // cmd.exe / chcp.com gọi bằng đường dẫn đầy đủ (không chạy nhầm file cùng tên cạnh ScheduleApp / trong thư mục hiện tại);
        // /v:off: dấu ! trong dữ liệu không bị thay như biến kể cả khi máy bật DelayedExpansion trong registry.
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/d /v:off /c {SystemExe("chcp.com")} 65001>nul & {SystemExe("cmd.exe")} /d /v:off /s /c \"{EscapeForOuterCmd(command)}\"")
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

    /// <summary>
    /// Đường dẫn đầy đủ trong System32 để đặt thẳng vào dòng lệnh cmd; thư mục Windows có khoảng trắng / ký tự đặc biệt (hiếm) thì dùng tên
    /// trần — không bọc nháy được vì dấu nháy đầu dòng làm cmd bỏ cặp nháy ngoài cùng của lệnh.
    /// </summary>
    private static string SystemExe(string exe)
    {
        var path = Path.Combine(Environment.SystemDirectory, exe);
        return path.IndexOfAny([' ', '&', '(', ')', '^', '%', '!']) < 0 ? path : exe;
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

    /// <summary>
    /// Trung hòa lệnh cho lớp cmd NGOÀI của "cmd /d /c chcp 65001 & cmd /d /s /c \"{lệnh}\"".
    /// Vì ta thêm một cặp nháy bao quanh lệnh, trạng thái "trong/ngoài dấu nháy" của lớp ngoài NGƯỢC với lệnh thật:
    /// ký tự người dùng đặt TRONG dấu nháy (vd tên file) lại nằm NGOÀI dấu nháy đối với lớp ngoài, nên "&amp; | &lt; &gt; ^ ( )"
    /// trong dữ liệu sẽ bị chạy như lệnh. Ta đi dọc lệnh với cờ nháy bắt đầu là "đang trong nháy", đảo khi gặp dấu ",
    /// và khi lớp ngoài đang NGOÀI nháy thì thêm ^ trước các ký tự đặc biệt đó. Lớp ngoài bỏ ^ rồi truyền ĐÚNG lệnh gốc
    /// cho lớp trong tự phân tích một lần như người dùng viết — giữ nguyên %VAR%, &amp;, |, &gt; và dấu ngoặc có chủ đích.
    /// </summary>
    internal static string EscapeForOuterCmd(string command)
    {
        var sb = new StringBuilder(command.Length + 16);
        bool insideQuote = true; // cặp nháy mở do ProcessStartInfo thêm vào ngay trước lệnh
        foreach (char c in command)
        {
            if (c == '"') insideQuote = !insideQuote;
            else if (!insideQuote && c is '&' or '|' or '<' or '>' or '^' or '(' or ')') sb.Append('^');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Rút gọn để ghi log / báo lỗi — che bí mật TRƯỚC khi cắt (cắt trước thì nửa bí mật còn lại không che được).</summary>
    private static string Truncate(string s)
    {
        s = Log.Redact(s);
        return s.Length > 500 ? s[..500] + "…" : s;
    }
}
