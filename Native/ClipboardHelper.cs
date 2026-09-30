using System.Diagnostics;

namespace ScheduleApp.Native;

/// <summary>Truy cập clipboard từ luồng bất kỳ (clipboard yêu cầu luồng STA).</summary>
internal static class ClipboardHelper
{
    private static readonly string[] VietnameseImeProcesses = ["unikeynt", "unikey", "evkey", "evkey64", "evkey32", "openkey", "gotiengviet", "vietkey"];

    /// <summary>Có bộ gõ tiếng Việt đang chạy không — bộ gõ có thể "sửa" nhầm các ký tự do ứng dụng gửi vào.</summary>
    public static string? RunningVietnameseIme()
    {
        foreach (var name in VietnameseImeProcesses)
        {
            var procs = Process.GetProcessesByName(name);
            bool found = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
            if (found) return name;
        }
        return null;
    }

    public static string? TryGetText() => RunSta(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

    public static void SetText(string text) => RunSta(() =>
    {
        // Đặt CF_UNICODETEXT lên đầu: nếu đưa thẳng chuỗi, CF_TEXT (ANSI) đứng trước và nhiều ứng dụng
        // (vd Notepad Win11) lấy bản ANSI → chữ tiếng Việt ngoài bảng mã thành "?".
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        data.SetData(DataFormats.Text, text);
        Clipboard.SetDataObject(data, true, 10, 50);
        return true;
    });

    private static T RunSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error != null) throw new InvalidOperationException("Không truy cập được clipboard: " + error.Message, error);
        return result;
    }
}
