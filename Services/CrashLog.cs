using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ScheduleApp.Services;

/// <summary>
/// Ghi lỗi chưa được xử lý ra "logs\crash-yyyyMMdd-HHmmss.txt": phiên bản, Windows, chi tiết lỗi (đã che bí mật) — để gửi cho người hỗ trợ.
/// Lỗi dồn dập (cách nhau dưới <see cref="Burst"/>) được ghi tiếp vào cùng một file, tối đa ~1 MB, thay vì tạo hàng loạt file.
/// </summary>
internal static partial class CrashLog
{
    /// <summary>Lỗi đến sau lỗi trước ít hơn khoảng này được coi là lặp lại: ghi chung file, không hiện thêm hộp thoại.</summary>
    public static readonly TimeSpan Burst = TimeSpan.FromSeconds(10);

    private const long MaxFileBytes = 1_000_000;
    private static readonly object Sync = new();
    private static string? _lastFile;
    private static DateTime _lastWrite;

    /// <summary>Ghi một dòng nhật ký + file crash (ghi xong mới trả về). Trả về đường dẫn file, null nếu không ghi được. Không ném lỗi.</summary>
    public static string? Write(Exception ex, string source)
    {
        try { Log.Error($"Lỗi chưa được xử lý ({source}): {Summary(ex)}"); }
        catch (Exception) { /* nơi nhận nhật ký (giao diện) có thể đang đóng — vẫn ghi file crash */ }
        try
        {
            var now = DateTime.Now;
            lock (Sync)
            {
                Directory.CreateDirectory(Log.LogDir);
                var path = _lastFile != null && now - _lastWrite < Burst && File.Exists(_lastFile)
                    ? _lastFile
                    : Path.Combine(Log.LogDir, $"crash-{now:yyyyMMdd-HHmmss}.txt");
                (_lastFile, _lastWrite) = (path, now);
                if (!File.Exists(path) || new FileInfo(path).Length < MaxFileBytes)
                    File.AppendAllText(path, Format(ex, source, now));
                return path;
            }
        }
        catch (Exception)
        {
            return null; // đĩa đầy / không có quyền… — đã có dòng nhật ký, không để việc ghi lỗi gây thêm lỗi
        }
    }

    /// <summary>Một dòng: loại lỗi + nội dung (đã che bí mật, rút gọn) cho nhật ký / hộp thoại.</summary>
    public static string Summary(Exception ex)
    {
        var e = ex is AggregateException { InnerExceptions.Count: 1 } a ? a.InnerExceptions[0] : ex;
        var text = Scrub($"{e.GetType().Name}: {e.Message}").ReplaceLineEndings(" ");
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>Che bí mật: giá trị đã biết (<see cref="Log.Redact"/>) và chuỗi trông như token (token bot Telegram trong URL, "Bearer …", khóa API Claude).</summary>
    internal static string Scrub(string text) => TokenLike().Replace(Log.Redact(text), "***");

    private static string Format(Exception ex, string source, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ScheduleApp {UpdateService.Current} — lỗi chưa được xử lý ({source})");
        sb.AppendLine($"Thời điểm: {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Hệ điều hành: {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture} · {RuntimeInformation.FrameworkDescription} · tiến trình {(Environment.Is64BitProcess ? 64 : 32)}-bit");
        sb.AppendLine();
        sb.AppendLine(Scrub(ex.ToString()));
        sb.AppendLine(new string('-', 80));
        return sb.ToString();
    }

    [GeneratedRegex(@"(?<=\bbot)\d{5,}:[\w-]{20,}|(?<=\bBearer\s+)[\w\-.~+/]{8,}=*|\bsk-ant-[\w-]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex TokenLike();
}

/// <summary>
/// Lỗi chưa xử lý trên luồng giao diện (Application.ThreadException): ghi file crash ngay, hộp thoại báo lỗi hiện SAU khi trình xử lý
/// trả về (<see cref="SynchronizationContext.Post"/>). WinForms bỏ qua mọi lỗi xảy ra trong lúc trình xử lý còn chạy — hộp thoại modal
/// mở ngay trong trình xử lý sẽ làm các lỗi tiếp theo (trong lúc hộp thoại mở) mất dấu, không có cả nhật ký lẫn file crash.
/// </summary>
internal sealed class UiErrorReporter(ErrorDialogGate gate, Action<Exception, string?> showDialog)
{
    /// <param name="interactive">False ở chế độ dòng lệnh: chỉ ghi nhật ký + file crash.</param>
    public void Handle(Exception ex, bool interactive)
    {
        var file = CrashLog.Write(ex, "luồng giao diện — Application.ThreadException");
        if (!interactive || SynchronizationContext.Current is not { } ui || !gate.TryOpen(DateTime.Now)) return;
        ui.Post(_ =>
        {
            try
            {
                showDialog(ex, file);
            }
            finally
            {
                gate.Close(DateTime.Now);
            }
        }, null);
    }
}

/// <summary>
/// Có hiện hộp thoại báo lỗi không: không bao giờ chồng hai hộp thoại; lỗi đến trong vòng <see cref="CrashLog.Burst"/> sau lỗi trước
/// hoặc sau lúc đóng hộp thoại trước thì chỉ ghi nhật ký.
/// </summary>
internal sealed class ErrorDialogGate
{
    private readonly object _sync = new();
    private bool _open;
    private DateTime _lastError = DateTime.MinValue;
    private DateTime _lastClosed = DateTime.MinValue;

    public bool TryOpen(DateTime now)
    {
        lock (_sync)
        {
            bool show = !_open && now - _lastError >= CrashLog.Burst && now - _lastClosed >= CrashLog.Burst;
            _lastError = now;
            if (show) _open = true;
            return show;
        }
    }

    public void Close(DateTime now)
    {
        lock (_sync)
        {
            _open = false;
            _lastClosed = now;
        }
    }
}
