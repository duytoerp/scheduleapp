namespace ScheduleApp.Services;

/// <summary>Đọc mật khẩu / token đã lưu (mã hóa DPAPI) ngay trước khi dùng để gửi ra ngoài.</summary>
internal static class Credentials
{
    /// <summary>
    /// Giải mã <paramref name="stored"/>; chưa nhập → "". Có giá trị nhưng không giải mã được (chép từ máy / tài khoản Windows khác)
    /// → báo lỗi rõ ràng thay vì âm thầm gửi chuỗi rỗng. Giá trị giải mã được tự động bị che trong log (<see cref="Protector"/>).
    /// </summary>
    /// <param name="what">Tên hiển thị trong thông báo lỗi, vd "token bot Telegram".</param>
    public static string Reveal(string? stored, string what)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (Protector.TryUnprotect(stored, out var plain)) return plain;
        throw new InvalidOperationException($"Không giải mã được {what} (dữ liệu chép từ máy / tài khoản Windows khác) — nhập lại trong ⚙ Cài đặt.");
    }
}
