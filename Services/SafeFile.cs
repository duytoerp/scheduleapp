using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ScheduleApp.Services;

/// <summary>
/// Ghi / đọc file dữ liệu (settings.json, secrets.json, jobs.json…) an toàn khi mất điện, treo máy hay file bị hỏng.
/// Ghi: ra file tạm → đẩy hẳn xuống đĩa → thay file chính, bản trước giữ lại thành "&lt;file&gt;.bak".
/// Đọc: file chính hỏng → chép nguyên văn sang "&lt;file&gt;.broken-yyyyMMdd-HHmmss" (không bao giờ xóa) → dùng bản .bak nếu còn tốt
/// → báo người dùng qua <see cref="DataIssues"/>. File chỉ đang bị chương trình khác giữ (không phải hỏng) → dùng tạm bản .bak
/// nhưng cả phiên không ghi đè lên file đó. Đọc / ghi cùng một file được khóa giữa các tiến trình ScheduleApp (ứng dụng ở khay
/// và lệnh --test chạy cùng lúc).
/// </summary>
internal static partial class SafeFile
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly object Sync = new();

    /// <summary>File hỏng chưa chép ra bản .broken-… được (đang bị chương trình khác giữ…) — chưa chép được thì không ghi đè lên nó.</summary>
    private static readonly HashSet<string> NotPreserved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// File chưa đọc được lúc mở (đang bị chương trình khác giữ, không có quyền) — dữ liệu đang dùng có thể cũ hơn file đó, nên cả phiên
    /// không ghi đè lên nó (<see cref="Load{T}"/> đọc lại được thì bỏ chặn).
    /// </summary>
    private static readonly HashSet<string> ReadOnlyFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File đang bị giữ thì thử đọc lại trong chừng này (phần mềm diệt virus, đồng bộ đám mây… thường nhả rất nhanh).</summary>
    internal static TimeSpan ReadRetry { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Chờ tiến trình ScheduleApp khác đọc / ghi xong cùng file tối đa chừng này.</summary>
    internal static TimeSpan LockWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Kiểm thử: gọi ngay sau khi tạo bản sao .broken-… (trước khi ghi nội dung) — ném lỗi để giả lập đĩa đầy giữa chừng.</summary>
    internal static Action<string>? AfterBrokenCopyCreated { get; set; }

    /// <summary>Kết quả đọc: nội dung (null = không có bản nào dùng được, bên gọi dùng mặc định) và file đã đọc (file chính, file tạm hoặc .bak).</summary>
    public sealed record Loaded<T>(T? Value, string? Source) where T : class;

    public static string BackupPath(string path) => path + ".bak";

    /// <summary>Ghi chuỗi (UTF-8): file tạm → đẩy xuống đĩa → thay file đích, bản trước thành "&lt;file&gt;.bak".</summary>
    public static void WriteAllText(string path, string contents) => Write(path, Utf8.GetBytes(contents), keepBackup: true);

    /// <summary>File đang bị chặn ghi trong phiên này vì lúc mở chưa đọc được (xem <see cref="ReadOnlyFiles"/>).</summary>
    public static bool IsReadOnly(string path)
    {
        path = Path.GetFullPath(path);
        lock (Sync) return ReadOnlyFiles.Contains(path);
    }

    /// <summary>
    /// Đọc file dữ liệu bằng <paramref name="parse"/> (ném lỗi hoặc trả null = nội dung hỏng). File chính hỏng →
    /// giữ bản sao .broken-…, thử bản .bak (đọc được thì đưa về làm file chính) và báo người dùng. Mất file chính (mất điện đúng lúc
    /// thay file) → dùng file tạm đã ghi xong nếu có (mới hơn .bak), không thì .bak. File chính / .bak chỉ đang bị chương trình khác
    /// giữ → dùng tạm những gì đọc được nhưng không ghi đè gì cả phiên. Chưa có file nào → Value = null.
    /// </summary>
    /// <param name="what">Nội dung file cho thông báo, vd "cài đặt".</param>
    /// <param name="fallback">Điều xảy ra khi không còn bản nào dùng được, vd "đang dùng cài đặt mặc định".</param>
    public static Loaded<T> Load<T>(string path, string what, string fallback, Func<string, T?> parse) where T : class
    {
        path = Path.GetFullPath(path);
        using var locked = FileLock.Acquire(path); // null = tiến trình khác giữ quá lâu — vẫn đọc (đọc không làm hỏng gì)
        lock (Sync) ReadOnlyFiles.Remove(path);
        var bak = BackupPath(path);
        bool exists = File.Exists(path);
        List<string> temps = exists ? [] : TempFiles(path);
        if (!exists && !File.Exists(bak) && temps.Count == 0) return new(null, null);
        string problem = "không còn (có thể do mất điện / tắt máy đúng lúc đang lưu)";
        if (exists)
        {
            try
            {
                return new(Parse(path, parse).Value, path);
            }
            catch (Exception ex) when (IsUnavailable(ex))
            {
                return LoadReadOnly(path, what, fallback, parse, Reason(ex));
            }
            catch (Exception ex)
            {
                problem = Reason(ex);
            }
        }

        var broken = exists ? PreserveBroken(path) : null;
        T? value = null;
        string? good = null, source = null;
        // File tạm đã ghi xong mà chưa kịp thay file chính: mới hơn bản .bak.
        foreach (var tmp in temps)
        {
            try
            {
                (value, good) = Parse(tmp, parse);
                source = tmp;
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"File tạm {Path.GetFileName(tmp)} không dùng được — {Reason(ex)}.");
            }
        }
        if (good == null && File.Exists(bak))
        {
            try
            {
                (value, good) = Parse(bak, parse);
                source = bak;
            }
            catch (Exception ex) when (IsUnavailable(ex))
            {
                // Bản .bak có thể là bản tốt duy nhất: không ghi gì để lần mở sau còn đọc được nó.
                return Unavailable<T>(path, what, fallback, $"{problem}; bản lưu trước {Path.GetFileName(bak)} {Reason(ex)}", broken, exists);
            }
            catch (Exception ex)
            {
                PreserveBroken(bak);
                Log.Warn($"Bản lưu trước {Path.GetFileName(bak)} {(exists ? "cũng " : "")}không dùng được — {Reason(ex)}.");
            }
        }
        if (good != null) Restore(path, good);

        var name = Path.GetFileName(path);
        DataIssues.Report($"{name} ({what}) {problem}. " +
            (value == null ? $"Không có bản lưu trước dùng được — {fallback}."
             : source == bak ? $"Đã khôi phục từ bản lưu trước ({name}.bak)."
             : $"Đã khôi phục từ bản vừa lưu ({Path.GetFileName(source)}).") +
            (!exists ? ""
                : broken != null ? $" Bản hỏng được giữ nguyên ở: {broken}"
                : " Chưa chép được bản hỏng (file đang bị chương trình khác giữ?) — ScheduleApp sẽ không ghi đè lên nó."));
        return new(value, value != null ? source : null);
    }

    /// <summary>File chính đang bị giữ (không phải hỏng): dùng tạm bản .bak nếu đọc được, không ghi đè file chính cả phiên.</summary>
    private static Loaded<T> LoadReadOnly<T>(string path, string what, string fallback, Func<string, T?> parse, string problem) where T : class
    {
        var bak = BackupPath(path);
        T? value = null;
        if (File.Exists(bak))
        {
            try
            {
                value = Parse(bak, parse).Value;
            }
            catch (Exception ex)
            {
                Log.Warn($"Bản lưu trước {Path.GetFileName(bak)} cũng không dùng được — {Reason(ex)}.");
            }
        }
        if (value == null) return Unavailable<T>(path, what, fallback, problem, null, exists: false);
        lock (Sync) ReadOnlyFiles.Add(path);
        var name = Path.GetFileName(path);
        DataIssues.Report($"{name} ({what}) {problem}. Đang dùng tạm bản lưu trước ({name}.bak) — có thể cũ hơn. {ReadOnlyAdvice}");
        return new(value, bak);
    }

    /// <summary>Không đọc được bản nào vì file đang bị giữ: dùng mặc định, không ghi đè file chính cả phiên.</summary>
    private static Loaded<T> Unavailable<T>(string path, string what, string fallback, string problem, string? broken, bool exists) where T : class
    {
        lock (Sync) ReadOnlyFiles.Add(path);
        DataIssues.Report($"{Path.GetFileName(path)} ({what}) {problem}. Tạm thời {fallback}. {ReadOnlyAdvice}" +
                          (exists && broken != null ? $" Bản hỏng được giữ nguyên ở: {broken}" : ""));
        return new(null, null);
    }

    private const string ReadOnlyAdvice =
        "Thay đổi trong lần mở này sẽ không được lưu vào file đó (để không ghi đè dữ liệu có thể mới hơn). " +
        "Hãy đóng chương trình đang giữ file (phần mềm diệt virus, đồng bộ đám mây…) rồi mở lại ScheduleApp.";

    /// <summary>
    /// Chép nguyên văn file hỏng sang "&lt;file&gt;.broken-yyyyMMdd-HHmmss" (không xóa file gốc, không ghi đè bản sao cũ; đã có bản sao
    /// cùng nội dung thì dùng lại). Trả về đường dẫn bản sao; null nếu chưa chép được — khi đó file gốc không bị ghi đè cho tới khi chép được.
    /// </summary>
    public static string? PreserveBroken(string path)
    {
        path = Path.GetFullPath(path);
        lock (Sync)
        {
            string? partial = null;
            try
            {
                var bytes = File.ReadAllBytes(path);
                var copy = Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".broken-*")
                    .FirstOrDefault(f => new FileInfo(f).Length == bytes.Length && File.ReadAllBytes(f).AsSpan().SequenceEqual(bytes));
                if (copy == null)
                {
                    var stamp = $"{path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
                    copy = stamp;
                    for (int i = 2; File.Exists(copy); i++) copy = $"{stamp}-{i}";
                    using (var fs = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        partial = copy;
                        AfterBrokenCopyCreated?.Invoke(copy);
                        fs.Write(bytes);
                        fs.Flush(flushToDisk: true);
                    }
                    partial = null;
                }
                NotPreserved.Remove(path);
                return copy;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Chép dở (đĩa đầy…): bỏ bản sao thiếu — không để nó trông như bản gốc đã được giữ.
                if (partial != null)
                    try { File.Delete(partial); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                NotPreserved.Add(path);
                return null;
            }
        }
    }

    private static void Write(string path, byte[] bytes, bool keepBackup)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var locked = FileLock.Acquire(path)
            ?? throw new IOException($"{Path.GetFileName(path)} đang được một tiến trình ScheduleApp khác ghi — chưa lưu được, hãy thử lại.");
        lock (Sync)
        {
            if (ReadOnlyFiles.Contains(path))
                throw new IOException($"Không lưu vào {Path.GetFileName(path)}: lúc mở ScheduleApp file này đang bị chương trình khác giữ nên chưa đọc được. " +
                                      "Hãy đóng chương trình đó rồi mở lại ScheduleApp.");
            if (NotPreserved.Contains(path))
            {
                if (File.Exists(path) && PreserveBroken(path) == null)
                    throw new IOException($"Chưa sao lưu được file hỏng {Path.GetFileName(path)} nên chưa ghi đè lên nó.");
                NotPreserved.Remove(path);
            }
            // Bản .bak không đọc được lúc mở mà chưa chép ra .broken-… được: không đẩy nó đi bằng bản mới — chỉ ghi file chính.
            var bak = BackupPath(path);
            bool rotate = keepBackup;
            if (NotPreserved.Contains(bak))
            {
                if (!File.Exists(bak)) NotPreserved.Remove(bak);
                else if (PreserveBroken(bak) == null) rotate = false;
            }
            var tmp = $"{path}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes);
                    fs.Flush(flushToDisk: true); // nằm hẳn trên đĩa trước khi đổi tên — mất điện không để lại file rỗng
                }
                if (rotate && File.Exists(path)) Replace(tmp, path);
                else File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
            // File tạm còn sót của lần ghi bị ngắt (mất điện…) — file chính vừa ghi xong đã mới hơn.
            foreach (var old in TempFiles(path))
                try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Thay file chính bằng file tạm, bản cũ thành .bak. Không dùng được ReplaceFile (ổ mạng, FAT32, .bak đang bị giữ…) → chép bản cũ
    /// ra .bak nếu được rồi đổi tên file tạm đè lên — lưu được file chính quan trọng hơn làm mới .bak.
    /// </summary>
    private static void Replace(string tmp, string path)
    {
        var bak = BackupPath(path);
        try
        {
            File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(tmp))
        {
            try
            {
                if (File.Exists(path)) File.Copy(path, bak, overwrite: true);
            }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException) { }
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>Đưa nội dung bản .bak / file tạm về làm file chính — không đẩy file chính hỏng vào .bak (nó đã có bản sao .broken-…).</summary>
    private static void Restore(string path, string text)
    {
        try
        {
            Write(path, Utf8.GetBytes(text), keepBackup: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Chưa ghi lại được {Path.GetFileName(path)} từ bản lưu trước: {ex.Message}");
        }
    }

    /// <summary>File tạm của các lần ghi "&lt;file&gt;.xxxxxxxx.tmp" (và tên cũ "&lt;file&gt;.tmp"), mới nhất trước.</summary>
    private static List<string> TempFiles(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.EnumerateFiles(dir, name + ".*")
            .Where(f => Path.GetFileName(f) is var n && n.Length > name.Length && TempSuffix().IsMatch(n[name.Length..]))
            .OrderByDescending(File.GetLastWriteTimeUtc)];
    }

    [GeneratedRegex(@"^\.([0-9a-f]{8}\.)?tmp$", RegexOptions.IgnoreCase)]
    private static partial Regex TempSuffix();

    private static (T Value, string Text) Parse<T>(string path, Func<string, T?> parse) where T : class
    {
        var text = ReadAllText(path);
        return (parse(text) ?? throw new InvalidDataException("file không có dữ liệu"), text);
    }

    /// <summary>Đọc file; đang bị chương trình khác giữ (phần mềm diệt virus, đồng bộ đám mây…) thì thử lại trong <see cref="ReadRetry"/>.</summary>
    private static string ReadAllText(string path)
    {
        var until = DateTime.UtcNow + ReadRetry;
        while (true)
        {
            try { return File.ReadAllText(path); }
            catch (Exception ex) when (IsUnavailable(ex) && DateTime.UtcNow < until && File.Exists(path)) { Thread.Sleep(150); }
        }
    }

    /// <summary>Lỗi do không mở được file (đang bị giữ, không có quyền) — khác với nội dung hỏng.</summary>
    private static bool IsUnavailable(Exception ex) =>
        ex is UnauthorizedAccessException or (IOException and not (FileNotFoundException or DirectoryNotFoundException));

    /// <summary>Vì sao file không dùng được: "bị hỏng (…)" hoặc "không đọc được (…)".</summary>
    private static string Reason(Exception ex)
    {
        var detail = ex.Message.ReplaceLineEndings(" ").TrimEnd('.', ' ');
        if (detail.Length > 200) detail = detail[..200] + "…";
        return ex is IOException or UnauthorizedAccessException ? $"không đọc được ({detail})" : $"bị hỏng ({detail})";
    }

    /// <summary>
    /// Khóa liên tiến trình cho một file (theo đường dẫn đầy đủ và tài khoản Windows): ScheduleApp ở khay và lệnh --test cùng lưu
    /// một file thì lần lượt từng bên. Cùng luồng lấy lại được (đọc → khôi phục → ghi).
    /// </summary>
    internal sealed class FileLock : IDisposable
    {
        private Mutex? _mutex;

        private FileLock(Mutex? mutex) => _mutex = mutex;

        internal static string Name(string path) =>
            @"Local\ScheduleApp-" + Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(Environment.UserName + "|" + Path.GetFullPath(path).ToUpperInvariant())))[..32];

        /// <summary>Null nếu tiến trình khác giữ quá <see cref="LockWait"/>. Không tạo được khóa (quyền…) → vẫn cho đọc / ghi như trước.</summary>
        public static FileLock? Acquire(string path)
        {
            Mutex mutex;
            try
            {
                mutex = new Mutex(false, Name(path));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
            {
                return new FileLock(null);
            }
            try
            {
                if (mutex.WaitOne(LockWait)) return new FileLock(mutex);
            }
            catch (AbandonedMutexException)
            {
                return new FileLock(mutex); // tiến trình trước bị tắt giữa chừng — khóa đã thuộc về luồng này
            }
            mutex.Dispose();
            return null;
        }

        public void Dispose()
        {
            if (_mutex == null) return;
            _mutex.ReleaseMutex();
            _mutex.Dispose();
            _mutex = null;
        }
    }
}

/// <summary>
/// Sự cố với file dữ liệu phát hiện lúc đọc (file hỏng, đã khôi phục từ .bak, công việc không đọc được…): ghi nhật ký ngay;
/// màn hình chính báo người dùng một lần (<see cref="Take"/>), chế độ dòng lệnh in ra console.
/// </summary>
internal static class DataIssues
{
    private static readonly object Sync = new();
    private static readonly List<string> Pending = [];

    /// <summary>Có sự cố mới (có thể phát từ luồng nền).</summary>
    public static event Action? Reported;

    public static bool HasPending
    {
        get
        {
            lock (Sync) return Pending.Count > 0;
        }
    }

    public static void Report(string message)
    {
        Log.Error(message);
        lock (Sync) Pending.Add(message);
        Reported?.Invoke();
    }

    /// <summary>Lấy các sự cố chưa báo và xóa khỏi danh sách chờ.</summary>
    public static List<string> Take()
    {
        lock (Sync)
        {
            var list = Pending.ToList();
            Pending.Clear();
            return list;
        }
    }

    /// <summary>
    /// Nội dung hộp thoại: tối đa <paramref name="max"/> mục, mỗi mục rút gọn còn <paramref name="maxChars"/> ký tự, còn lại
    /// "… và N mục khác" — chi tiết đầy đủ đã có trong nhật ký.
    /// </summary>
    public static string Summarize(IReadOnlyList<string> issues, int max = 5, int maxChars = 400)
    {
        var lines = issues.Take(max).Select(i => "• " + (i.Length > maxChars ? i[..maxChars].TrimEnd() + "…" : i)).ToList();
        if (issues.Count > max) lines.Add($"… và {issues.Count - max} mục khác — xem nhật ký.");
        return string.Join("\n\n", lines);
    }
}
