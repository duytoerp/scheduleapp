using ScheduleApp.Models;

namespace ScheduleApp.Services.Engine;

/// <summary>
/// Chạy danh sách bước của một công việc: điều khiển luồng (Nếu/Lặp/Nhãn), thay biến, thử lại và xử lý lỗi từng bước,
/// chụp màn hình khi lỗi, tạm dừng gỡ lỗi và gọi công việc con.
/// </summary>
public static class FlowEngine
{
    private const int MaxCallDepth = 8;

    /// <summary>Kết quả thực thi một bước có áp dụng chính sách thử lại.</summary>
    private sealed record Attempt(bool Ok, string Error, ActionStep? Expanded);

    /// <param name="isRoot">Flow gốc (báo vị trí bước cho UI, áp dụng điểm dừng). Flow con chạy "trong" bước gọi nó.</param>
    public static async Task<FlowResult> RunAsync(Job job, FlowContext ctx, int startIndex, bool isRoot)
    {
        var steps = job.Steps;
        var fs = FlowStructure.Build(steps);
        if (!fs.IsValid) return new FlowResult(false, "Flow có lỗi cấu trúc — " + fs.Errors[0]);

        foreach (var v in job.Variables)
            if (!string.IsNullOrWhiteSpace(v.Name) && !ctx.Vars.ContainsKey(v.Name.Trim()))
                ctx.Vars[v.Name.Trim()] = ctx.Expand(v.Value);

        var loops = new List<LoopFrame>();
        int errors = 0;
        int total = steps.Count;
        string indent = new(' ', ctx.Depth * 3);
        int pc = Math.Clamp(startIndex, 0, total);

        while (pc < total)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var step = steps[pc];
            bool marker = step.Type is StepType.Else or StepType.EndIf or StepType.EndLoop;

            if (!step.Enabled && !marker)
            {
                // Tắt một khối Nếu/Lặp = bỏ qua cả khối.
                pc = step.Type is StepType.If or StepType.Loop && fs.Match[pc] >= 0 ? fs.Match[pc] + 1 : pc + 1;
                continue;
            }

            if (isRoot) ctx.Options.StepStarted?.Invoke(pc);

            if (!marker)
            {
                if (isRoot && (ctx.StepMode || (ctx.Options.UseBreakpoints && step.Breakpoint)))
                {
                    var cmd = await ctx.Ui.DebugPauseAsync(job.Name, pc, $"{pc + 1}. {step.Describe()}",
                        ctx.StepMode ? "Chạy từng bước" : "Điểm dừng", ctx.Vars, ctx.Ct);
                    if (cmd == DebugCommand.Stop) throw new OperationCanceledException("Người dùng dừng khi gỡ lỗi.");
                    ctx.StepMode = cmd == DebugCommand.Step;
                }
                if (ctx.BeforeStep != null) await ctx.BeforeStep(ctx);
                Log.Info($"{indent}   [{pc + 1}/{total}] {step.Describe()}");
            }

            switch (step.Type)
            {
                case StepType.If:
                {
                    bool truth = false;
                    var a = await TryAsync(step, pc, ctx, async s => truth = await ConditionEvaluator.EvaluateAsync(s, ctx));
                    if (!a.Ok)
                    {
                        var jump = HandleFailure(job, step, pc, a.Error, ctx, fs, loops, ref errors, out var fail);
                        if (fail != null) return fail;
                        if (jump >= 0) { pc = jump; continue; }
                    }
                    Log.Info($"{indent}      → {(truth ? "đúng" : "sai")}");
                    pc = truth ? pc + 1 : fs.ElseOf[pc] >= 0 ? fs.ElseOf[pc] + 1 : fs.Match[pc] + 1;
                    continue;
                }

                case StepType.Else:
                    // Tới đây nghĩa là vừa chạy xong nhánh "đúng" → bỏ qua nhánh "ngược lại".
                    pc = fs.Match[pc] + 1;
                    continue;

                case StepType.EndIf:
                case StepType.Label:
                    pc++;
                    continue;

                case StepType.Loop:
                {
                    LoopFrame? frame = null;
                    var a = await TryAsync(step, pc, ctx, async s =>
                    {
                        frame = LoopFrame.Create(s, pc, fs.Match[pc]);
                        if (!await frame.MoveNextAsync(step, ctx)) frame = null;
                    });
                    if (!a.Ok)
                    {
                        var jump = HandleFailure(job, step, pc, a.Error, ctx, fs, loops, ref errors, out var fail);
                        if (fail != null) return fail;
                        pc = jump >= 0 ? jump : fs.Match[pc] + 1;
                        continue;
                    }
                    if (frame == null)
                    {
                        Log.Info($"{indent}      → không có lần lặp nào");
                        pc = fs.Match[pc] + 1;
                        continue;
                    }
                    loops.Add(frame);
                    Log.Info($"{indent}      ↻ lần 1{(frame.Total is int t ? $"/{t}" : "")}");
                    pc++;
                    continue;
                }

                case StepType.EndLoop:
                {
                    var frame = loops.Count > 0 && loops[^1].End == pc ? loops[^1] : null;
                    if (frame == null) { pc++; continue; } // bắt đầu chạy từ giữa vòng lặp
                    if (!steps[frame.Start].Enabled) { loops.RemoveAt(loops.Count - 1); pc++; continue; }

                    var loopStep = steps[frame.Start];
                    bool more;
                    try
                    {
                        more = await frame.MoveNextAsync(loopStep, ctx);
                    }
                    catch (OperationCanceledException) when (ctx.Ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        loops.RemoveAt(loops.Count - 1);
                        var jump = HandleFailure(job, loopStep, frame.Start, ex.Message, ctx, fs, loops, ref errors, out var fail);
                        if (fail != null) return fail;
                        pc = jump >= 0 ? jump : pc + 1;
                        continue;
                    }
                    if (more)
                    {
                        Log.Info($"{indent}   ↻ [{frame.Start + 1}] lần {frame.Index}{(frame.Total is int t ? $"/{t}" : "")}");
                        pc = frame.Start + 1;
                    }
                    else
                    {
                        loops.RemoveAt(loops.Count - 1);
                        pc++;
                    }
                    continue;
                }

                case StepType.BreakLoop:
                {
                    if (loops.Count == 0) { pc++; continue; }
                    var frame = loops[^1];
                    loops.RemoveAt(loops.Count - 1);
                    pc = frame.End + 1;
                    continue;
                }

                case StepType.Goto:
                {
                    int target = fs.Labels[step.Target.Trim()];
                    loops.RemoveAll(f => !(target > f.Start && target <= f.End));
                    pc = target;
                    continue;
                }

                case StepType.StopFlow:
                {
                    var msg = ctx.Expand(step.Text);
                    if (step.Force)
                    {
                        Log.Warn($"{indent}   ■ Dừng flow (lỗi){(msg.Length > 0 ? ": " + msg : "")}");
                        return new FlowResult(false, msg.Length > 0 ? msg : "Dừng flow theo bước " + (pc + 1), pc + 1, ctx.LastScreenshot);
                    }
                    Log.Info($"{indent}   ■ Dừng flow{(msg.Length > 0 ? ": " + msg : "")}");
                    return new FlowResult(errors == 0, msg.Length > 0 ? msg : "Dừng sớm theo flow");
                }

                case StepType.CallJob:
                {
                    var a = await TryAsync(step, pc, ctx, async _ =>
                    {
                        var sub = (step.JobRef is Guid id ? ctx.FindJob(id) : null)
                                  ?? throw new InvalidOperationException($"Không tìm thấy công việc \"{step.Target}\" (đã bị xóa?).");
                        if (ctx.Depth >= MaxCallDepth) throw new InvalidOperationException("Gọi công việc lồng nhau quá sâu (có thể đang tự gọi lại chính nó).");
                        ctx.Depth++;
                        try
                        {
                            var r = await RunAsync(sub, ctx, 0, isRoot: false);
                            if (!r.Ok) throw new InvalidOperationException($"\"{sub.Name}\": {r.Message}");
                        }
                        finally
                        {
                            ctx.Depth--;
                        }
                    }, screenshot: false);
                    if (!a.Ok)
                    {
                        var jump = HandleFailure(job, step, pc, a.Error, ctx, fs, loops, ref errors, out var fail);
                        if (fail != null) return fail;
                        if (jump >= 0) { pc = jump; continue; }
                    }
                    break;
                }

                default:
                {
                    var a = await TryAsync(step, pc, ctx, s => StepExecutor.ExecuteAsync(s, job, ctx));
                    if (!a.Ok)
                    {
                        var jump = HandleFailure(job, step, pc, a.Error, ctx, fs, loops, ref errors, out var fail);
                        if (fail != null) return fail;
                        if (jump >= 0) { pc = jump; continue; }
                    }
                    break;
                }
            }

            if (step.DelayAfterMs > 0) await Task.Delay(step.DelayAfterMs, ctx.Ct);
            pc++;
        }

        return errors > 0
            ? new FlowResult(false, $"Xong, {errors} bước lỗi", -1, ctx.LastScreenshot)
            : new FlowResult(true, "Thành công");
    }

    /// <summary>Chạy hành động của bước với số lần thử lại đã cấu hình. Biến được thay lại ở mỗi lần thử.</summary>
    private static async Task<Attempt> TryAsync(ActionStep step, int pc, FlowContext ctx, Func<ActionStep, Task> action, bool screenshot = true)
    {
        int attempts = 1 + Math.Max(0, step.Retries);
        string error = "";
        for (int i = 1; i <= attempts; i++)
        {
            ActionStep? expanded = null;
            try
            {
                expanded = ctx.ExpandStep(step);
                await action(expanded);
                // Nếu/Lặp chỉ đọc trạng thái lỗi — không xóa, để các bước bên trong khối vẫn dùng được {{lastError}}.
                if (!step.IsControl)
                {
                    ctx.LastStepFailed = false;
                    ctx.Vars["lastError"] = "";
                }
                return new Attempt(true, "", expanded);
            }
            catch (OperationCanceledException) when (ctx.Ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (i < attempts)
                {
                    Log.Warn($"      ⟳ Lỗi: {error} — thử lại lần {i}/{attempts - 1} sau {ActionStep.FormatMs(step.RetryDelayMs)}.");
                    await Task.Delay(Math.Max(0, step.RetryDelayMs), ctx.Ct);
                }
            }
        }
        ctx.LastStepFailed = true;
        ctx.Vars["lastError"] = error;
        if (screenshot && SettingsStore.Current.ScreenshotOnError)
            ctx.LastScreenshot = ErrorScreenshots.Capture(ctx.RootJob.Name, pc + 1) ?? ctx.LastScreenshot;
        return new Attempt(false, error, null);
    }

    /// <summary>
    /// Áp dụng "Khi lỗi" của bước. Trả về vị trí cần nhảy tới (-1 = chạy bước kế), hoặc đặt <paramref name="fail"/> nếu phải dừng flow.
    /// </summary>
    private static int HandleFailure(Job job, ActionStep step, int pc, string error, FlowContext ctx, FlowStructure fs,
        List<LoopFrame> loops, ref int errors, out FlowResult? fail)
    {
        errors++;
        fail = null;
        Log.Error($"{new string(' ', ctx.Depth * 3)}   ✖ Bước {pc + 1} lỗi: {error}");

        var action = step.OnError == ErrorAction.Default
            ? (job.StopOnError ? ErrorAction.Stop : ErrorAction.Continue)
            : step.OnError;

        switch (action)
        {
            case ErrorAction.GotoLabel when fs.Labels.TryGetValue(step.ErrorLabel.Trim(), out int target):
                Log.Info($"      → nhảy tới nhãn \"{step.ErrorLabel}\"");
                loops.RemoveAll(f => !(target > f.Start && target <= f.End));
                return target;
            case ErrorAction.Continue:
                return -1;
            default:
                fail = new FlowResult(false, $"Lỗi ở bước {pc + 1}: {error}", pc + 1, ctx.LastScreenshot);
                return -1;
        }
    }
}
