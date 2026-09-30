namespace ScheduleApp.Services;

/// <summary>Ghi log ra file theo ngày và phát sự kiện để UI hiển thị.</summary>
public static class Log
{
    private static readonly object Sync = new();

    public static event Action<string>? Written;

    public static string LogDir => Path.Combine(JobStore.DataDir, "logs");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("LỖI", message);

    private static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss} [{level}] {message}";
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
