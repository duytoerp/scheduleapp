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

    /// <summary>Đặt chữ lên clipboard (dữ liệu của người dùng, lưu bình thường).</summary>
    public static void SetText(string text) => Put(text, forAutomation: false);

    /// <summary>
    /// Đặt chữ lên clipboard để tự động dán (có thể là mật khẩu): đánh dấu KHÔNG đưa vào Lịch sử clipboard (Win+V),
    /// clipboard đám mây hay các trình theo dõi clipboard — gọi kèm <see cref="Clear"/> sau khi dán xong.
    /// </summary>
    public static void SetTextForAutomation(string text) => Put(text, forAutomation: true);

    /// <summary>Xóa sạch clipboard (dùng sau khi dán xong mà trước đó clipboard trống).</summary>
    public static void Clear() => RunSta(() => { Clipboard.Clear(); return true; });

    /// <summary>
    /// Dán tạm <paramref name="text"/> (có thể là mật khẩu): đặt lên clipboard ở chế độ riêng tư, gọi <paramref name="paste"/>
    /// (gửi Ctrl+V), chờ ứng dụng đích đọc xong rồi LUÔN dọn — trả lại chữ cũ, hoặc xóa hẳn nếu trước đó không có chữ —
    /// kể cả khi bị hủy / lỗi giữa chừng, để mật khẩu không nằm lại trên clipboard.
    /// </summary>
    public static async Task PasteTemporarilyAsync(string text, Action paste, CancellationToken ct, int settleMs = 300)
    {
        var previous = TryGetText();
        SetTextForAutomation(text);
        try
        {
            paste();
            await Task.Delay(settleMs, ct); // ứng dụng đích đọc clipboard bất đồng bộ
        }
        finally
        {
            if (previous != null) SetText(previous);
            else Clear();
        }
    }

    private static void Put(string text, bool forAutomation) => RunSta(() =>
    {
        // Đặt CF_UNICODETEXT lên đầu: nếu đưa thẳng chuỗi, CF_TEXT (ANSI) đứng trước và nhiều ứng dụng
        // (vd Notepad Win11) lấy bản ANSI → chữ tiếng Việt ngoài bảng mã thành "?".
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text);
        data.SetData(DataFormats.Text, text);
        if (forAutomation)
        {
            // Các format "riêng tư" của Windows: chỉ cần có mặt format exclude; hai format còn lại là DWORD = 0 (không cho).
            var deny = new byte[4];
            data.SetData(ExcludeFromMonitors, new MemoryStream(deny, writable: false));
            data.SetData(CanIncludeInHistory, new MemoryStream(deny, writable: false));
            data.SetData(CanUploadToCloud, new MemoryStream(deny, writable: false));
        }
        Clipboard.SetDataObject(data, true, 10, 50);
        return true;
    });

    private const string ExcludeFromMonitors = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloud = "CanUploadToCloudClipboard";

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
