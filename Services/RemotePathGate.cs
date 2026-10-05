namespace ScheduleApp.Services;

/// <summary>
/// Chặn mở đường dẫn mạng (\\máy\thư mục, file://máy/…) khi XEM TRƯỚC công việc chờ duyệt: chỉ mở trình soạn công việc nhận qua
/// Telegram / nhập từ file cũng đủ để Windows tự đăng nhập (gửi mã băm NTLM của bạn) tới máy chủ trong đường dẫn — ảnh thu nhỏ video,
/// thời lượng, tên sheet Excel… Trong lúc có màn hình như vậy (<see cref="Block"/>), các hàm xem trước trả về "không đọc được" thay vì mở.
/// Lúc flow chạy thật (<see cref="EnterRun"/>) không bị chặn — công việc đã được duyệt.
/// </summary>
public static class RemotePathGate
{
    private static int _blocks;
    private static readonly AsyncLocal<bool> InRun = new();

    /// <summary>Thông báo cho ô xem trước khi đường dẫn mạng bị chặn.</summary>
    public const string BlockedMessage = "đường dẫn mạng — chưa mở khi công việc đang chờ duyệt";

    /// <summary>Đang có màn hình xem trước công việc chờ duyệt.</summary>
    public static bool IsBlocking => Volatile.Read(ref _blocks) > 0;

    /// <summary>Bắt đầu chặn (Dispose để thôi chặn); gọi lồng nhau được.</summary>
    public static IDisposable Block()
    {
        Interlocked.Increment(ref _blocks);
        return new Release();
    }

    /// <summary>
    /// Đánh dấu luồng chạy flow hiện tại (và mọi thứ nó gọi, kể cả qua await / Task.Run) là chạy thật — không bị chặn.
    /// Gọi ở đầu một phương thức async: thay đổi tự hết khi phương thức đó kết thúc.
    /// </summary>
    public static void EnterRun() => InRun.Value = true;

    /// <summary>Đường dẫn UNC, \\?\UNC\…, file://máy/… (sau khi thay biến môi trường).</summary>
    public static bool IsRemote(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"', '\'')).Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) return false; // đường dẫn cục bộ dạng dài / thiết bị
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        if (p.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(path.Trim().Trim('"', '\''), UriKind.Absolute, out var uri) && uri.IsUnc;
        return false;
    }

    /// <summary>Có được mở <paramref name="path"/> để xem trước không (false = đường dẫn mạng trong lúc đang chặn, ngoài flow chạy thật).</summary>
    public static bool Allows(string? path) => !IsBlocking || InRun.Value || !IsRemote(path);

    private sealed class Release : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref _blocks);
        }
    }
}
