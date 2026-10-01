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

    private static async Task<bool> EvaluateCoreAsync(ActionStep s, FlowContext ctx)
    {
        var ct = ctx.Ct;
        switch (s.Condition)
        {
            case ConditionKind.Compare:
                return Compare(s.Target, s.CompareOp, s.Arguments);

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
