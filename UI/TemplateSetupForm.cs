using ScheduleApp.Services;
using ScheduleApp.Services.Engine;
using Item = ScheduleApp.Services.TemplateSetup.Item;
using ItemKind = ScheduleApp.Services.TemplateSetup.ItemKind;

namespace ScheduleApp.UI;

/// <summary>
/// Điền một lần các giá trị riêng của bạn cho mẫu (tenant, URL Dynamics 365, tài khoản test, bí mật, đường dẫn…) —
/// áp cho mọi công việc dùng chung giá trị đó. Hiện khi thêm mẫu; mở lại được từ menu chuột phải công việc.
/// </summary>
internal sealed class TemplateSetupForm : BaseForm
{
    private readonly List<Item> _items;
    private readonly Action? _openSettings;
    private readonly Dictionary<Item, TextBox> _inputs = [];
    private readonly Dictionary<Item, Label> _status = [];
    private readonly ToolTip _tips = new();

    /// <summary>Số chỗ đã thay đổi sau khi bấm Áp dụng.</summary>
    public int Changes { get; private set; }

    public TemplateSetupForm(List<Item> items, string intro, Action? openSettings)
    {
        _items = items;
        _openSettings = openSettings;
        SuspendLayout();
        Text = "Thiết lập mẫu";
        Size = new Size(920, 640);
        MinimumSize = new Size(700, 420);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(12);
        BuildUi(intro);
        ResumeLayout(true);
    }

    private void BuildUi(string intro)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(new Label
        {
            Text = intro,
            UseMnemonic = false,
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(860), 0),
            ForeColor = UiText.Muted,
            Margin = new Padding(0, 0, 0, 8)
        });

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Top, Padding = new Padding(10, 6, 10, 10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(270)));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        scroll.Controls.Add(grid);
        root.Controls.Add(scroll);

        foreach (var group in _items.GroupBy(i => i.Kind))
        {
            var header = new Label
            {
                Text = group.Key switch
                {
                    ItemKind.Variable => "Biến",
                    ItemKind.Secret => "Bí mật — lưu mã hóa trên máy này, không nằm trong công việc",
                    ItemKind.Connection => "Kết nối API (⚙ Cài đặt → Kết nối API)",
                    _ => "Đường dẫn file / thư mục"
                },
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(0, grid.Controls.Count == 0 ? 4 : 16, 0, 4)
            };
            grid.Controls.Add(header);
            grid.SetColumnSpan(header, 3);
            foreach (var item in group) AddRow(grid, item);
        }

        var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
        var skip = new Button { Text = "Để sau", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        var apply = new Button { Text = "Áp dụng", AutoSize = true, MinimumSize = new Size(110, 0) };
        apply.Click += (_, _) => Apply();
        bottom.Controls.AddRange([skip, apply]);
        root.Controls.Add(bottom);
        AcceptButton = apply;
        CancelButton = skip;
        Controls.Add(root);
    }

    private void AddRow(TableLayoutPanel grid, Item item)
    {
        var caption = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 4, 8, 4) };
        caption.Controls.Add(new Label { Text = item.Key, AutoSize = true, UseMnemonic = false, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0) });
        var detail = item.Description.Length > 0 ? item.Description + Environment.NewLine : "";
        detail += "Dùng trong: " + item.JobNames;
        caption.Controls.Add(new Label
        {
            Text = detail,
            UseMnemonic = false,
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(262), 0),
            ForeColor = UiText.Muted,
            Margin = new Padding(0, 2, 0, 0)
        });
        if (item.Kind == ItemKind.Path) caption.Controls.RemoveAt(0);
        grid.Controls.Add(caption);

        var status = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0) };
        _status[item] = status;

        switch (item.Kind)
        {
            case ItemKind.Variable:
            {
                var box = Input(item.Suggested ?? item.Value);
                if (item.Mixed) box.PlaceholderText = "khác nhau giữa các công việc — để nguyên nếu không đổi";
                if (item.Suggested != null) _tips.SetToolTip(box, $"Điền theo giá trị bạn nhập lần trước (giá trị trong mẫu: \"{item.Value}\").");
                var env = OverridingEnvironment(item.Key);
                if (env != null) _tips.SetToolTip(box, $"Khi chạy ở môi trường \"{env}\", giá trị của môi trường được dùng thay cho giá trị này.");
                box.TextChanged += (_, _) => UpdateVariableStatus(item, box);
                grid.Controls.Add(box);
                // 🎲: giá trị ngẫu nhiên sinh mới ở mỗi lần chạy (=hoten(), =email()…) — hợp với dữ liệu kiểm thử.
                var extra = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
                var dice = new Button { Text = "⚄ Ngẫu nhiên", AutoSize = true, Margin = new Padding(6, 4, 0, 0) };
                _tips.SetToolTip(dice, "Giá trị ngẫu nhiên — sinh mới ở mỗi lần chạy (họ tên, email, số điện thoại, số, ngày…)");
                dice.Click += (_, _) => RandomValueMenu.Show(dice, (_, formula) => box.Text = formula);
                status.MaximumSize = new Size(LogicalToDeviceUnits(190), 0);
                extra.Controls.AddRange([dice, status]);
                grid.Controls.Add(extra);
                UpdateVariableStatus(item, box);
                _inputs[item] = box;
                break;
            }
            case ItemKind.Secret:
            {
                var box = Input("");
                box.UseSystemPasswordChar = true;
                box.PlaceholderText = item.Exists ? "đã lưu — để trống để giữ nguyên" : "chưa có — nhập để lưu";
                box.TextChanged += (_, _) => SetStatus(status, item.Exists || box.TextLength > 0, item.Exists ? "✔ đã có" : "✔ sẽ lưu", "⚠ chưa có");
                grid.Controls.Add(box);
                grid.Controls.Add(status);
                SetStatus(status, item.Exists, "✔ đã có", "⚠ chưa có");
                _inputs[item] = box;
                break;
            }
            case ItemKind.Connection:
            {
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
                SetStatus(status, item.Exists, "✔ đã khai báo", "⚠ chưa có kết nối tên này");
                status.Margin = new Padding(0, 6, 8, 0);
                row.Controls.Add(status);
                if (!item.Exists && _openSettings != null)
                {
                    var open = new Button { Text = "Mở Cài đặt…", AutoSize = true };
                    open.Click += (_, _) =>
                    {
                        _openSettings();
                        bool exists = SettingsStore.Current.ApiConnections.Any(c => c.Name.Trim().Equals(item.Key, StringComparison.OrdinalIgnoreCase));
                        SetStatus(status, exists, "✔ đã khai báo", "⚠ chưa có kết nối tên này");
                        open.Visible = !exists;
                    };
                    row.Controls.Add(open);
                }
                grid.Controls.Add(row);
                grid.SetColumnSpan(row, 2);
                break;
            }
            case ItemKind.Path:
            {
                var box = Input(item.Value);
                var browse = new Button { Text = "Duyệt…", AutoSize = true, Margin = new Padding(6, 2, 0, 2) };
                browse.Click += (_, _) => Browse(item, box);
                grid.Controls.Add(box);
                grid.Controls.Add(browse);
                _inputs[item] = box;
                break;
            }
        }
    }

    private TextBox Input(string text) => new() { Text = text, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 4) };

    private static void SetStatus(Label label, bool ok, string okText, string badText)
    {
        label.Text = ok ? okText : badText;
        label.ForeColor = ok ? Color.FromArgb(0, 110, 0) : Color.FromArgb(190, 90, 0);
    }

    private void UpdateVariableStatus(Item item, TextBox box)
    {
        if (TestData.Preview(box.Text) is { } preview)
        {
            _status[item].Text = preview.Ok ? "vd: " + preview.Text : "✖ " + preview.Text;
            _status[item].ForeColor = preview.Ok ? RandomValueMenu.FormulaColor : Color.FromArgb(190, 40, 20);
            _tips.SetToolTip(_status[item], preview.Ok ? "Mỗi lần chạy sinh một giá trị mới; giá trị đã dùng được ghi trong nhật ký." : preview.Text);
            return;
        }
        bool placeholder = TemplateSetup.IsPlaceholder(box.Text);
        _status[item].Text = placeholder ? "⚠ giá trị mẫu — cần sửa" : box.Text != item.Value ? "✎ sẽ cập nhật" : "";
        _status[item].ForeColor = placeholder ? Color.FromArgb(190, 90, 0) : Color.FromArgb(0, 110, 0);
    }

    private static string? OverridingEnvironment(string variable)
    {
        var s = SettingsStore.Current;
        var env = s.Environments.FirstOrDefault(e => e.Name.Equals(s.CurrentEnvironment, StringComparison.OrdinalIgnoreCase));
        return env != null && env.Variables.Any(v => v.Name.Equals(variable, StringComparison.OrdinalIgnoreCase)) ? env.Name : null;
    }

    /// <summary>Chọn file / thư mục; phần động phía sau (vd \{{today:yyyy-MM}}) được giữ nguyên, đường dẫn trong hồ sơ người dùng ghi dạng %USERPROFILE%.</summary>
    private void Browse(Item item, TextBox box)
    {
        var current = box.Text.Trim();
        int dynamic = current.IndexOf("{{", StringComparison.Ordinal);
        string suffix = "";
        if (dynamic >= 0)
        {
            int cut = current.LastIndexOf('\\', dynamic);
            suffix = cut >= 0 ? current[(cut + 1)..] : current[dynamic..];
            current = cut >= 0 ? current[..cut] : "";
        }
        var expanded = Environment.ExpandEnvironmentVariables(current);
        string? chosen = null;
        if (item.IsFolder || suffix.Length > 0)
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = Directory.Exists(expanded) ? expanded : "", UseDescriptionForTitle = true, Description = "Chọn thư mục" };
            if (dlg.ShowDialog(this) == DialogResult.OK) chosen = dlg.SelectedPath;
        }
        else
        {
            using var dlg = new OpenFileDialog { Title = "Chọn file", CheckFileExists = false, FileName = Path.GetFileName(expanded) };
            var dir = Path.GetDirectoryName(expanded);
            if (dir != null && Directory.Exists(dir)) dlg.InitialDirectory = dir;
            if (dlg.ShowDialog(this) == DialogResult.OK) chosen = dlg.FileName;
        }
        if (chosen == null) return;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (chosen.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase)) chosen = "%USERPROFILE%" + chosen[profile.Length..];
        box.Text = suffix.Length > 0 ? chosen.TrimEnd('\\') + "\\" + suffix : chosen;
    }

    private void Apply()
    {
        var left = _inputs.Where(kv => kv.Key.Kind == ItemKind.Variable && TemplateSetup.IsPlaceholder(kv.Value.Text)).Select(kv => kv.Key.Key).ToList();
        if (left.Count > 0 &&
            MessageBox.Show(this, $"Còn {left.Count} biến đang để giá trị mẫu ({string.Join(", ", left)}) — công việc sẽ chạy lỗi tới khi bạn sửa.\n\nVẫn áp dụng?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        Changes = TemplateSetup.Apply(_inputs.ToDictionary(kv => kv.Key, kv => kv.Value.Text));
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
