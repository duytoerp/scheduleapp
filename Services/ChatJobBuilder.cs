using System.Text;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>
/// Tạo công việc qua tin nhắn (Telegram): mô tả bằng lời → AI dựng flow kèm lịch chạy → xem trước → nhắn thêm để sửa → lưu.
/// Giữ một bản nháp tại một thời điểm (hội thoại với AI được giữ để sửa tiếp); gọi từ luồng của bot.
/// </summary>
public sealed class ChatJobBuilder(IRemoteHost host)
{
    /// <summary>Số bước tối đa liệt kê trong tin nhắn xem trước — bước cần xem kỹ (chạy lệnh, mở ứng dụng…) luôn được liệt kê đầy đủ.</summary>
    private const int PreviewSteps = 40;

    private FlowGenerator? _generator;
    private FlowGenerator.Result? _draft;
    private int _busy;

    public bool HasDraft => _draft != null;

    /// <summary>Tăng mỗi lần có bản nháp mới / bản sửa — nút Lưu / Bỏ ở tin xem trước mang số này để không lưu nhầm bản nháp đã đổi.</summary>
    public int DraftVersion { get; private set; }

    private const string StaleDraft = "Bản nháp đã thay đổi sau tin này — dùng nút ở tin xem trước mới nhất (hoặc /ok, /huy).";
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
            DraftVersion++;
            // Bước phát video / nhạc: tính luôn thời lượng (hiện trong bản nháp, lưu cùng công việc). Không mở đường dẫn mạng
            // trong bản nháp nhận từ xa (Windows sẽ tự đăng nhập tới máy chủ lạ).
            try
            {
                using (RemotePathGate.Block()) await MediaInfo.FillDurationsAsync(_draft.Steps, _draft.Variables, ct);
            }
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

    /// <summary>
    /// Lưu bản nháp thành công việc mới; <paramref name="run"/> = chạy ngay. Mặc định công việc chờ duyệt trên máy (không chạy theo lịch,
    /// kích hoạt hay /run cho tới khi duyệt) — trừ khi Cài đặt cho phép chạy ngay không cần duyệt.
    /// </summary>
    /// <param name="version">Số phiên bản ở nút bấm (null = lệnh /ok gõ tay, lưu bản nháp hiện tại).</param>
    public string Save(bool run, int? version = null)
    {
        if (IsBusy) return "⏳ Đang dựng bản nháp, chờ xong rồi /ok.";
        if (_draft is not { } draft) return "Chưa có bản nháp nào — tạo bằng /new <mô tả>.";
        if (version != null && version != DraftVersion) return StaleDraft;
        if (draft.Steps.Count == 0) return "Bản nháp chưa có bước nào — nhắn yêu cầu để sửa, hoặc /huy.";
        var job = ToJob(draft);
        if (!SettingsStore.Current.Telegram.RunWithoutApproval) JobApproval.Require(job, JobApproval.TelegramReason());
        var reply = host.AddJob(job, run && !job.NeedsApproval);
        if (job.NeedsApproval)
            reply += "\n🔒 Chờ duyệt trên máy tính: công việc chưa chạy theo lịch, kích hoạt hay /run cho tới khi bạn mở ScheduleApp trên máy, " +
                     "xem từng bước và bấm Duyệt (chuột phải công việc → Duyệt…)." + (run ? " Vì vậy chưa chạy ngay." : "");
        _draft = null;
        _generator = null;
        return reply;
    }

    public string Cancel(int? version = null)
    {
        if (IsBusy) return "⏳ Đang dựng bản nháp, chờ xong rồi /huy.";
        if (_draft == null && _generator == null) return "Không có bản nháp nào.";
        if (version != null && (_draft == null || version != DraftVersion)) return StaleDraft;
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
        int omitted = 0;
        for (int i = 0; i < r.Steps.Count; i++)
        {
            var step = r.Steps[i];
            // Bước cần xem kỹ (chạy lệnh, mở ứng dụng, gửi HTTP, ghi file, gõ phím…) luôn hiện, đầy đủ — kể cả sau giới hạn số bước.
            if (i >= PreviewSteps && !step.IsRisky)
            {
                omitted++;
                continue;
            }
            sb.AppendLine(new string(' ', Math.Min(depth[i], 6) * 3) + $"{(step.IsRisky ? "⚠ " : "")}{i + 1}. {Log.Redact(step.FullDescribe())}");
        }
        if (omitted > 0) sb.AppendLine($"… và {omitted} bước khác không hiện (không có bước chạy lệnh / mở ứng dụng / gõ phím nào trong số đó)");

        if (r.Variables.Count > 0) sb.AppendLine().AppendLine("🔣 Biến: " + Log.Redact(string.Join(", ", r.Variables.Select(v => $"{v.Name} = \"{v.Value}\""))));
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
        if (!SettingsStore.Current.Telegram.RunWithoutApproval)
            sb.AppendLine().Append("🔒 Lưu xong, công việc chờ bạn duyệt trên máy tính (xem từng bước) rồi mới chạy.");
        return sb.ToString();
    }
}
