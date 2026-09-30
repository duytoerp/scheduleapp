using ScheduleApp.UI;

namespace ScheduleApp;

internal static class Program
{
    private const string MutexName = "ScheduleApp_SingleInstance_7F3A1C";
    private const string ShowEventName = "ScheduleApp_ShowMainWindow_7F3A1C";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Đã có một phiên bản đang chạy -> yêu cầu nó hiện cửa sổ chính rồi thoát.
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            return;
        }

        ApplicationConfiguration.Initialize();

        bool startHidden = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var form = new MainForm(startHidden);

        var listener = new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                if (form.IsDisposed) break;
                try { form.BeginInvoke(new MethodInvoker(form.ShowMain)); } catch { break; }
            }
        }) { IsBackground = true, Name = "SingleInstanceListener" };
        listener.Start();

        Application.Run(form);
    }
}
