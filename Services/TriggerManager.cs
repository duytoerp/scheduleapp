using System.Diagnostics;
using Microsoft.Win32;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.Services;

/// <summary>Cửa sổ nhận phím tắt toàn hệ thống (RegisterHotKey gắn với một cửa sổ).</summary>
public interface IHotkeyHost
{
    bool RegisterHotkey(int id, uint modifiers, uint vk);
    void UnregisterHotkey(int id);
}

/// <summary>
/// Kích hoạt công việc theo sự kiện: phím tắt, file mới, ứng dụng mở/đóng, máy rảnh, mở khóa màn hình, khi ScheduleApp khởi động.
/// Phải tạo và gọi <see cref="Reload"/> trên luồng UI.
/// </summary>
public sealed class TriggerManager : IDisposable
{
    public const int HotkeyIdBase = 0x6000;

    private readonly List<Job> _jobs;
    private readonly FlowRunner _runner;
    private readonly IHotkeyHost _host;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    private readonly Dictionary<int, Job> _hotkeys = [];
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<Guid, Queue<(string Trigger, Dictionary<string, string> Vars)>> _fileQueues = [];
    private readonly object _queueSync = new();

    private HashSet<string> _runningProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(Guid, int)> _idleFired = [];
    private int _tick;

    private DateTime _nextMailCheck = DateTime.MinValue;
    private bool _mailBusy;
    private string _lastMailError = "";

    public TriggerManager(List<Job> jobs, FlowRunner runner, IHotkeyHost host)
    {
        _jobs = jobs;
        _runner = runner;
        _host = host;
        _timer.Tick += (_, _) => Poll();
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>Trình kích hoạt đang bật của các công việc bật và đã duyệt (công việc chờ duyệt không được kích hoạt).</summary>
    private IEnumerable<(Job Job, JobTrigger Trigger)> Active(TriggerType type) =>
        _jobs.Where(j => j.Armed).SelectMany(j => j.Triggers.Where(t => t.Enabled && t.Type == type).Select(t => (j, t)));

    /// <summary>Đăng ký lại toàn bộ trình kích hoạt sau khi danh sách công việc thay đổi.</summary>
    public void Reload()
    {
        foreach (var id in _hotkeys.Keys) _host.UnregisterHotkey(id);
        _hotkeys.Clear();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();

        int next = HotkeyIdBase;
        foreach (var (job, t) in Active(TriggerType.Hotkey))
        {
            try
            {
                var (mods, vk) = ParseHotkey(t.Value);
                int id = next++;
                if (_host.RegisterHotkey(id, mods | Win32.MOD_NOREPEAT, vk)) _hotkeys[id] = job;
                else Log.Warn($"[{job.Name}] Phím tắt {t.Value} đã bị ứng dụng khác (hoặc công việc khác) dùng.");
            }
            catch (FormatException ex)
            {
                Log.Warn($"[{job.Name}] Phím tắt \"{t.Value}\" không hợp lệ: {ex.Message}");
            }
        }

        foreach (var (job, t) in Active(TriggerType.FileCreated))
        {
            var folder = Environment.ExpandEnvironmentVariables(t.Value.Trim().Trim('"'));
            if (!Directory.Exists(folder))
            {
                Log.Warn($"[{job.Name}] Thư mục theo dõi \"{folder}\" không tồn tại.");
                continue;
            }
            var filter = string.IsNullOrWhiteSpace(t.Value2) ? "*.*" : t.Value2.Trim();
            var watcher = new FileSystemWatcher(folder)
            {
                NotifyFilter = NotifyFilters.FileName,
                IncludeSubdirectories = false
            };
            foreach (var f in filter.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                watcher.Filters.Add(f);
            var captured = job;
            watcher.Created += (_, e) => OnFile(captured, e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                // Trình duyệt tải về: file .crdownload/.tmp được đổi tên thành tên thật khi xong.
                if (watcher.Filters.Any(f => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(f, e.Name ?? ""))) OnFile(captured, e.FullPath);
            };
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }

        _runningProcesses = RunningProcessNames();
        _idleFired.Clear();
        _nextMailCheck = DateTime.Now.AddSeconds(15); // kiểm tra email lần đầu ngay sau khi khởi động
        _timer.Enabled = Active(TriggerType.ProcessStarted).Any() || Active(TriggerType.ProcessExited).Any() || Active(TriggerType.Idle).Any()
                         || Active(TriggerType.EmailReceived).Any();
    }

    /// <summary>Gọi khi ScheduleApp vừa khởi động xong.</summary>
    public void OnAppStartup()
    {
        foreach (var (job, _) in Active(TriggerType.AppStartup).ToList())
            Fire(job, "khi ScheduleApp khởi động");
    }

    /// <summary>Gọi từ WndProc khi nhận WM_HOTKEY. Trả về true nếu là phím tắt của một công việc.</summary>
    public bool OnHotkey(int id)
    {
        if (!_hotkeys.TryGetValue(id, out var job)) return false;
        Fire(job, "phím tắt");
        return true;
    }

    private void Fire(Job job, string trigger, Dictionary<string, string>? vars = null)
    {
        if (job.Steps.Count(s => s.Enabled) == 0) return;
        Log.Info($"⚡ [{job.Name}] kích hoạt: {trigger}");
        _ = _runner.EnqueueAsync(job, trigger, vars == null ? null : new RunOptions { Variables = vars });
    }

    // ───────────────────────────── File mới ─────────────────────────────

    private void OnFile(Job job, string path)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["trigger.file"] = path,
            ["trigger.name"] = Path.GetFileName(path),
            ["trigger.base"] = Path.GetFileNameWithoutExtension(path),
            ["trigger.dir"] = Path.GetDirectoryName(path) ?? ""
        };
        Enqueue(job, path, $"file mới {Path.GetFileName(path)}", vars);
    }

    /// <summary>Xếp một sự kiện (file mới, email mới) vào hàng đợi riêng của công việc; mỗi sự kiện chạy flow một lần.</summary>
    private void Enqueue(Job job, string key, string trigger, Dictionary<string, string> vars)
    {
        vars["trigger.key"] = key;
        bool start;
        lock (_queueSync)
        {
            if (!_fileQueues.TryGetValue(job.Id, out var q)) _fileQueues[job.Id] = q = new();
            if (q.Any(i => i.Vars["trigger.key"].Equals(key, StringComparison.OrdinalIgnoreCase))) return;
            q.Enqueue((trigger, vars));
            start = q.Count == 1;
        }
        if (start) _ = PumpFilesAsync(job);
    }

    /// <summary>Chạy lần lượt mỗi sự kiện một lần (FlowRunner bỏ qua kích hoạt trùng khi công việc đang chạy).</summary>
    private async Task PumpFilesAsync(Job job)
    {
        while (true)
        {
            (string Trigger, Dictionary<string, string> Vars) item;
            lock (_queueSync) item = _fileQueues[job.Id].Peek();

            if (item.Vars.TryGetValue("trigger.file", out var file)) await WaitUntilReadableAsync(file);
            Log.Info($"⚡ [{job.Name}] kích hoạt: {item.Trigger}");
            await _runner.EnqueueAsync(job, item.Trigger, new RunOptions { Variables = item.Vars });

            lock (_queueSync)
            {
                var q = _fileQueues[job.Id];
                q.Dequeue();
                if (q.Count == 0) return;
            }
        }
    }

    /// <summary>Chờ file được ghi xong (không còn bị khóa), tối đa 60 giây.</summary>
    private static async Task WaitUntilReadableAsync(string path)
    {
        var sw = Stopwatch.StartNew();
        await Task.Delay(1000);
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return;
            }
            catch (FileNotFoundException) { return; }
            catch (IOException) { await Task.Delay(1000); }
            catch (UnauthorizedAccessException) { return; }
        }
    }

    // ───────────────────────────── Tiến trình & máy rảnh ─────────────────────────────

    private void Poll()
    {
        _tick++;
        if (_tick % 2 == 0 && (Active(TriggerType.ProcessStarted).Any() || Active(TriggerType.ProcessExited).Any()))
        {
            var now = RunningProcessNames();
            foreach (var (job, t) in Active(TriggerType.ProcessStarted).ToList())
            {
                var name = ProcName(t.Value);
                if (now.Contains(name) && !_runningProcesses.Contains(name)) Fire(job, $"mở {name}");
            }
            foreach (var (job, t) in Active(TriggerType.ProcessExited).ToList())
            {
                var name = ProcName(t.Value);
                if (!now.Contains(name) && _runningProcesses.Contains(name)) Fire(job, $"đóng {name}");
            }
            _runningProcesses = now;
        }

        var idle = PowerHelper.IdleTime();
        foreach (var (job, t) in Active(TriggerType.Idle).ToList())
        {
            var key = (job.Id, t.Minutes);
            if (idle < TimeSpan.FromSeconds(5)) _idleFired.Remove(key);
            else if (idle >= TimeSpan.FromMinutes(Math.Max(1, t.Minutes)) && _idleFired.Add(key))
                Fire(job, $"máy rảnh {t.Minutes} phút");
        }

        if (!_mailBusy && DateTime.Now >= _nextMailCheck)
        {
            var mailTriggers = Active(TriggerType.EmailReceived).ToList();
            if (mailTriggers.Count > 0)
            {
                _nextMailCheck = DateTime.Now.AddMinutes(Math.Max(1, mailTriggers.Min(x => x.Trigger.Minutes)));
                _mailBusy = true;
                _ = CheckMailAsync(mailTriggers);
            }
        }
    }

    // ───────────────────────────── Email mới ─────────────────────────────

    /// <summary>Kiểm tra hộp thư một lần cho mọi trình kích hoạt email; mỗi email khớp chạy công việc tương ứng một lần.</summary>
    private async Task CheckMailAsync(List<(Job Job, JobTrigger Trigger)> triggers)
    {
        try
        {
            var filters = triggers.Select(x => (x.Trigger.Value, x.Trigger.Value2)).Distinct().ToList();
            var mails = await Task.Run(() => MailWatcher.FetchAsync(filters, CancellationToken.None));
            _lastMailError = "";
            foreach (var mail in mails)
            {
                var matched = triggers.Where(x => MailWatcher.Matches(mail, x.Trigger.Value, x.Trigger.Value2)).Select(x => x.Job).Distinct().ToList();
                Log.Info($"✉ Email mới: \"{mail.Subject}\" từ {mail.From}" + (mail.Attachments.Count > 0 ? $" ({mail.Attachments.Count} file đính kèm)" : ""));
                foreach (var job in matched)
                    Enqueue(job, mail.Id, $"email \"{(mail.Subject.Length > 40 ? mail.Subject[..40] + "…" : mail.Subject)}\"", mail.ToVariables());
            }
        }
        catch (Exception ex)
        {
            // Lỗi giống lần trước (mất mạng, sai mật khẩu…) chỉ ghi một lần để không làm đầy nhật ký.
            if (ex.Message != _lastMailError) Log.Warn("Không kiểm tra được email: " + ex.Message);
            _lastMailError = ex.Message;
        }
        finally
        {
            _mailBusy = false;
        }
    }

    private static string ProcName(string value)
    {
        var name = value.Trim();
        if (name.StartsWith("exe:", StringComparison.OrdinalIgnoreCase)) name = name[4..].Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>Tên các tiến trình đang chạy trong phiên đăng nhập này (người dùng khác / dịch vụ không kích hoạt công việc).</summary>
    private static HashSet<string> RunningProcessNames()
    {
        int session;
        using (var self = Process.GetCurrentProcess()) session = self.SessionId;
        var list = new List<(string Name, int Session)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { list.Add((p.ProcessName, p.SessionId)); } catch (InvalidOperationException) { }
            }
        }
        return NamesInSession(list, session);
    }

    /// <summary>Tên (không phân biệt hoa thường) của các tiến trình thuộc phiên <paramref name="session"/>.</summary>
    internal static HashSet<string> NamesInSession(IEnumerable<(string Name, int Session)> processes, int session) =>
        new(processes.Where(p => p.Session == session).Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionUnlock) return;
        var jobs = Active(TriggerType.SessionUnlock).Select(x => x.Job).Distinct().ToList();
        if (jobs.Count == 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000); // chờ màn hình desktop hiện đầy đủ
            foreach (var job in jobs) Fire(job, "mở khóa màn hình");
        });
    }

    // ───────────────────────────── Phím tắt ─────────────────────────────

    /// <summary>"Ctrl+Alt+1" → (modifiers, virtual key).</summary>
    public static (uint Modifiers, uint Vk) ParseHotkey(string text)
    {
        uint mods = 0;
        uint? vk = null;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Win32.MOD_CONTROL; break;
                case "alt": mods |= Win32.MOD_ALT; break;
                case "shift": mods |= Win32.MOD_SHIFT; break;
                case "win" or "windows": mods |= Win32.MOD_WIN; break;
                default:
                    if (vk != null) throw new FormatException("Chỉ được một phím chính (vd Ctrl+Alt+1).");
                    vk = InputSimulator.ParseKey(part);
                    break;
            }
        }
        if (vk == null) throw new FormatException("Thiếu phím chính (vd Ctrl+Alt+1).");
        if (mods == 0 && vk is not (>= 0x70 and <= 0x87)) throw new FormatException("Phím tắt cần có Ctrl/Alt/Shift/Win (trừ F1–F24).");
        return (mods, vk.Value);
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _timer.Dispose();
        foreach (var id in _hotkeys.Keys) _host.UnregisterHotkey(id);
        foreach (var w in _watchers) w.Dispose();
    }
}
