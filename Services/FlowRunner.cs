using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>
/// Chạy flow của các công việc theo hàng đợi — mỗi lúc chỉ một flow, tránh hai flow cùng điều khiển chuột/phím.
/// </summary>
public sealed class FlowRunner
{
    private readonly IUserNotifier _ui;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly HashSet<Guid> _pending = [];
    private CancellationTokenSource _stopAll = new();

    /// <summary>Trạng thái hiện tại (mô tả bước đang chạy). Có thể phát từ luồng nền.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>(jobId, thời điểm bắt đầu, thành công?, thông điệp). Có thể phát từ luồng nền.</summary>
    public event Action<Guid, DateTime, bool, string>? JobFinished;

    public FlowRunner(IUserNotifier ui) => _ui = ui;

    public bool IsBusy
    {
        get { lock (_sync) return _pending.Count > 0; }
    }

    /// <summary>Đưa công việc vào hàng đợi; task hoàn thành khi flow chạy xong (hoặc bị hủy).</summary>
    public async Task EnqueueAsync(Job job, string trigger)
    {
        CancellationToken stopToken;
        lock (_sync)
        {
            if (!_pending.Add(job.Id))
            {
                Log.Warn($"[{job.Name}] đang chạy hoặc đã trong hàng đợi — bỏ qua lần kích hoạt ({trigger}).");
                return;
            }
            stopToken = _stopAll.Token;
        }

        bool entered = false;
        var started = DateTime.Now;
        try
        {
            if (_gate.CurrentCount == 0) Log.Info($"[{job.Name}] chờ trong hàng đợi…");
            await _gate.WaitAsync(stopToken);
            entered = true;
            started = DateTime.Now;

            var (ok, message) = await Task.Run(() => RunFlowAsync(job, trigger, stopToken));
            JobFinished?.Invoke(job.Id, started, ok, message);
            if (!ok) _ui.Notify($"Flow \"{job.Name}\" không hoàn thành", message, true);
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"[{job.Name}] đã hủy khi đang chờ trong hàng đợi.");
            JobFinished?.Invoke(job.Id, started, false, "Đã hủy (trong hàng đợi)");
        }
        finally
        {
            lock (_sync) _pending.Remove(job.Id);
            if (entered) _gate.Release();
            StatusChanged?.Invoke(IsBusy ? "Đang chờ flow tiếp theo…" : "Sẵn sàng");
        }
    }

    /// <summary>Dừng flow đang chạy và hủy mọi flow đang chờ.</summary>
    public void StopAll()
    {
        lock (_sync)
        {
            if (_pending.Count == 0) return;
            _stopAll.Cancel();
            _stopAll = new CancellationTokenSource();
        }
        Log.Warn("■ Đã yêu cầu dừng tất cả flow.");
    }

    private async Task<(bool Ok, string Message)> RunFlowAsync(Job job, string trigger, CancellationToken ct)
    {
        var steps = job.Steps;
        int total = steps.Count;
        int errors = 0;
        Log.Info($"▶ Bắt đầu \"{job.Name}\" ({trigger}) — {total} bước");

        IDisposable? screen = null;
        try
        {
            if (steps.Any(s => s.Enabled && (s.UsesInput || s.UsesScreen))) screen = _ui.ClearScreenForAutomation();

            for (int i = 0; i < total; i++)
            {
                var step = steps[i];
                if (!step.Enabled) continue;
                ct.ThrowIfCancellationRequested();

                var desc = step.Describe();
                StatusChanged?.Invoke($"Đang chạy \"{job.Name}\" — bước {i + 1}/{total}: {desc}");
                Log.Info($"   [{i + 1}/{total}] {desc}");

                try
                {
                    await StepExecutor.ExecuteAsync(step, job, _ui, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors++;
                    Log.Error($"   ✖ Bước {i + 1} lỗi: {ex.Message}");
                    if (job.StopOnError) return (false, $"Lỗi ở bước {i + 1}: {ex.Message}");
                }

                if (step.DelayAfterMs > 0) await Task.Delay(step.DelayAfterMs, ct);
            }

            if (errors > 0)
            {
                Log.Warn($"◼ \"{job.Name}\" xong nhưng có {errors} bước lỗi.");
                return (false, $"Xong, {errors} bước lỗi");
            }
            Log.Info($"✔ Hoàn thành \"{job.Name}\".");
            return (true, "Thành công");
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"■ \"{job.Name}\" đã bị dừng.");
            return (false, "Đã dừng");
        }
        catch (Exception ex)
        {
            Log.Error($"✖ \"{job.Name}\" lỗi: {ex.Message}");
            return (false, ex.Message);
        }
        finally
        {
            screen?.Dispose();
        }
    }
}
