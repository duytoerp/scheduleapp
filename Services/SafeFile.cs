using System.Text;

namespace ScheduleApp.Services;

/// <summary>
/// Ghi / đọc file dữ liệu (settings.json, secrets.json, jobs.json…) an toàn khi mất điện, treo máy hay file bị hỏng.
/// Ghi: ra file tạm → đẩy hẳn xuống đĩa → thay file chính, bản trước giữ lại thành "&lt;file&gt;.bak".
/// Đọc: file chính hỏng → chép nguyên văn sang "&lt;file&gt;.broken-yyyyMMdd-HHmmss" (không bao giờ xóa) → dùng bản .bak nếu còn tốt
/// → báo người dùng qua <see cref="DataIssues"/>.
/// </summary>
internal static class SafeFile
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly object Sync = new();

    /// <summary>File hỏng chưa chép ra bản .broken-… được (đang bị chương trình khác giữ…) — chưa chép được thì không ghi đè lên nó.</summary>
    private static readonly HashSet<string> NotPreserved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kết quả đọc: nội dung (null = không có bản nào dùng được, bên gọi dùng mặc định) và file đã đọc (file chính hoặc .bak).</summary>
    public sealed record Loaded<T>(T? Value, string? Source) where T : class;

    public static string BackupPath(string path) => path + ".bak";

    /// <summary>Ghi chuỗi (UTF-8): file tạm → đẩy xuống đĩa → thay file đích, bản trước thành "&lt;file&gt;.bak".</summary>
    public static void WriteAllText(string path, string contents) => Write(path, Utf8.GetBytes(contents), keepBackup: true);

    /// <summary>
    /// Đọc file dữ liệu bằng <paramref name="parse"/> (ném lỗi hoặc trả null = nội dung hỏng). File chính hỏng / không đọc được →
    /// giữ bản sao .broken-…, thử bản .bak (đọc được thì đưa về làm file chính) và báo người dùng. Mất file chính nhưng còn .bak
    /// (mất điện đúng lúc thay file) → cũng khôi phục từ .bak. Chưa có file nào → Value = null.
    /// </summary>
    /// <param name="what">Nội dung file cho thông báo, vd "cài đặt".</param>
    /// <param name="fallback">Điều xảy ra khi không còn bản nào dùng được, vd "đang dùng cài đặt mặc định".</param>
    public static Loaded<T> Load<T>(string path, string what, string fallback, Func<string, T?> parse) where T : class
    {
        path = Path.GetFullPath(path);
        var bak = BackupPath(path);
        bool exists = File.Exists(path);
        if (!exists && !File.Exists(bak)) return new(null, null);
        string problem = "không còn (có thể do mất điện / tắt máy đúng lúc đang lưu)";
        if (exists)
        {
            try
            {
                return new(Parse(path, parse).Value, path);
            }
            catch (Exception ex)
            {
                problem = Reason(ex);
            }
        }

        var broken = exists ? PreserveBroken(path) : null;
        T? value = null;
        string? good = null;
        if (File.Exists(bak))
        {
            try
            {
                (value, good) = Parse(bak, parse);
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
            (value != null ? $"Đã khôi phục từ bản lưu trước ({name}.bak)." : $"Không có bản lưu trước dùng được — {fallback}.") +
            (!exists ? ""
                : broken != null ? $" Bản hỏng được giữ nguyên ở: {broken}"
                : " Chưa chép được bản hỏng (file đang bị chương trình khác giữ?) — ScheduleApp sẽ không ghi đè lên nó."));
        return new(value, value != null ? bak : null);
    }

    /// <summary>
    /// Chép nguyên văn file hỏng sang "&lt;file&gt;.broken-yyyyMMdd-HHmmss" (không xóa file gốc, không ghi đè bản sao cũ; đã có bản sao
    /// cùng nội dung thì dùng lại). Trả về đường dẫn bản sao; null nếu chưa chép được — khi đó file gốc không bị ghi đè cho tới khi chép được.
    /// </summary>
    public static string? PreserveBroken(string path)
    {
        path = Path.GetFullPath(path);
        lock (Sync)
        {
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
                    using var fs = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    fs.Write(bytes);
                    fs.Flush(flushToDisk: true);
                }
                NotPreserved.Remove(path);
                return copy;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                NotPreserved.Add(path);
                return null;
            }
        }
    }

    private static void Write(string path, byte[] bytes, bool keepBackup)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (Sync)
        {
            if (NotPreserved.Contains(path))
            {
                if (File.Exists(path) && PreserveBroken(path) == null)
                    throw new IOException($"Chưa sao lưu được file hỏng {Path.GetFileName(path)} nên chưa ghi đè lên nó.");
                NotPreserved.Remove(path);
            }
            var tmp = path + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes);
                    fs.Flush(flushToDisk: true); // nằm hẳn trên đĩa trước khi đổi tên — mất điện không để lại file rỗng
                }
                if (keepBackup && File.Exists(path)) Replace(tmp, path);
                else File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
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

    /// <summary>Đưa nội dung bản .bak về làm file chính — không đẩy file chính hỏng vào .bak (nó đã có bản sao .broken-…).</summary>
    private static void Restore(string path, string text)
    {
        try
        {
            Write(path, Utf8.GetBytes(text), keepBackup: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Chưa ghi lại được {Path.GetFileName(path)} từ bản .bak: {ex.Message}");
        }
    }

    private static (T Value, string Text) Parse<T>(string path, Func<string, T?> parse) where T : class
    {
        var text = ReadAllText(path);
        return (parse(text) ?? throw new InvalidDataException("file không có dữ liệu"), text);
    }

    /// <summary>Đọc file; đang bị chương trình khác giữ (phần mềm diệt virus, đồng bộ đám mây…) thì thử lại vài lần.</summary>
    private static string ReadAllText(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (attempt < 3 && File.Exists(path)) { Thread.Sleep(150); }
        }
    }

    /// <summary>Vì sao file không dùng được: "bị hỏng (…)" hoặc "không đọc được (…)".</summary>
    private static string Reason(Exception ex)
    {
        var detail = ex.Message.ReplaceLineEndings(" ").TrimEnd('.', ' ');
        if (detail.Length > 200) detail = detail[..200] + "…";
        return ex is IOException or UnauthorizedAccessException ? $"không đọc được ({detail})" : $"bị hỏng ({detail})";
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
}
