using System.Reflection;
using ScheduleApp.Models;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>Kho mẫu có sẵn (nhúng trong ứng dụng): chọn một mẫu để tạo công việc mới rồi chỉnh sửa.</summary>
internal sealed class TemplatePickerForm : BaseForm
{
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly TextBox _detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.None };
    private readonly List<Job> _templates;

    public Job? Selected { get; private set; }

    public TemplatePickerForm()
    {
        _templates = LoadTemplates();

        SuspendLayout();
        Text = "Mẫu có sẵn — ScheduleApp";
        Size = new Size(980, 600);
        MinimumSize = new Size(760, 460);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(10);

        _list.Columns.Add("Mẫu", 420);
        _list.Columns.Add("Nhóm", 150);
        _list.Columns.Add("Lịch", 220);
        foreach (var t in _templates)
            _list.Items.Add(new ListViewItem([t.Name, t.Group, t.Schedule.Describe()]) { Tag = t });

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 300 };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_detail);

        var ok = new Button { Text = "Dùng mẫu này…", AutoSize = true, MinimumSize = new Size(120, 0) };
        var cancel = new Button { Text = "Hủy", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Choose();
        _list.DoubleClick += (_, _) => Choose();
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([cancel, ok]);

        var hint = new Label
        {
            Text = "Chọn mẫu để mở trình soạn với flow dựng sẵn — chỉnh lại cửa sổ, đường dẫn, bộ chọn… cho phù hợp máy của bạn rồi Lưu.",
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = UiText.Muted,
            Padding = new Padding(0, 0, 0, 6)
        };
        Controls.Add(split);
        Controls.Add(buttons);
        Controls.Add(hint);
        ResumeLayout(true);

        _list.SelectedIndexChanged += (_, _) => ShowDetail();
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
    }

    private static List<Job> LoadTemplates()
    {
        var asm = Assembly.GetExecutingAssembly();
        var result = new List<Job>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Order())
        {
            try
            {
                using var stream = asm.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                result.AddRange(JobStore.ImportJson(reader.ReadToEnd()));
            }
            catch (Exception ex)
            {
                Log.Warn($"Không đọc được mẫu {name}: {ex.Message}");
            }
        }
        // Mẫu có lịch hoặc trình kích hoạt (theo dõi thư mục, phím tắt…) để tắt sẵn — người dùng tự bật sau khi xem lại.
        foreach (var j in result) j.Enabled = j.Schedule.Type == ScheduleType.Manual && j.Triggers.Count == 0;
        return result;
    }

    private void ShowDetail()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not Job t)
        {
            _detail.Text = "";
            return;
        }
        var depth = FlowStructure.Build(t.Steps).Depth;
        var steps = t.Steps.Select((s, i) => $"{i + 1,3}. {new string(' ', Math.Min(depth[i], 6) * 4)}{ActionStep.TypeNames[s.Type]} — {s.Describe()}");
        var nl = Environment.NewLine;
        _detail.Text = $"{t.Name}{nl}{t.Schedule.Describe()}" +
                       (t.Triggers.Count > 0 ? "  ·  ⚡ " + string.Join(", ", t.Triggers.Select(x => x.Describe())) : "") +
                       nl + nl + string.Join(nl, steps);
    }

    /// <summary>Các mẫu mà mẫu được chọn gọi tới (bước "Chạy công việc khác" / công việc xử lý lỗi) — cần thêm cùng.</summary>
    public List<Job> Dependencies { get; } = [];

    private void Choose()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not Job t) return;
        Selected = t;
        var pending = new Queue<Job>([t]);
        while (pending.Count > 0)
        {
            var job = pending.Dequeue();
            var refs = job.Steps.Where(s => s.JobRef != null).Select(s => s.JobRef!.Value).Append(job.OnFailureJobId ?? Guid.Empty);
            foreach (var dep in _templates.Where(x => refs.Contains(x.Id) && x != t && !Dependencies.Contains(x)))
            {
                Dependencies.Add(dep);
                pending.Enqueue(dep);
            }
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
