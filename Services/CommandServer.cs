using System.IO.Pipes;
using System.Text;

namespace ScheduleApp.Services;

/// <summary>
/// Nhận lệnh từ dòng lệnh khi ScheduleApp đã chạy sẵn (named pipe, chỉ tài khoản hiện tại kết nối được):
/// "show", "run &lt;tên công việc&gt;", "stop".
/// </summary>
public static class CommandServer
{
    private const string PipeName = "ScheduleApp_Cmd_7F3A1C";

    /// <summary>Lắng nghe lệnh tới khi <paramref name="ct"/> bị hủy; <paramref name="handle"/> được gọi trên luồng nền.</summary>
    public static async Task ListenAsync(Action<string> handle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
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
    public static bool Send(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
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
