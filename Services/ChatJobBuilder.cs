using System.Text;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>
/// Tạo công việc qua tin nhắn (Telegram): mô tả bằng lời → AI dựng flow kèm lịch chạy → xem trước → nhắn thêm để sửa → lưu.
/// Giữ một bản nháp tại một thời điểm (hội thoại với AI được giữ để sửa tiếp); gọi từ luồng của bot.
/// </summary>
public sealed class ChatJobBuilder(IRemoteHost host)
{
    /// <summary>Số bước tối đa liệt kê trong tin nhắn xem trước.</summary>
    private const int PreviewSteps = 40;

    private FlowGenerator? _generator;
    private FlowGenerator.Result? _draft;
    private int _busy;

    public bool HasDraft => _draft != null;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>Bản nháp mới từ mô tả (thay bản nháp cũ nếu có).</summary>
    public Task<string> CreateAsync(string prompt, CancellationToken ct) => SendAsync(prompt, fresh: true, ct);

    /// <summary>Sửa bản nháp đang có theo yêu cầu ("dùng Edge thay Chrome", "chạy lúc 9h"…).</summary>
    public Task<string> ReviseAsync(string request, CancellationToken ct) => SendAsync(request, fresh: false, ct);

    private async Task<string> SendAsync(string text, bool fresh, CancellationToken ct)
    {
        if (!AiClient.IsConfigured) return "Chưa có khóa Claude — nhập trong ⚙ Cài đặt → Tích hợp trên máy tính rồi thử lại.";
        if (text.Trim().Length == 0)
            return fresh ? "Cú pháp: /new <mô tả việc cần tự động>\nVd: /new 8h sáng các ngày làm việc mở D:\\bao-cao.xlsx, làm mới dữ liệu (Ctrl+Alt+F5), lưu rồi gửi thông báo cho tôi"
                         : "Hãy nhắn yêu cầu sửa.";
        if (!fresh && _draft == null) return "Chưa có bản nháp nào — tạo bằng /new <mô tả>.";
        if (Interlocked.Exchange(ref _busy, 1) != 0) return "⏳ Đang dựng bản nháp trước, chờ xong rồi nhắn tiếp.";
        try
        {
            if (fresh)
            {
                _generator = new FlowGenerator(host.NewJobContext());
                _draft = null;
            }
            _draft = await _generator!.SendAsync(text, FlowGenerator.Mode.Replace, ct);
            // Bước phát video / nhạc: tính luôn thời lượng (hiện trong bản nháp, lưu cùng công việc).
            try { await MediaInfo.FillDurationsAsync(_draft.Steps, _draft.Variables, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("Telegram: không tính được thời lượng video — " + ex.Message); }
            return Preview(_draft);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            Log.Warn("Telegram: AI không tạo được công việc — " + ex.Message);
            return "✖ Không tạo được: " + ex.Message +
                   (_draft != null ? "\nBản nháp trước vẫn còn — /ok để lưu, hoặc nhắn yêu cầu khác." : "");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Lưu bản nháp thành công việc mới; <paramref name="run"/> = chạy ngay.</summary>
    public string Save(bool run)
    {
        if (IsBusy) return "⏳ Đang dựng bản nháp, chờ xong rồi /ok.";
        if (_draft is not { } draft) return "Chưa có bản nháp nào — tạo bằng /new <mô tả>.";
        if (draft.Steps.Count == 0) return "Bản nháp chưa có bước nào — nhắn yêu cầu để sửa, hoặc /huy.";
        var reply = host.AddJob(ToJob(draft), run);
        _draft = null;
        _generator = null;
        return reply;
    }

    public string Cancel()
    {
        if (IsBusy) return "⏳ Đang dựng bản nháp, chờ xong rồi /huy.";
        if (_draft == null && _generator == null) return "Không có bản nháp nào.";
        _draft = null;
        _generator = null;
        return "Đã bỏ bản nháp.";
    }

    internal static Job ToJob(FlowGenerator.Result r) => new()
    {
        Name = r.Name.Length > 0 ? r.Name : "Công việc từ Telegram",
        Group = "Telegram",
        Steps = [.. r.Steps],
        Variables = [.. r.Variables],
        Schedule = r.Schedule ?? new ScheduleConfig { Type = ScheduleType.Manual },
        Triggers = [.. r.Triggers],
        SkipHolidays = r.SkipHolidays
    };

    /// <summary>Tin nhắn xem trước bản nháp: tên, lịch, các bước, việc cần kiểm tra và cách lưu / sửa.</summary>
    internal static string Preview(FlowGenerator.Result r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("✨ Bản nháp: " + (r.Name.Length > 0 ? r.Name : "(chưa đặt tên)"));
        if (r.Summary.Length > 0) sb.AppendLine(r.Summary);
        sb.AppendLine();
        sb.AppendLine(r.Schedule is { Type: not ScheduleType.Manual } s
            ? "⏰ " + s.Describe() + (r.SkipHolidays ? " · bỏ qua ngày nghỉ lễ" : "")
            : "⏰ Không đặt lịch — chạy bằng /run" + (r.Triggers.Count > 0 ? " hoặc khi:" : ""));
        foreach (var t in r.Triggers) sb.AppendLine("⚡ " + t.Describe());

        sb.AppendLine().AppendLine($"🔧 {r.Steps.Count} bước:");
        var depth = FlowStructure.Build(r.Steps).Depth;
        for (int i = 0; i < Math.Min(PreviewSteps, r.Steps.Count); i++)
            sb.AppendLine(new string(' ', Math.Min(depth[i], 6) * 3) + $"{i + 1}. {r.Steps[i].Describe()}");
        if (r.Steps.Count > PreviewSteps) sb.AppendLine($"… và {r.Steps.Count - PreviewSteps} bước nữa");

        if (r.Variables.Count > 0) sb.AppendLine().AppendLine("🔣 Biến: " + string.Join(", ", r.Variables.Select(v => $"{v.Name} = \"{v.Value}\"")));
        if (r.Notes.Count > 0)
        {
            sb.AppendLine().AppendLine("📌 Cần kiểm tra trước khi chạy:");
            foreach (var n in r.Notes) sb.AppendLine("• " + n);
        }
        if (r.Problems.Count > 0)
        {
            sb.AppendLine().AppendLine("⚠ Lỗi AI chưa tự sửa được (vẫn lưu được, sửa tiếp trên máy hoặc nhắn yêu cầu sửa):");
            foreach (var p in r.Problems) sb.AppendLine("• " + p);
        }
        sb.AppendLine().Append("👉 /ok — lưu · /ok chay — lưu và chạy ngay · nhắn thêm để sửa (vd \"dùng Edge thay Chrome\", \"chạy lúc 9h\") · /huy — bỏ");
        return sb.ToString();
    }
}
