using ScheduleApp.Services;
using ScheduleApp.UI;

namespace ScheduleApp;

/// <summary>
/// Tham số dòng lệnh:
///   --minimized            khởi động ẩn ở khay hệ thống
///   --run "Tên công việc"  chạy ngay một công việc (gửi tới phiên bản đang chạy nếu có)
///   --stop                 dừng flow đang chạy
///   --test "Nhóm/Tên"      chạy bộ kiểm thử không mở giao diện, xuất báo cáo (--report "thư mục"); mã thoát 0 = đạt, 1 = không đạt
/// </summary>
internal static class Program
{
    private const string MutexName = "ScheduleApp_SingleInstance_7F3A1C";

    [STAThread]
    private static void Main(string[] args)
    {
        if (HasArg(args, "--test"))
        {
            // Chạy độc lập với phiên bản đang mở (không cần giao diện) — dùng cho CI / lịch chạy đêm.
            Environment.ExitCode = Services.Testing.TestCli.Run(ArgValue(args, "--test") ?? "", ArgValue(args, "--report"));
            return;
        }

        string? runJob = ArgValue(args, "--run");
        bool stop = HasArg(args, "--stop");
        bool startHidden = HasArg(args, "--minimized") || runJob != null || stop;

        using var mutex = new Mutex(true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Đã có một phiên bản đang chạy → chuyển lệnh cho nó rồi thoát.
            var command = runJob != null ? "run " + runJob : stop ? "stop" : "show";
            if (!CommandServer.Send(command))
                MessageBox.Show("ScheduleApp đang chạy nhưng không nhận được lệnh. Hãy thử lại.", "ScheduleApp",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (stop) return; // không có flow nào đang chạy

        ApplicationConfiguration.Initialize();

        var form = new MainForm(startHidden, runJob != null ? "run " + runJob : null);
        using var cts = new CancellationTokenSource();
        _ = CommandServer.ListenAsync(cmd =>
        {
            if (form.IsDisposed) return;
            try { form.BeginInvoke(new MethodInvoker(() => form.HandleCommand(cmd))); }
            catch (InvalidOperationException) { }
        }, cts.Token);

        Application.Run(form);
        cts.Cancel();
    }

    private static bool HasArg(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
