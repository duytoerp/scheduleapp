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
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct);
                if (!string.IsNullOrWhiteSpace(line)) handle(line.Trim());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException ex)
            {
                Log.Warn("Lỗi kênh lệnh: " + ex.Message);
                await Task.Delay(500, CancellationToken.None);
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
