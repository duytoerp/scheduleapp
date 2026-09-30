namespace ScheduleApp.Services;

/// <summary>Cầu nối để bộ chạy flow tương tác với giao diện (gọi được từ luồng bất kỳ).</summary>
public interface IUserNotifier
{
    /// <summary>Hiện cửa sổ nhắc nhở. Nếu <paramref name="waitForUser"/> thì task chỉ hoàn thành khi người dùng xác nhận.</summary>
    Task ShowReminderAsync(string title, string message, bool waitForUser, CancellationToken ct);

    /// <summary>Thông báo nhanh ở khay hệ thống.</summary>
    void Notify(string title, string text, bool isError);

    /// <summary>Tạm dời các cửa sổ của ScheduleApp khỏi màn hình để không che vùng thao tác. Dispose để khôi phục.</summary>
    IDisposable ClearScreenForAutomation();
}
