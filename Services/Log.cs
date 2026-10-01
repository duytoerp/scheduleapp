namespace ScheduleApp.Services;

/// <summary>Ghi log ra file theo ngày và phát sự kiện để UI hiển thị.</summary>
public static class Log
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> Masks = new(StringComparer.Ordinal);

    public static event Action<string>? Written;

    public static string LogDir => Path.Combine(JobStore.DataDir, "logs");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("LỖI", message);

    /// <summary>Che giá trị bí mật (mật khẩu…) mỗi khi nó xuất hiện trong log.</summary>
    public static void Mask(string secret)
    {
        if (secret.Length < 3) return; // quá ngắn — che sẽ làm hỏng log
        lock (Sync) Masks.Add(secret);
    }

    public static string Redact(string text)
    {
        lock (Sync)
        {
            foreach (var m in Masks)
                if (text.Contains(m, StringComparison.Ordinal)) text = text.Replace(m, "***");
        }
        return text;
    }

    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss} [{level}] {Redact(message)}";
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(Path.Combine(LogDir, $"{now:yyyy-MM-dd}.log"), line + Environment.NewLine);
            }
            catch
            {
                // Không để lỗi ghi log làm hỏng flow.
            }
        }
        Written?.Invoke(line);
    }
}
