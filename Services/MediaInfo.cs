using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Threading;
using ScheduleApp.Models;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.Services;

/// <summary>
/// Thời lượng video / nhạc: đọc metadata của Windows (nhanh, không mở file), không có thì mở thử bằng trình phát WPF (không phát).
/// Kết quả được nhớ theo file (kích thước + thời điểm sửa) để tính lại danh sách phát nhanh.
/// </summary>
internal static partial class MediaInfo
{
    /// <summary>Dòng dùng biến chỉ biết giá trị khi chạy.</summary>
    public const string VariableProblem = "dùng biến — tính khi chạy";

    /// <summary>Biến có sẵn đổi giá trị theo từng lần chạy: ngày giờ, số ngẫu nhiên, guid.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(random(\s*:.*)?|guid|(today|now|time|yesterday|tomorrow)(\s*[-+:].*)?)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex PerRunVariable();

    /// <summary>Mở thử bằng trình phát quá chừng này mà chưa xong thì coi như không rõ thời lượng.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private static readonly ConcurrentDictionary<string, (long Size, DateTime Modified, TimeSpan? Duration)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Thời lượng của file; null = không có file / không đọc được.</summary>
    public static async Task<TimeSpan?> GetDurationAsync(string path, CancellationToken ct = default)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
        if (Cache.TryGetValue(info.FullName, out var cached) && cached.Size == info.Length && cached.Modified == info.LastWriteTimeUtc)
            return cached.Duration;

        var duration = await FromMetadataAsync(info.FullName).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // Chỉ mở thử bằng trình phát với file có đuôi video / nhạc (file khác sẽ chờ tới hết giờ mới biết).
        if (duration == null && MediaPlayback.Extensions.Contains(info.Extension))
            duration = await FromPlayerAsync(info.FullName).ConfigureAwait(false);
        Cache[info.FullName] = (info.Length, info.LastWriteTimeUtc, duration);
        return duration;
    }

    /// <summary>Thuộc tính System.Media.Duration của Windows (đơn vị 100 ns).</summary>
    internal static async Task<TimeSpan?> FromMetadataAsync(string path)
    {
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
            var props = await file.Properties.RetrievePropertiesAsync(["System.Media.Duration"]).AsTask().ConfigureAwait(false);
            if (props.TryGetValue("System.Media.Duration", out var value) && value is ulong ticks && ticks > 0)
                return TimeSpan.FromTicks((long)Math.Min(ticks, long.MaxValue));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaInfo metadata {path}: {ex.Message}");
        }
        return null;
    }

    /// <summary>Mở file bằng trình phát WPF (không phát) để lấy thời lượng — cho file không có metadata.</summary>
    internal static Task<TimeSpan?> FromPlayerAsync(string path)
    {
        var done = new TaskCompletionSource<TimeSpan?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            System.Windows.Media.MediaPlayer? player = null;
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                player = new System.Windows.Media.MediaPlayer { Volume = 0 };
                var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = ProbeTimeout };
                timer.Tick += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                player.MediaOpened += (_, _) =>
                {
                    if (player.NaturalDuration.HasTimeSpan && player.NaturalDuration.TimeSpan > TimeSpan.Zero)
                        done.TrySetResult(player.NaturalDuration.TimeSpan);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                };
                player.MediaFailed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
                player.Open(new Uri(path));
                timer.Start();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MediaInfo player {path}: {ex.Message}");
            }
            finally
            {
                try { player?.Close(); } catch (Exception ex) { Debug.WriteLine(ex.Message); }
                done.TrySetResult(null);
            }
        }) { IsBackground = true, Name = "ScheduleApp — đọc thời lượng" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <param name="Line">Dòng trong danh sách phát (đã thay biến nếu có).</param>
    /// <param name="Path">File sẽ phát; null = dòng không dùng được (xem <paramref name="Problem"/>).</param>
    /// <param name="Duration">Thời lượng; null = không rõ.</param>
    public sealed record Entry(string Line, string? Path, TimeSpan? Duration, string? Problem)
    {
        /// <summary>Thứ tự của dòng tạo ra mục này trong danh sách phát (0 = dòng đầu; một thư mục tạo nhiều mục cùng thứ tự).</summary>
        public int Item { get; init; }
    }

    /// <param name="Total">Tổng thời lượng các file đọc được.</param>
    /// <param name="Complete">Biết chắc tổng: mọi file sẽ phát đều đọc được thời lượng, không còn dòng dùng biến chưa biết giá trị.</param>
    public sealed record Plan(IReadOnlyList<Entry> Entries, TimeSpan Total, bool Complete)
    {
        public int FileCount => Entries.Count(e => e.Path != null);
        public int UnknownCount => Entries.Count(e => e.Path != null && e.Duration == null);
    }

    /// <summary>
    /// Phân tích danh sách phát: từng file sẽ phát (thư mục → các file trong đó theo tên), thời lượng của mỗi file và tổng.
    /// </summary>
    /// <param name="expand">Thay {{biến}} trong dòng (ném lỗi nếu biến chưa có giá trị); null = dòng có biến coi như chưa biết.</param>
    public static async Task<Plan> AnalyzeAsync(IEnumerable<string> lines, Func<string, string>? expand, CancellationToken ct = default)
    {
        var entries = new List<Entry>();
        bool complete = true;
        int item = -1;
        foreach (var raw in lines)
        {
            item++;
            var line = raw;
            List<string> files, missing;
            try
            {
                if (line.Contains("{{"))
                {
                    // Biến chỉ biết khi chạy (clipboard, bí mật, dữ liệu ngẫu nhiên…) — không đọc ở đây.
                    if (expand == null || line.Contains("{{clipboard", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("{{secret:", StringComparison.OrdinalIgnoreCase) || line.Contains("{{=", StringComparison.Ordinal))
                    {
                        entries.Add(new Entry(raw, null, null, VariableProblem) { Item = item });
                        complete = false;
                        continue;
                    }
                    try { line = expand(line); }
                    catch (InvalidOperationException)
                    {
                        entries.Add(new Entry(raw, null, null, VariableProblem) { Item = item });
                        complete = false;
                        continue;
                    }
                    // Biến đổi theo từng lần chạy ({{today}}, {{now:…}}, {{random}}…): file lúc chạy có thể khác → tổng chưa chắc.
                    if (VariableExpander.Names(raw).Any(n => PerRunVariable().IsMatch(n))) complete = false;
                }
                (files, missing) = MediaPlayback.Resolve([line]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                           or FormatException or OverflowException or System.Security.SecurityException)
            {
                // Định dạng biến sai ({{now:%}}), thư mục có mà không mở được (không có quyền, ổ mạng rớt)… → chỉ dòng này lỗi (ô đỏ).
                entries.Add(new Entry(raw, null, null, "không đọc được: " + ex.Message) { Item = item });
                complete = false;
                continue;
            }
            // File / thư mục chưa có (vd ổ mạng chưa kết nối, file tải về sau) → tổng chưa chắc.
            if (missing.Count > 0)
            {
                entries.Add(new Entry(raw, null, null, "không thấy file / thư mục") { Item = item });
                complete = false;
            }
            else if (files.Count == 0)
            {
                entries.Add(new Entry(raw, null, null, "thư mục không có video / nhạc") { Item = item });
                complete = false;
            }
            foreach (var f in files) entries.Add(new Entry(raw, f, null, null) { Item = item });
        }

        // Đọc song song (giới hạn) — thư mục nhiều file vẫn nhanh.
        using var gate = new SemaphoreSlim(4);
        var durations = await Task.WhenAll(entries.Select(async e =>
        {
            if (e.Path == null) return e;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { return e with { Duration = await GetDurationAsync(e.Path, ct).ConfigureAwait(false) }; }
            finally { gate.Release(); }
        })).ConfigureAwait(false);

        var total = TimeSpan.FromTicks(durations.Where(e => e.Duration != null).Sum(e => e.Duration!.Value.Ticks));
        if (durations.Any(e => e.Path != null && e.Duration == null)) complete = false;
        return new Plan(durations, total, complete);
    }

    /// <summary>Thay {{biến}} theo giá trị khai báo sẵn của công việc (và biến có sẵn như {{today}}, {{env:…}}).</summary>
    public static Func<string, string> ExpanderFor(IEnumerable<VariableDef> variables)
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in variables.Where(v => v.Name.Trim().Length > 0)) vars[v.Name.Trim()] = TestData.IsFormula(v.Value) ? "{{=}}" : v.Value;
        var expander = new VariableExpander(vars);
        return line =>
        {
            var result = expander.Expand(line);
            // Biến có giá trị là công thức ngẫu nhiên / lại chứa biến khác → chỉ biết khi chạy.
            return result.Contains("{{") ? throw new InvalidOperationException("Còn biến chưa biết giá trị.") : result;
        };
    }

    /// <summary>
    /// Tính và lưu tổng thời lượng (<see cref="ActionStep.MediaDurationMs"/>) cho các bước "Phát video / nhạc";
    /// trả về true nếu có bước thay đổi. Bước có biến chưa biết giá trị / file không đọc được → 0 (chưa rõ).
    /// </summary>
    public static async Task<bool> FillDurationsAsync(IEnumerable<ActionStep> steps, IEnumerable<VariableDef>? variables = null, CancellationToken ct = default)
    {
        var expand = ExpanderFor(variables ?? []);
        bool changed = false;
        foreach (var step in steps.Where(s => s.Type == StepType.PlayMedia).ToList())
        {
            var plan = await AnalyzeAsync(step.MediaLines, expand, ct).ConfigureAwait(false);
            int ms = ToMs(plan);
            if (step.MediaDurationMs == ms) continue;
            step.MediaDurationMs = ms;
            changed = true;
        }
        return changed;
    }

    /// <summary>Tổng thời lượng để lưu vào bước (ms); 0 nếu chưa biết chắc.</summary>
    public static int ToMs(Plan plan) =>
        plan.Complete && plan.FileCount > 0 ? (int)Math.Min(int.MaxValue, Math.Round(plan.Total.TotalMilliseconds)) : 0;
}
