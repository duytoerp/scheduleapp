using System.Diagnostics;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>
/// Mô tả việc cần tự động hóa bằng lời → Claude dựng flow → xem trước, yêu cầu sửa tiếp, rồi áp dụng vào trình soạn.
/// </summary>
internal sealed class AiFlowForm : BaseForm
{
    private readonly FlowGenerator.Context _context;
    private FlowGenerator? _generator;
    private CancellationTokenSource? _cts;

    private readonly Label _lblPrompt = new() { AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
    private readonly TextBox _txtPrompt = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly Button _btnExamples = new() { Text = "Ví dụ ▾", AutoSize = true };
    private readonly RadioButton _rdoReplace = new() { AutoSize = true, Checked = true, Margin = new Padding(0, 4, 12, 0) };
    private readonly RadioButton _rdoInsert = new() { AutoSize = true, Margin = new Padding(0, 4, 12, 0) };
    private readonly CheckBox _chkWindows = new() { Text = "Gửi kèm danh sách cửa sổ đang mở", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly Button _btnGenerate = new() { Text = "✨ Tạo flow", AutoSize = true, MinimumSize = new Size(150, 0) };
    private readonly Button _btnRestart = new() { Text = "Bắt đầu lại", AutoSize = true, Visible = false };
    private readonly Label _lblStatus = new() { AutoSize = true, ForeColor = UiText.Muted, Margin = new Padding(8, 8, 0, 0) };
    private readonly ListView _preview = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, ShowItemToolTips = true
    };
    private readonly TextBox _txtInfo = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White };
    private readonly Button _btnApply = new() { Text = "Áp dụng vào flow", AutoSize = true, MinimumSize = new Size(150, 0), Enabled = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Stopwatch _elapsed = new();
    private string _progress = "";

    public FlowGenerator.Result? Result { get; private set; }

    public AiFlowForm(FlowGenerator.Context context)
    {
        _context = context;
        SuspendLayout();
        Text = "Tạo flow bằng AI";
        Size = new Size(1000, 760);
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(12);
        BuildUi();
        ResumeLayout(true);
        UpdatePromptLabel();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, LogicalToDeviceUnits(120)));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(_lblPrompt, 0, 0);
        header.Controls.Add(_btnExamples, 1, 0);
        root.Controls.Add(header);
        root.Controls.Add(_txtPrompt);

        int count = _context.Steps.Count;
        _rdoReplace.Text = count == 0 ? "Tạo flow mới" : $"Viết lại / sửa cả flow ({count} bước hiện có)";
        _rdoInsert.Text = count == 0 ? "Chỉ thêm bước" : $"Chỉ thêm bước mới vào sau bước {_context.InsertAt}";
        _rdoInsert.Enabled = count > 0;
        _rdoReplace.CheckedChanged += (_, _) => UpdatePromptLabel();
        var options = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
        options.Controls.AddRange([_rdoReplace, _rdoInsert, _chkWindows]);
        root.Controls.Add(options);

        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6) };
        actions.Controls.AddRange([_btnGenerate, _btnRestart, _lblStatus]);
        root.Controls.Add(actions);

        _preview.Columns.Add("#", 48, HorizontalAlignment.Right);
        _preview.Columns.Add("Bước đề xuất", 640);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
        split.Panel1.Controls.Add(_preview);
        split.Panel2.Controls.Add(_txtInfo);
        root.Controls.Add(split);

        var bottom = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 8, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(new Label
        {
            Text = "Được gửi tới Anthropic để tạo flow: mô tả của bạn, các bước hiện có (cả chữ, URL, nội dung trong bước), tên và giá trị biến, " +
                   "tên công việc / kết nối API (và danh sách cửa sổ nếu chọn). Mật khẩu, token, khóa API nhận ra được (mục 🔑 Bí mật, ⚙ Cài đặt, " +
                   "ô mật khẩu, header / tham số tên password, token, key…) được thay bằng chữ giữ chỗ trước khi gửi và tự điền lại sau. " +
                   "Bí mật gõ thẳng ở chỗ khác có thể không nhận ra — nên dùng {{secret:Tên}}. Hãy xem lại flow và chạy thử trước khi đặt lịch.",
            AutoSize = true,
            MaximumSize = new Size(LogicalToDeviceUnits(640), 0),
            ForeColor = UiText.Muted
        }, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0) };
        var close = new Button { Text = "Đóng", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([close, _btnApply]);
        bottom.Controls.Add(buttons, 1, 0);
        root.Controls.Add(bottom);
        CancelButton = close;
        Controls.Add(root);

        _btnGenerate.Click += async (_, _) => await GenerateAsync();
        _btnRestart.Click += (_, _) => Restart();
        _btnApply.Click += (_, _) => Apply();
        _btnExamples.Click += (_, _) => ShowExamples();
        _txtPrompt.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && e.Control && _cts == null)
            {
                e.SuppressKeyPress = true;
                await GenerateAsync();
            }
        };
        _timer.Tick += (_, _) => ShowProgress();
        Shown += (_, _) =>
        {
            split.SplitterDistance = (int)(split.Height * 0.62);   // xem trước các bước chiếm phần lớn
            _txtPrompt.Focus();
        };
        FormClosing += (_, _) => _cts?.Cancel();
    }

    private void UpdatePromptLabel()
    {
        _lblPrompt.Text = _generator?.HasResult == true
            ? "Muốn sửa gì thêm? (vd: dùng Edge thay Chrome · thêm bước gửi thông báo khi xong · bỏ bước đăng nhập) — Ctrl+Enter để gửi"
            : _rdoInsert.Checked
                ? "Mô tả các bước cần thêm (vd: sau khi lưu, gửi thông báo kèm ảnh màn hình) — Ctrl+Enter để gửi"
                : "Mô tả việc cần tự động hóa: ứng dụng / trang web nào, dữ liệu lấy từ đâu, ghi vào đâu, khi nào báo… — Ctrl+Enter để gửi";
    }

    private void ShowExamples()
    {
        var menu = new ContextMenuStrip();
        foreach (var (title, text) in Examples)
            menu.Items.Add(title, null, (_, _) =>
            {
                _txtPrompt.Text = text;
                _txtPrompt.Focus();
                _txtPrompt.SelectionStart = _txtPrompt.TextLength;
            });
        menu.Closed += (_, _) => BeginInvoke(new MethodInvoker(menu.Dispose));
        menu.Show(_btnExamples, new Point(0, _btnExamples.Height));
    }

    private static readonly (string Title, string Text)[] Examples =
    [
        ("Web: nhập dữ liệu từ Excel",
            "Đọc file khach-hang.xlsx trong thư mục Documents (cột Họ tên, Email, Số điện thoại, Trạng thái). Với mỗi dòng chưa có Trạng thái: " +
            "mở Edge vào trang https://example.com/dang-ky, điền Họ tên, Email, Số điện thoại, bấm Đăng ký, rồi ghi \"Đã nhập\" và giờ hiện tại vào cột Trạng thái của dòng đó. " +
            "Xong thì gửi thông báo đã nhập bao nhiêu dòng."),
        ("API: báo cáo hằng ngày",
            "Gọi API Dynamics 365 lấy các cơ hội (opportunities) tạo hôm nay gồm tên, giá trị ước tính và người phụ trách; " +
            "ghi ra file CSV bao-cao-ngày-hôm-nay.csv trong Documents rồi gửi thông báo tổng số cơ hội."),
        ("Email: lưu file đính kèm",
            "Khi có email hóa đơn mới: với mỗi file PDF đính kèm, chép vào thư mục D:\\HoaDon\\năm-tháng hiện tại và ghi tên file, người gửi, tiêu đề, ngày nhận vào hoa-don.xlsx. " +
            "Nếu không có file PDF thì gửi thông báo nhắc kiểm tra email."),
        ("Ứng dụng Windows: Notepad",
            "Hỏi tôi nhập nội dung công việc hôm nay, mở Notepad, gõ tiêu đề \"Báo cáo ngày\" kèm ngày hôm nay và nội dung vừa nhập, " +
            "lưu vào Documents với tên bao-cao-ngày.txt rồi đóng Notepad."),
        ("Theo dõi giá trên trang web",
            "Mở Chrome vào https://example.com/gia-vang, đọc giá bán hiện tại; ghi giá và thời gian vào gia-vang.csv trong Documents; " +
            "nếu giá cao hơn 9.000.000 thì gửi thông báo kèm giá."),
        ("Dọn thư mục Downloads",
            "Với mỗi file trong Downloads cũ hơn 30 ngày: chuyển file PDF sang Documents\\PDF, file ảnh sang Pictures\\Tai-ve, " +
            "ghi nhật ký tên file đã chuyển, cuối cùng gửi thông báo số file đã dọn.")
    ];

    private FlowGenerator.Mode CurrentMode => _rdoInsert.Checked ? FlowGenerator.Mode.Insert : FlowGenerator.Mode.Replace;

    private async Task GenerateAsync()
    {
        if (_cts != null)
        {
            _cts.Cancel();   // nút đang là "Dừng"
            return;
        }
        if (!AiClient.IsConfigured)
        {
            MessageBox.Show(this, "Chưa nhập khóa API Claude.\n\nVào ⚙ Cài đặt → Tích hợp → nhập khóa API (lấy tại console.anthropic.com), rồi thử lại.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var prompt = _txtPrompt.Text.Trim();
        if (prompt.Length == 0)
        {
            MessageBox.Show(this, "Hãy mô tả việc cần tự động hóa (hoặc bấm \"Ví dụ\").", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            _txtPrompt.Focus();
            return;
        }

        if (_generator == null)
        {
            if (_chkWindows.Checked)
                _context.OpenWindows = [.. WindowHelper.GetOpenWindows().Select(w => $"{w.Title} [{w.ProcessName}]")];
            _generator = new FlowGenerator(_context);
            _generator.Progress += p => _progress = p;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            var result = await _generator.SendAsync(prompt, CurrentMode, _cts.Token);
            Result = result;
            ShowResult(result);
            _txtPrompt.Clear();
            _lblStatus.ForeColor = result.Problems.Count == 0 ? Color.FromArgb(0, 110, 0) : Color.FromArgb(180, 90, 0);
            _lblStatus.Text = (result.Problems.Count == 0 ? "✔ " : "⚠ ") +
                              $"{result.Steps.Count} bước · {_elapsed.Elapsed.TotalSeconds:0} giây" +
                              (result.Repairs > 0 ? $" · AI đã tự sửa lỗi {result.Repairs} lần" : "") +
                              (result.Problems.Count > 0 ? $" · còn {result.Problems.Count} lỗi cần sửa tay" : "");
        }
        catch (OperationCanceledException)
        {
            _lblStatus.ForeColor = UiText.Muted;
            _lblStatus.Text = "Đã dừng.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Text.Json.JsonException or System.Net.Http.HttpRequestException)
        {
            _lblStatus.ForeColor = Color.FromArgb(180, 40, 20);
            _lblStatus.Text = "✖ " + ex.Message;
            Log.Warn("Tạo flow bằng AI lỗi: " + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _txtPrompt.ReadOnly = busy;
        _btnExamples.Enabled = !busy;
        _btnApply.Enabled = !busy && Result != null;
        _btnRestart.Enabled = !busy;
        bool started = _generator?.HasResult == true;
        _rdoReplace.Enabled = !busy && !started;
        _rdoInsert.Enabled = !busy && !started && _context.Steps.Count > 0;
        _chkWindows.Enabled = !busy && _generator == null;
        _btnRestart.Visible = started;
        _btnGenerate.Text = busy ? "■ Dừng" : started ? "↻ Gửi yêu cầu sửa" : "✨ Tạo flow";
        UseWaitCursor = false;
        if (busy)
        {
            _elapsed.Restart();
            _progress = "Đang tạo flow…";
            _lblStatus.ForeColor = UiText.Muted;
            ShowProgress();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _elapsed.Stop();
        }
        UpdatePromptLabel();
    }

    private void ShowProgress() =>
        _lblStatus.Text = $"{_progress} {_elapsed.Elapsed.TotalSeconds:0} giây · {AiClient.Model} (flow dài có thể mất 1–2 phút)";

    private void ShowResult(FlowGenerator.Result r)
    {
        int offset = r.Mode == FlowGenerator.Mode.Insert ? _context.InsertAt : 0;
        var whole = r.Mode == FlowGenerator.Mode.Replace
            ? r.Steps
            : [.. _context.Steps.Take(_context.InsertAt), .. r.Steps, .. _context.Steps.Skip(_context.InsertAt)];
        var depth = FlowStructure.Build(whole).Depth;

        _preview.BeginUpdate();
        _preview.Items.Clear();
        for (int i = 0; i < r.Steps.Count; i++)
        {
            var s = r.Steps[i];
            var text = new string(' ', Math.Min(depth[offset + i], 8) * 4) + ActionStep.TypeNames[s.Type] + " — " + s.Describe();
            _preview.Items.Add(new ListViewItem([(offset + i + 1).ToString(), text])
            {
                ForeColor = s.IsControl ? StepVisuals.Accent(s.Type) : SystemColors.WindowText,
                ToolTipText = text.Trim()
            });
        }
        _preview.EndUpdate();
        _preview.Columns[1].Width = -1;   // vừa nội dung
        if (_preview.Columns[1].Width < LogicalToDeviceUnits(600)) _preview.Columns[1].Width = LogicalToDeviceUnits(600);

        var nl = Environment.NewLine;
        var info = new List<string>();
        if (r.Summary.Length > 0) info.Add("📝 " + r.Summary);
        if (r.Name.Length > 0 && r.Mode == FlowGenerator.Mode.Replace) info.Add("Tên đề xuất: " + r.Name);
        if (r.Variables.Count > 0) info.Add("Biến khai báo thêm: " + string.Join(", ", r.Variables.Select(v => $"{v.Name} = \"{v.Value}\"")));
        if (r.Mode == FlowGenerator.Mode.Replace && (r.Schedule != null || r.Triggers.Count > 0))
            info.Add("⏰ Lịch chạy đề xuất (áp dụng cùng flow): " + string.Join("; ",
                (r.Schedule != null ? [r.Schedule.Describe() + (r.SkipHolidays ? ", bỏ qua ngày nghỉ lễ" : "")] : Array.Empty<string>())
                .Concat(r.Triggers.Select(t => t.Describe()))));
        if (r.Notes.Count > 0) info.Add("⚠ Cần kiểm tra / điền trước khi chạy:" + nl + string.Join(nl, r.Notes.Select(n => "   • " + n)));
        if (r.Problems.Count > 0)
            info.Add("✖ Lỗi AI chưa tự sửa được — áp dụng rồi sửa tay, hoặc gửi yêu cầu sửa:" + nl + string.Join(nl, r.Problems.Select(p => "   • " + p)));
        _txtInfo.Text = string.Join(nl + nl, info);
    }

    private void Restart()
    {
        _generator = null;
        Result = null;
        _context.OpenWindows = [];
        _preview.Items.Clear();
        _txtInfo.Clear();
        _lblStatus.Text = "";
        SetBusy(false);
        _txtPrompt.Focus();
    }

    private void Apply()
    {
        if (Result is not { } r) return;
        if (r.Problems.Count > 0 &&
            MessageBox.Show(this, $"Flow còn {r.Problems.Count} lỗi AI chưa tự sửa được (xem phần dưới). Vẫn áp dụng để sửa tay?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
