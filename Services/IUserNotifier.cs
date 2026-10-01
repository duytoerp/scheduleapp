using ScheduleApp.Services.Engine;

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

    /// <summary>Hỏi người dùng nhập một giá trị; null nếu người dùng hủy.</summary>
    Task<string?> PromptAsync(string title, string message, string defaultValue, bool password, CancellationToken ct);

    /// <summary>Tạm dừng gỡ lỗi trước một bước, chờ người dùng chọn chạy tiếp / từng bước / dừng.</summary>
    Task<DebugCommand> DebugPauseAsync(string jobName, int stepIndex, string stepText, string reason,
        IReadOnlyDictionary<string, string> variables, CancellationToken ct);

    /// <summary>Hỏi Có/Không (vd chế độ an toàn: người dùng vừa đụng chuột, có chạy tiếp flow không).</summary>
    Task<bool> AskContinueAsync(string title, string message, CancellationToken ct);
}
