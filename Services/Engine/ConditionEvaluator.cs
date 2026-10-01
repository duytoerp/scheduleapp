using System.Diagnostics;
using System.Text.RegularExpressions;
using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Vision;

namespace ScheduleApp.Services.Engine;

/// <summary>Đánh giá điều kiện của bước "Nếu" / "Lặp khi".</summary>
public static class ConditionEvaluator
{
    /// <param name="s">Bước đã được thay {{biến}}.</param>
    public static async Task<bool> EvaluateAsync(ActionStep s, FlowContext ctx)
    {
        bool result = await EvaluateCoreAsync(s, ctx);
        return s.Negate ? !result : result;
    }

    /// <summary>
    /// Bước "Kiểm tra (Assert)": kiểm tra lại tới khi điều kiện (đã tính cả "Đảo ngược") đúng hoặc hết thời gian chờ.
    /// Sai thì báo lỗi kèm giá trị thực tế — bước được ghi là "không đạt" trong báo cáo.
    /// </summary>
    public static async Task AssertAsync(ActionStep s, FlowContext ctx)
    {
        var once = s.ShallowCopy();
        once.DelayMs = 0;
        var sw = Stopwatch.StartNew();
        string? error = null;
        while (true)
        {
            try
            {
                if (await EvaluateAsync(once, ctx))
                {
                    Log.Info($"      ✔ Đạt{(ctx.ConditionDetail.Length > 0 ? " — " + ctx.ConditionDetail : "")}");
                    return;
                }
                error = null;
            }
            catch (InvalidOperationException ex)
            {
                error = ex.Message; // vd form chưa tải xong — kiểm tra lại tới khi hết giờ
            }
            if (sw.ElapsedMilliseconds >= s.DelayMs) break;
            await Task.Delay(300, ctx.Ct);
        }

        var what = string.IsNullOrWhiteSpace(s.Message) ? s.DescribeCondition() : s.Message.Trim();
        var detail = error ?? ctx.ConditionDetail;
        throw new InvalidOperationException($"Kiểm tra không đạt: {what}{(detail.Length > 0 ? " — " + detail : "")}");
    }

    private static async Task<bool> EvaluateCoreAsync(ActionStep s, FlowContext ctx)
    {
        var ct = ctx.Ct;
        switch (s.Condition)
        {
            case ConditionKind.Compare:
                ctx.ConditionDetail = s.CompareOp is CompareOp.IsEmpty or CompareOp.IsNotEmpty
                    ? $"giá trị: \"{Short(s.Target)}\""
                    : $"giá trị: \"{Short(s.Target)}\", mong đợi: \"{Short(s.Arguments)}\"";
                return Compare(s.Target, s.CompareOp, s.Arguments);

            case ConditionKind.BrowserElement:
            {
                bool found = await BrowserClient.ExistsAsync(s.Target, s.Text, Math.Max(0, s.DelayMs), ct);
                ctx.ConditionDetail = found ? "có phần tử trên trang" : "không thấy phần tử trên trang";
                return found;
            }

            case ConditionKind.D365FieldValue:
                return await PollD365Async(async () =>
                {
                    var value = await D365Client.ReadFieldAsync(s.Target, s.Text, ct);
                    ctx.ConditionDetail = s.CompareOp is CompareOp.IsEmpty or CompareOp.IsNotEmpty
                        ? $"giá trị field: \"{Short(value)}\""
                        : $"giá trị field: \"{Short(value)}\", mong đợi: \"{Short(s.Arguments)}\"";
                    return Compare(value, s.CompareOp, s.Arguments);
                }, s.DelayMs, ct);

            case ConditionKind.D365FieldState:
                return await PollD365Async(async () =>
                {
                    var st = await D365Client.FieldStateAsync(s.Target, s.Text, ct);
                    ctx.ConditionDetail = "trạng thái: " + string.Join(", ", st.Select(kv => $"{kv.Key}={kv.Value}"));
                    return D365Client.HasState(st, s.Arguments);
                }, s.DelayMs, ct);

            case ConditionKind.D365Notification:
                return await PollD365Async(async () =>
                {
                    var text = await D365Client.NotificationsAsync(s.Target, ct);
                    ctx.ConditionDetail = text.Length == 0 ? "form không có thông báo nào" : $"thông báo: \"{Short(text.Replace("\n", " | "))}\"";
                    return string.IsNullOrWhiteSpace(s.Text)
                        ? text.Length > 0
                        : text.Contains(s.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
                }, s.DelayMs, ct);

            case ConditionKind.D365RecordCount:
                return await PollD365Async(async () =>
                {
                    int n = await D365Client.CountAsync(s.Target, s.Text, ct);
                    ctx.ConditionDetail = $"số bản ghi: {n}";
                    return Compare(n.ToString(System.Globalization.CultureInfo.InvariantCulture), s.CompareOp, s.Arguments);
                }, s.DelayMs, ct);

            case ConditionKind.WindowExists:
                return await PollAsync(() => Task.FromResult(WindowHelper.Find(s.Target) != IntPtr.Zero), s.DelayMs, ct);

            case ConditionKind.ProcessRunning:
            {
                var name = s.Target.Trim();
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                return await PollAsync(() =>
                {
                    var procs = Process.GetProcessesByName(name);
                    foreach (var p in procs) p.Dispose();
                    return Task.FromResult(procs.Length > 0);
                }, s.DelayMs, ct);
            }

            case ConditionKind.FileExists:
            {
                var path = Environment.ExpandEnvironmentVariables(s.Target.Trim().Trim('"'));
                return await PollAsync(() => Task.FromResult(File.Exists(path) || Directory.Exists(path)), s.DelayMs, ct);
            }

            case ConditionKind.ImageOnScreen or ConditionKind.TextOnScreen:
            {
                var probe = s.ShallowCopy();
                probe.Type = s.Condition == ConditionKind.ImageOnScreen ? StepType.WaitForImage : StepType.WaitForText;
                probe.DelayMs = Math.Max(0, s.DelayMs);
                if (!string.IsNullOrWhiteSpace(probe.Target) && WindowHelper.Find(probe.Target) == IntPtr.Zero) return false;
                var r = await ScreenLocator.WaitAsync(probe, ct);
                Log.Info($"      {(r.Found ? "Thấy" : "Không thấy")} — {r.Detail}");
                return r.Found;
            }

            case ConditionKind.ElementExists:
                try
                {
                    await UiElementFinder.WaitAsync(s.Target, s.Text, Math.Max(0, s.DelayMs), ct);
                    return true;
                }
                catch (TimeoutException)
                {
                    return false;
                }

            case ConditionKind.LastStepFailed:
                return ctx.LastStepFailed;

            default:
                return false;
        }
    }

    /// <summary>Kiểm tra lặp lại tới khi đúng hoặc hết <paramref name="timeoutMs"/> (0 = kiểm tra một lần).</summary>
    private static async Task<bool> PollAsync(Func<Task<bool>> check, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (await check()) return true;
            if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            await Task.Delay(300, ct);
        }
    }

    /// <summary>
    /// Như <see cref="PollAsync"/> nhưng lỗi tạm thời (form đang tải, chưa có Xrm…) cũng được thử lại; hết giờ mà vẫn lỗi thì báo lỗi đó.
    /// </summary>
    private static async Task<bool> PollD365Async(Func<Task<bool>> check, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (await check()) return true;
                if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            }
            catch (InvalidOperationException) when (sw.ElapsedMilliseconds < timeoutMs)
            {
            }
            await Task.Delay(300, ct);
        }
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s.Replace("\r", "").Replace("\n", " ⏎ ");

    /// <summary>So sánh hai giá trị: so số nếu cả hai là số, ngược lại so chuỗi không phân biệt hoa thường.</summary>
    public static bool Compare(string left, CompareOp op, string right)
    {
        left ??= "";
        right ??= "";
        bool numeric = VariableExpander.TryParseNumber(left, out var a) & VariableExpander.TryParseNumber(right, out var b);
        int cmp = numeric ? a.CompareTo(b) : string.Compare(left.Trim(), right.Trim(), StringComparison.CurrentCultureIgnoreCase);

        return op switch
        {
            CompareOp.Equals => cmp == 0,
            CompareOp.NotEquals => cmp != 0,
            CompareOp.Contains => left.Contains(right, StringComparison.CurrentCultureIgnoreCase),
            CompareOp.NotContains => !left.Contains(right, StringComparison.CurrentCultureIgnoreCase),
            CompareOp.StartsWith => left.Trim().StartsWith(right.Trim(), StringComparison.CurrentCultureIgnoreCase),
            CompareOp.Greater => cmp > 0,
            CompareOp.GreaterOrEqual => cmp >= 0,
            CompareOp.Less => cmp < 0,
            CompareOp.LessOrEqual => cmp <= 0,
            CompareOp.IsEmpty => string.IsNullOrWhiteSpace(left),
            CompareOp.IsNotEmpty => !string.IsNullOrWhiteSpace(left),
            CompareOp.Regex => Regex.IsMatch(left, right, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)),
            _ => false
        };
    }
}
