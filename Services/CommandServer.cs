using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ScheduleApp.Services;

/// <summary>
/// Nhận lệnh từ dòng lệnh khi ScheduleApp đã chạy sẵn (named pipe, chỉ tài khoản hiện tại kết nối được):
/// "show", "run &lt;tên công việc&gt;", "stop".
/// </summary>
public static class CommandServer
{
    private const string PipePrefix = "ScheduleApp_Cmd_7F3A1C_";

    /// <summary>
    /// Tên pipe riêng cho từng tài khoản Windows (tên pipe dùng chung cả máy): hai người cùng đăng nhập
    /// (chuyển người dùng nhanh / Remote Desktop) mỗi người có kênh lệnh riêng, không chặn nhau.
    /// </summary>
    internal static string PipeName { get; } = PipeNameFor(CurrentUserSid());

    /// <summary>Tên pipe ứng với SID người dùng (băm — không lộ SID trong tên).</summary>
    internal static string PipeNameFor(string userSid) =>
        PipePrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid.ToUpperInvariant())))[..16];

    private static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? Environment.UserDomainName + "\\" + Environment.UserName;
    }

    /// <summary>Lắng nghe lệnh tới khi <paramref name="ct"/> bị hủy; <paramref name="handle"/> được gọi trên luồng nền.</summary>
    public static Task ListenAsync(Action<string> handle, CancellationToken ct) => ListenAsync(handle, ct, PipeName);

    internal static async Task ListenAsync(Action<string> handle, CancellationToken ct, string pipeName)
    {
        // Lỗi lặp lại (vd tài khoản khác đã chiếm tên kênh, hoặc cùng tài khoản đang mở ScheduleApp ở phiên Windows khác):
        // chờ lâu dần tới 1 phút, mỗi kiểu lỗi chỉ ghi nhật ký một lần — không ghi mỗi 500 ms suốt ngày.
        var delay = TimeSpan.FromMilliseconds(500);
        string? lastError = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct);
                delay = TimeSpan.FromMilliseconds(500);
                lastError = null;
                if (!string.IsNullOrWhiteSpace(line)) handle(line.Trim());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var kind = ex.GetType().Name;
                if (kind != lastError)
                    Log.Warn("Lỗi kênh lệnh (lệnh từ shortcut / dòng lệnh có thể không tới được ScheduleApp đang mở): " + ex.Message);
                lastError = kind;
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromMinutes(1).Ticks));
            }
        }
    }

    /// <summary>Gửi lệnh tới phiên bản đang chạy. False nếu không kết nối được.</summary>
    public static bool Send(string command) => Send(command, PipeName);

    internal static bool Send(string command, string pipeName, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(command);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
