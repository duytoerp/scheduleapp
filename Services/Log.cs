namespace ScheduleApp.Services;

/// <summary>Ghi log ra file theo ngày và phát sự kiện để UI hiển thị.</summary>
public static class Log
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> Masks = new(StringComparer.Ordinal);

    /// <summary>Các giá trị cần che, dài trước ngắn sau (che "abcdef" trước "abc" để không còn sót "def"); null = cần sắp lại.</summary>
    private static string[]? _ordered;

    public static event Action<string>? Written;

    public static string LogDir => Path.Combine(JobStore.DataDir, "logs");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("LỖI", message);

    /// <summary>
    /// Che giá trị bí mật (mật khẩu, token, khóa API…) mỗi khi nó xuất hiện trong log, lịch sử chạy, kết quả, báo cáo, thông báo.
    /// Che cả dạng đã mã hóa URL (%40…) của nó.
    /// </summary>
    public static void Mask(string? secret) => Mask(secret, MinLength);

    /// <summary>
    /// Che giá trị chỉ ĐOÁN là bí mật (header tên kiểu …-key / …-token, biến tên kiểu matKhau / token, giá trị nhập dạng ẩn):
    /// chỉ che khi dài từ <see cref="MinGuessedLength"/> ký tự — không che "2.0" của X-Api-Key-Version hay "1" của biến passCount,
    /// vì che chuỗi ngắn sẽ thay mọi chỗ trùng trong log thành ***.
    /// </summary>
    public static void MaskGuessed(string? value) => Mask(value, MinGuessedLength);

    /// <summary>Độ dài tối thiểu của bí mật đã biết chắc (kho 🔑 Bí mật, mật khẩu / token trong ⚙ Cài đặt).</summary>
    internal const int MinLength = 3;

    /// <summary>Độ dài tối thiểu của giá trị chỉ đoán là bí mật (<see cref="MaskGuessed"/>).</summary>
    internal const int MinGuessedLength = 6;

    private static void Mask(string? secret, int minLength)
    {
        if (string.IsNullOrEmpty(secret)) return;
        secret = secret.Trim();
        if (secret.Length < minLength) return; // quá ngắn — che sẽ làm hỏng log
        lock (Sync)
        {
            bool added = Masks.Add(secret);
            var escaped = Uri.EscapeDataString(secret);
            if (escaped != secret) added |= Masks.Add(escaped);
            if (added) _ordered = null;
        }
    }

    /// <summary>
    /// Thay mọi giá trị bí mật đã đăng ký bằng "***". Gọi TRƯỚC khi cắt ngắn chuỗi — cắt trước thì nửa bí mật còn lại không khớp để che.
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        foreach (var m in Snapshot())
            if (text.Contains(m, StringComparison.Ordinal)) text = text.Replace(m, "***");
        return text;
    }

    /// <summary>Các giá trị đang được che (dài trước) — để nơi khác thay bằng chữ giữ chỗ riêng (vd khi gửi flow cho AI).</summary>
    internal static string[] Snapshot()
    {
        lock (Sync) return _ordered ??= [.. Masks.OrderByDescending(m => m.Length)];
    }

    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss} [{level}] {Redact(message)}";
        lock (Sync)
        {
            try
            {
                AppendLine(LogDir, now, line);
            }
            catch
            {
                // Không để lỗi ghi log làm hỏng flow.
            }
        }
        Written?.Invoke(line);
    }

    // ───────────────────────────── Giới hạn dung lượng & thời gian giữ ─────────────────────────────

    /// <summary>Mỗi file nhật ký tối đa chừng này byte; quá thì ghi tiếp sang yyyy-MM-dd-2.log, -3.log…</summary>
    internal static long MaxFileBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>Số ngày giữ nhật ký và file crash-….txt.</summary>
    public const int KeepDays = 30;

    private static string? _file;
    private static long _fileSize;

    /// <summary>
    /// Ghi một dòng vào file của ngày <paramref name="now"/>, sang phần tiếp theo khi file đầy.
    /// Mở – ghi – đóng mỗi dòng (không đệm), nên app bị tắt đột ngột cũng không mất dòng đã ghi.
    /// </summary>
    internal static void AppendLine(string dir, DateTime now, string line)
    {
        var text = line + Environment.NewLine;
        var day = now.ToString("yyyy-MM-dd");
        lock (Sync)
        {
            if (_file == null || Path.GetDirectoryName(_file) != dir || !Path.GetFileName(_file).StartsWith(day, StringComparison.Ordinal) ||
                _fileSize >= MaxFileBytes)
            {
                Directory.CreateDirectory(dir);
                int part = 1;
                string Name(int p) => Path.Combine(dir, p == 1 ? $"{day}.log" : $"{day}-{p}.log");
                while (File.Exists(Name(part)) && new FileInfo(Name(part)).Length >= MaxFileBytes) part++;
                _file = Name(part);
                _fileSize = File.Exists(_file) ? new FileInfo(_file).Length : 0;
            }
            try
            {
                File.AppendAllText(_file, text);
            }
            catch (DirectoryNotFoundException)
            {
                // Thư mục logs bị xóa trong lúc app đang chạy → tạo lại ngay, không mất nhật ký tới hết ngày.
                Directory.CreateDirectory(dir);
                _fileSize = 0;
                File.AppendAllText(_file, text);
            }
            _fileSize += System.Text.Encoding.UTF8.GetByteCount(text);
        }
    }

    /// <summary>
    /// File nhật ký có các dòng ghi từ lúc <paramref name="at"/>: trong các phần yyyy-MM-dd.log, -2.log… của ngày đó,
    /// phần đầu tiên được ghi tới sau <paramref name="at"/> (các phần ghi nối tiếp nhau); không có thì phần cuối; null nếu ngày đó không có nhật ký.
    /// </summary>
    internal static string? FileFor(string dir, DateTime at)
    {
        var day = at.ToString("yyyy-MM-dd");
        if (!Directory.Exists(dir)) return null;
        var parts = new List<(int Part, string File)>();
        foreach (var file in Directory.GetFiles(dir, day + "*.log"))
        {
            var rest = Path.GetFileNameWithoutExtension(file)[day.Length..];
            if (rest.Length == 0) parts.Add((1, file));
            else if (rest[0] == '-' && int.TryParse(rest[1..], System.Globalization.NumberStyles.None, null, out int p) && p >= 2) parts.Add((p, file));
        }
        var ordered = parts.OrderBy(p => p.Part).Select(p => p.File).ToList();
        return ordered.FirstOrDefault(f => File.GetLastWriteTime(f) >= at) ?? ordered.LastOrDefault();
    }

    /// <summary>
    /// Xóa nhật ký (yyyy-MM-dd*.log) và file crash-yyyyMMdd-….txt cũ hơn <paramref name="keepDays"/> ngày tính tới <paramref name="today"/>
    /// (ngày lấy từ tên file). Không đụng tới file khác và thư mục con (ảnh lỗi). Trả về số file đã xóa.
    /// </summary>
    internal static int Cleanup(string dir, DateTime today, int keepDays = KeepDays)
    {
        if (keepDays <= 0 || !Directory.Exists(dir)) return 0;
        var cutoff = today.Date.AddDays(-keepDays);
        int deleted = 0;
        foreach (var file in Directory.GetFiles(dir))
        {
            if (FileDate(Path.GetFileName(file)) is not DateTime date || date >= cutoff) continue;
            lock (Sync)
            {
                if (_file != null && string.Equals(_file, file, StringComparison.OrdinalIgnoreCase)) continue; // file đang ghi
                try { File.Delete(file); deleted++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        return deleted;
    }

    /// <summary>Ngày trong tên file nhật ký ("yyyy-MM-dd….log") hoặc file crash ("crash-yyyyMMdd-….txt"); null nếu không phải hai loại đó.</summary>
    private static DateTime? FileDate(string name)
    {
        const System.Globalization.DateTimeStyles none = System.Globalization.DateTimeStyles.None;
        if (name.Length >= 14 && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) &&
            DateTime.TryParseExact(name[..10], "yyyy-MM-dd", null, none, out var day))
            return day;
        if (name.Length >= 14 && name.StartsWith("crash-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
            DateTime.TryParseExact(name[6..14], "yyyyMMdd", null, none, out var crash))
            return crash;
        return null;
    }
}
