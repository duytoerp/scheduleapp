using System.Text;
using ScheduleApp.Models;

namespace ScheduleApp.Services;

/// <summary>
/// Duyệt công việc đến từ nguồn khác (tạo / sửa qua Telegram, nhập từ file hoặc thư mục kịch bản): công việc chờ duyệt không chạy
/// theo bất kỳ cách nào cho tới khi người dùng xem tóm tắt từng bước trên máy và bấm Duyệt — kể cả chỉ một bước "Gõ chữ" cũng có thể
/// chạy lệnh tùy ý (Win+R…), nên mọi công việc đến từ xa đều phải duyệt.
/// </summary>
public static class JobApproval
{
    /// <summary>Lý do cho công việc tạo qua Telegram, vd "Tạo qua Telegram 05/10 14:32".</summary>
    public static string TelegramReason(DateTime? at = null) => $"Tạo qua Telegram {at ?? DateTime.Now:dd/MM HH:mm}";

    /// <summary>Lý do cho công việc nhập từ file / thư mục, vd "Nhập từ file cong-viec.json 05/10 14:32".</summary>
    public static string ImportReason(string source, DateTime? at = null) => $"Nhập từ {source} {at ?? DateTime.Now:dd/MM HH:mm}";

    /// <summary>
    /// Nhập công việc từ file (<paramref name="path"/>): đọc như <see cref="JobStore.Import"/> rồi đánh dấu TẤT CẢ chờ duyệt —
    /// file từ nơi khác có thể chứa lệnh / lịch tự chạy, không được chạy trước khi người dùng xem.
    /// </summary>
    public static List<Job> ImportFile(string path, DateTime? at = null)
    {
        var imported = JobStore.Import(path);
        var reason = ImportReason("file " + Path.GetFileName(path), at);
        foreach (var j in imported) Require(j, reason);
        return imported;
    }

    /// <summary>Đánh dấu chờ duyệt.</summary>
    public static void Require(Job job, string reason)
    {
        job.NeedsApproval = true;
        job.ApprovalReason = reason;
    }

    /// <summary>Người dùng đã duyệt trên máy.</summary>
    public static void Approve(Job job)
    {
        job.NeedsApproval = false;
        job.ApprovalReason = null;
    }

    /// <summary>Lý do không chạy công việc chờ duyệt — ghi nhật ký, trả lời Telegram, báo lỗi bước "Chạy công việc khác".</summary>
    public static string RefusalMessage(Job job) =>
        $"\"{job.Name}\" đang chờ duyệt trên máy tính" + (string.IsNullOrWhiteSpace(job.ApprovalReason) ? "" : $" ({job.ApprovalReason})") +
        " — chưa chạy. Mở ScheduleApp, chuột phải công việc → Duyệt… để xem từng bước rồi duyệt.";

    /// <summary>
    /// Tóm tắt để duyệt: lý do, lịch, kích hoạt, công việc xử lý lỗi, biến và MỌI bước theo thứ tự — bước cần xem kỹ (⚠) hiện đầy đủ
    /// (<see cref="ActionStep.FullDescribe"/>). Giá trị bí mật đã biết được che (<see cref="Log.Redact"/>).
    /// </summary>
    /// <param name="all">Mọi công việc — để ghi tên công việc được gọi tới và chúng có đang chờ duyệt không.</param>
    public static string Summary(Job job, IReadOnlyList<Job>? all = null)
    {
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(Log.Redact(text)).Append("\r\n");
        Job? Find(Guid id) => all?.FirstOrDefault(j => j.Id == id);

        Line($"Công việc: {job.Name}" + (job.Group.Trim().Length > 0 ? $"  (nhóm {job.Group.Trim()})" : ""));
        if (!string.IsNullOrWhiteSpace(job.ApprovalReason)) Line("Nguồn: " + job.ApprovalReason);
        Line(job.Enabled ? "Trạng thái: bật — sau khi duyệt sẽ tự chạy theo lịch / kích hoạt dưới đây" : "Trạng thái: tắt (chỉ chạy khi bấm Chạy)");
        Line(job.Schedule.Type == ScheduleType.Manual ? "⏰ Lịch: không có (chạy tay)" : "⏰ Lịch: " + job.Schedule.Describe() + (job.SkipHolidays ? " · bỏ qua ngày nghỉ lễ" : ""));
        foreach (var t in job.Triggers)
            Line($"⚡ Kích hoạt: {t.Describe()}" + (t.Enabled ? "" : " (đang tắt)") + (t.Type == TriggerType.AppStartup ? " — chạy mỗi lần mở ScheduleApp" : ""));
        if (job.OnFailureJobId is Guid f) Line($"↪ Khi lỗi chạy: {Find(f)?.Name ?? f.ToString()}" + (Find(f) is { NeedsApproval: true } ? " (cũng đang chờ duyệt)" : ""));
        if (!string.IsNullOrWhiteSpace(job.DataFile)) Line($"📄 Dữ liệu kiểm thử: {job.DataFile}");
        foreach (var v in job.Variables) Line($"🔣 Biến {v.Name} = \"{v.Value.Replace("\r", "").Replace("\n", " ⏎ ")}\"");

        int risky = job.Steps.Count(s => s.Enabled && s.IsRisky);
        sb.Append("\r\n");
        Line($"🔧 {job.Steps.Count} bước" + (risky > 0 ? $" — {risky} bước cần xem kỹ (⚠, hiện đầy đủ):" : ":"));
        var depth = FlowStructure.Build(job.Steps).Depth;
        for (int i = 0; i < job.Steps.Count; i++)
        {
            var s = job.Steps[i];
            bool call = s.Type == StepType.CallJob;
            var text = call ? CallText(s, all) : s.FullDescribe();
            Line((s.IsRisky || call ? "⚠ " : "   ") + new string(' ', Math.Min(i < depth.Length ? depth[i] : 0, 6) * 2) + $"{i + 1}. {text}" + (s.Enabled ? "" : " (đang tắt)"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Bước "Chạy công việc khác": tên công việc THẬT sẽ chạy (theo JobRef — nhãn trong file có thể ghi sai / giả), cảnh báo khi đó là
    /// công việc đã duyệt có sẵn trên máy (công việc này sẽ được chạy theo lịch của công việc nhập vào).
    /// </summary>
    private static string CallText(ActionStep s, IReadOnlyList<Job>? all)
    {
        if (s.JobRef is not Guid id) return $"Chạy công việc \"{s.Target}\" (không gắn công việc nào — bước sẽ lỗi)";
        var target = all?.FirstOrDefault(j => j.Id == id);
        if (target == null) return all == null ? s.FullDescribe() : $"Chạy công việc (không có trên máy — Id {id}; nhãn trong file: \"{s.Target}\")";
        var text = $"Chạy công việc \"{target.Name}\"";
        if (!string.Equals(s.Target.Trim(), target.Name.Trim(), StringComparison.CurrentCultureIgnoreCase) && s.Target.Trim().Length > 0)
            text += $" (nhãn trong file ghi \"{s.Target}\" — KHÁC tên thật)";
        return text + (target.NeedsApproval ? " (công việc này cũng đang chờ duyệt)" : " — công việc ĐÃ DUYỆT có sẵn trên máy, sẽ được chạy bởi công việc này");
    }

    /// <summary>
    /// Tóm tắt sau khi nhập nhiều công việc: mỗi công việc kèm lịch / kích hoạt, biến (bước có thể dùng {{biến}} làm lệnh) và các bước
    /// cần xem kỹ (đầy đủ); công việc không có bước nào như vậy chỉ ghi một dòng.
    /// </summary>
    /// <param name="all">Mọi công việc trên máy — để ghi tên thật của công việc được gọi / chạy khi lỗi.</param>
    public static string ImportSummary(IReadOnlyList<Job> jobs, IReadOnlyList<Job>? all = null)
    {
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(Log.Redact(text)).Append("\r\n");
        foreach (var job in jobs)
        {
            // Bước gọi công việc khác luôn được liệt kê: có thể chạy một công việc đã duyệt có sẵn trên máy.
            var risky = job.Steps.Select((s, i) => (Step: s, Number: i + 1)).Where(x => x.Step.IsRisky || x.Step.Type == StepType.CallJob).ToList();
            Line($"■ {job.Name} — {job.Steps.Count} bước" + (risky.Count > 0 ? $", {risky.Count} bước cần xem kỹ" : ", không có bước chạy lệnh / mở ứng dụng / gõ phím…"));
            if (job.Enabled && job.Schedule.Type != ScheduleType.Manual) Line("   ⏰ " + job.Schedule.Describe());
            foreach (var t in job.Triggers.Where(t => t.Enabled)) Line("   ⚡ " + t.Describe());
            foreach (var v in job.Variables) Line($"   🔣 {v.Name} = \"{v.Value.Replace("\r", "").Replace("\n", " ⏎ ")}\"");
            if (job.OnFailureJobId is Guid f)
            {
                var onFailure = all?.FirstOrDefault(j => j.Id == f);
                Line($"   ↪ Khi lỗi chạy: {onFailure?.Name ?? "(không có trên máy)"}" +
                     (onFailure is { NeedsApproval: false } ? " — công việc ĐÃ DUYỆT có sẵn trên máy" : ""));
            }
            foreach (var (step, number) in risky)
                Line($"   ⚠ {number}. {(step.Type == StepType.CallJob ? CallText(step, all) : step.FullDescribe())}" + (step.Enabled ? "" : " (đang tắt)"));
            sb.Append("\r\n");
        }
        return sb.ToString().TrimEnd();
    }
}
