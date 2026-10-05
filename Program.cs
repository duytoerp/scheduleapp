using ScheduleApp.Services;
using ScheduleApp.UI;

namespace ScheduleApp;

/// <summary>
/// Tham số dòng lệnh:
///   --minimized            khởi động ẩn ở khay hệ thống
///   --run "Tên công việc"  chạy ngay một công việc (gửi tới phiên bản đang chạy nếu có)
///   --stop                 dừng flow đang chạy
///   --test "Nhóm/Tên"      chạy bộ kiểm thử không mở giao diện, xuất báo cáo (--report "thư mục"); mã thoát 0 = đạt, 1 = không đạt
///                          thêm: --tag, --env, --test-dir, --retry, --shard, --headless, --list (xem TestCli)
/// </summary>
internal static class Program
{
    private const string MutexName = "ScheduleApp_SingleInstance_7F3A1C";

    /// <summary>Lỗi trên luồng giao diện: ghi file crash ngay; hộp thoại không chồng nhau, lỗi lặp lại ngay sau đó chỉ ghi nhật ký.</summary>
    private static readonly UiErrorReporter UiErrors = new(new ErrorDialogGate(), ShowErrorDialog);

    [STAThread]
    private static void Main(string[] args)
    {
        // Bắt lỗi chưa xử lý trước khi tạo bất kỳ cửa sổ nào; chế độ dòng lệnh (--test) không hiện hộp thoại.
        bool cli = HasArg(args, "--test");
        InstallErrorHandlers(interactive: !cli);

        if (cli)
        {
            // Chạy độc lập với phiên bản đang mở (không cần giao diện) — dùng cho CI / lịch chạy đêm.
            Environment.ExitCode = Services.Testing.TestCli.Run(args);
            return;
        }

        string? runJob = ArgValue(args, "--run");
        bool stop = HasArg(args, "--stop");
        bool startHidden = HasArg(args, "--minimized") || runJob != null || stop;

        using var mutex = new Mutex(true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Đã có một phiên bản đang chạy → chuyển lệnh cho nó rồi thoát.
            UpdateService.NoteSecondInstance(); // do script cập nhật mở → không coi là bản mới hỏng
            var command = runJob != null ? "run " + runJob : stop ? "stop" : "show";
            if (!CommandServer.Send(command))
                MessageBox.Show("ScheduleApp đang chạy nhưng không nhận được lệnh. Hãy thử lại.", "ScheduleApp",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (stop) return; // không có flow nào đang chạy
        UpdateService.BeginStartupCheck(); // mở bởi script cập nhật → ghi PID, nhớ file đánh dấu khởi động xong

        ApplicationConfiguration.Initialize();
        Theme.Install();

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

    /// <summary>
    /// Lỗi chưa xử lý ở đâu cũng được ghi nhật ký + file crash-….txt trong thư mục logs (không chứa giá trị bí mật).
    /// Lỗi trên luồng giao diện không làm tắt ứng dụng — lịch vẫn chạy — chỉ báo bằng hộp thoại (không chồng hộp thoại).
    /// </summary>
    private static void InstallErrorHandlers(bool interactive)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => UiErrors.Handle(e.Exception, interactive);
        // Lỗi ở luồng nền: tiến trình đóng ngay sau sự kiện này → ghi file crash xong mới trả về.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write(e.ExceptionObject as Exception ?? new InvalidOperationException(Convert.ToString(e.ExceptionObject)),
                e.IsTerminating ? "luồng nền, ScheduleApp phải đóng — AppDomain.UnhandledException" : "luồng nền — AppDomain.UnhandledException");
        // Task chạy nền không ai chờ kết quả ("_ = …Async()"): ghi lại rồi bỏ qua, không ảnh hưởng ứng dụng.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception, "tác vụ nền — TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };
    }

    private static void ShowErrorDialog(Exception ex, string? file)
    {
        try
        {
            MessageBox.Show(
                "ScheduleApp vừa gặp một lỗi ngoài dự kiến nhưng vẫn tiếp tục chạy — lịch và các công việc vẫn hoạt động.\n\n" +
                $"Lỗi: {CrashLog.Summary(ex)}\n\n" +
                (file != null ? $"Chi tiết đã lưu ở:\n{file}" : $"Chi tiết đã ghi vào nhật ký trong:\n{Log.LogDir}") +
                "\n\nNếu lỗi lặp lại, hãy gửi file này cho người hỗ trợ.",
                "ScheduleApp — lỗi ngoài dự kiến", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Không hiện được hộp thoại (phiên không tương tác…) — đã có nhật ký và file crash.
        }
    }

    private static bool HasArg(string[] args, string name) =>
        args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
