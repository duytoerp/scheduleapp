using System.Text.Json;
using ScheduleApp.Models;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>Cài đặt chung: chạy flow, ngày nghỉ, kênh thông báo (Telegram / email / webhook).</summary>
internal sealed class SettingsForm : BaseForm
{
    private readonly AppSettings _s;

    // Chung
    private readonly CheckBox _chkScreenshot = new() { Text = "Chụp màn hình khi bước bị lỗi (lưu trong thư mục log; chỉ gửi kèm thông báo ở kênh có tích \"Kèm ảnh\")", AutoSize = true };
    private readonly NumericUpDown _numKeep = new() { Minimum = 0, Maximum = 3650, Width = 70 };
    private readonly CheckBox _chkSafe = new() { Text = "Chế độ an toàn: tạm dừng flow và hỏi khi tôi dùng chuột/bàn phím trong lúc flow chạy", AutoSize = true };
    private readonly CheckBox _chkAwake = new() { Text = "Không cho máy ngủ / tắt màn hình khi flow đang chạy", AutoSize = true };
    private readonly CheckBox _chkOverlay = new() { Text = "Hiện khung trạng thái ở góc phải màn hình khi flow đang chạy", AutoSize = true };
    private readonly TextBox _txtLoginHosts = new() { Width = 360, PlaceholderText = "vd adfs.contoso.com, sso.contoso.vn" };

    // Ngày nghỉ
    private readonly TextBox _txtHolidays = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true };

    // Thông báo
    private readonly CheckBox _chkTelegram = new() { Text = "Gửi qua Telegram", AutoSize = true };
    private readonly TextBox _txtTgToken = new() { Width = 380, UseSystemPasswordChar = true };
    private readonly TextBox _txtTgChat = new() { Width = 200 };
    private readonly CheckBox _chkTgPhoto = new() { Text = "Kèm ảnh chụp màn hình lỗi", AutoSize = true };
    private readonly CheckBox _chkEmail = new() { Text = "Gửi email (SMTP)", AutoSize = true };
    private readonly TextBox _txtHost = new() { Width = 200 };
    private readonly NumericUpDown _numSmtpPort = new() { Minimum = 1, Maximum = 65535, Width = 70 };
    private readonly CheckBox _chkSsl = new() { Text = "SSL/TLS", AutoSize = true };
    private readonly CheckBox _chkSmtpPlain = new()
    {
        Text = "Cho phép gửi không mã hóa (máy chủ nội bộ)", AutoSize = true, Margin = new Padding(22, 0, 3, 3)
    };
    private readonly Label _lblSmtpWarning = new()
    {
        Text = "⚠ Mật khẩu và nội dung thư đi trên mạng dạng chữ thường — chỉ dùng với máy chủ chuyển tiếp trong mạng nội bộ tin cậy (vd cổng 25).",
        AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = Theme.Danger, Margin = new Padding(40, 0, 3, 4), Visible = false
    };
    private readonly TextBox _txtUser = new() { Width = 220 };
    private readonly TextBox _txtPass = new() { Width = 180, UseSystemPasswordChar = true };
    private readonly TextBox _txtFrom = new() { Width = 220 };
    private readonly TextBox _txtTo = new() { Width = 380 };
    private readonly CheckBox _chkAttach = new() { Text = "Đính kèm ảnh chụp màn hình lỗi", AutoSize = true };
    private readonly CheckBox _chkWebhook = new() { Text = "Gửi tới webhook (Teams / Slack / Discord / Google Chat)", AutoSize = true };
    private readonly TextBox _txtWebhook = new() { Width = 520, UseSystemPasswordChar = true };
    private readonly CheckBox _chkTgCommands = new() { Text = "Nhận lệnh điều khiển từ chat này (/run, /stop, /status, /screenshot, /new tạo công việc bằng AI…)", AutoSize = true, Margin = new Padding(22, 3, 3, 3) };
    private readonly CheckBox _chkTgUnsafe = new()
    {
        Text = "Cho phép công việc tạo qua Telegram chạy ngay, không cần duyệt (không an toàn)", AutoSize = true, Margin = new Padding(40, 0, 3, 3)
    };
    private readonly Label _lblTgWarning = new() { AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = Theme.Danger, Margin = new Padding(40, 0, 3, 4), Visible = false };

    // Cập nhật
    private readonly TextBox _txtUpdateSource = new() { Width = 420, PlaceholderText = "github:chủ/repo · https://…/version.json · \\\\máy\\thư mục" };
    private readonly TextBox _txtUpdateToken = new() { Width = 300, UseSystemPasswordChar = true };
    private readonly CheckBox _chkUpdateStartup = new() { Text = "Tự kiểm tra khi khởi động", AutoSize = true };

    // Kết nối API
    private readonly ListView _lstApi = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };

    // Tích hợp: AI, hộp thư
    private readonly TextBox _txtAiKey = new() { Width = 380, UseSystemPasswordChar = true };
    private readonly ComboBox _cboAiModel = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 240 };
    private readonly NumericUpDown _numAiTokens = new() { Minimum = 64, Maximum = 32_000, Width = 80, Increment = 256 };
    private readonly ComboBox _cboMailSource = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly TextBox _txtImapHost = new() { Width = 200 };
    private readonly NumericUpDown _numImapPort = new() { Minimum = 1, Maximum = 65535, Width = 70 };
    private readonly CheckBox _chkImapSsl = new() { Text = "SSL", AutoSize = true };
    private readonly CheckBox _chkImapPlain = new() { Text = "Cho phép gửi mật khẩu không mã hóa", AutoSize = true };
    private readonly TextBox _txtImapUser = new() { Width = 220 };
    private readonly TextBox _txtImapPass = new() { Width = 180, UseSystemPasswordChar = true };
    private readonly TextBox _txtMailFolder = new() { Width = 220, PlaceholderText = "trống = Hộp thư đến" };
    private readonly CheckBox _chkMarkRead = new() { Text = "Đánh dấu đã đọc sau khi xử lý (khuyên dùng)", AutoSize = true };

    private const string Unchanged = "••••••••";

    /// <summary>Người dùng vừa cài bản mới — ứng dụng cần thoát để script cập nhật thay file.</summary>
    public bool ExitForUpdate { get; private set; }

    public SettingsForm()
    {
        // Sửa trên bản sao — chỉ ghi lại khi bấm Lưu.
        _s = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(SettingsStore.Current, JsonDefaults.Options), JsonDefaults.Options)!;

        SuspendLayout();
        Text = "Cài đặt — ScheduleApp";
        Size = new Size(820, 660);
        MinimumSize = new Size(740, 560);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Padding = new Padding(10);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(GeneralTab());
        tabs.TabPages.Add(HolidaysTab());
        tabs.TabPages.Add(NotifyTab());
        tabs.TabPages.Add(ApiTab());
        tabs.TabPages.Add(IntegrationTab());

        var ok = new Button { Text = "Lưu", AutoSize = true, MinimumSize = new Size(90, 0) };
        var cancel = new Button { Text = "Hủy", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Save();
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([cancel, ok]);

        Controls.Add(tabs);
        Controls.Add(buttons);
        ResumeLayout(true);
        LoadValues();
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 6, 3) };

    private static FlowLayoutPanel Line(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        p.Controls.AddRange(controls);
        return p;
    }

    private static Label Hint(string text) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = UiText.Muted, Margin = new Padding(22, 0, 3, 8) };

    private TabPage GeneralTab()
    {
        var page = new TabPage("Chung") { Padding = new Padding(10), UseVisualStyleBackColor = true, AutoScroll = true };
        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoScroll = true };
        col.Controls.Add(_chkScreenshot);
        col.Controls.Add(Line(Caption("   Tự xóa ảnh lỗi cũ hơn (ngày, 0 = giữ mãi):"), _numKeep));
        col.Controls.Add(_chkOverlay);
        col.Controls.Add(Hint("Cho biết flow nào đang chạy, đang ở bước mấy và chạy được bao lâu; có nút ⏸ Tạm dừng (dừng trước bước kế tiếp rồi chạy từng bước) " +
                              "và ■ Dừng. Khung không lấy focus, không lọt vào ảnh chụp màn hình và tự dời sang góc khác nếu flow cần click vào chỗ nó đang che."));
        col.Controls.Add(_chkSafe);
        col.Controls.Add(Hint("Khi bật, nếu bạn click / gõ phím vào ứng dụng khác lúc flow đang chạy, flow tạm dừng trước bước kế tiếp và hỏi chạy tiếp hay dừng. " +
                              "Tránh việc flow gõ nhầm vào chỗ bạn đang làm."));
        col.Controls.Add(_chkAwake);
        col.Controls.Add(Line(Caption("Trang đăng nhập riêng (ADFS / SSO):"), _txtLoginHosts));
        col.Controls.Add(Hint("Bước \"Dynamics 365 → Đăng nhập Microsoft\" chỉ điền mật khẩu / mã xác thực vào https://login.microsoftonline.com " +
                              "hoặc https://login.microsoft.com. Tổ chức đăng nhập qua trang riêng thì ghi tên máy của trang đó ở đây (cách nhau dấu phẩy, chỉ qua https)."));
        col.Controls.Add(Caption("Dòng lệnh:"));
        col.Controls.Add(Hint("ScheduleApp.exe --run \"Tên công việc\"   ·   ScheduleApp.exe --stop   ·   ScheduleApp.exe --minimized\n" +
                              "Chuột phải một công việc → \"Tạo shortcut trên Desktop\" để chạy bằng 1 cú nhấp đúp."));

        var check = new Button { Text = "Kiểm tra ngay", AutoSize = true };
        check.Click += async (_, _) => await CheckUpdateAsync(check);
        col.Controls.Add(Caption($"Cập nhật (đang dùng bản {UpdateService.Current}):"));
        col.Controls.Add(Line(Caption("   Nguồn bản mới:"), _txtUpdateSource));
        col.Controls.Add(Line(Caption("   Token GitHub (repo riêng tư):"), _txtUpdateToken, _chkUpdateStartup, check));
        col.Controls.Add(Hint("github:chủ/repo lấy file ScheduleApp.exe từ bản phát hành (Release) mới nhất trên GitHub. Hoặc đặt ScheduleApp.exe + version.json " +
                              "vào một thư mục dùng chung (\\\\máy-chủ\\ScheduleApp) để cả phòng cập nhật — xem README."));
        page.Controls.Add(col);
        return page;
    }

    private async Task CheckUpdateAsync(Button button)
    {
        button.Enabled = false;
        UseWaitCursor = true;
        try
        {
            var info = await UpdateService.CheckAsync(CancellationToken.None, ReadUpdate());
            if (info == null)
            {
                MessageBox.Show(this, $"Bạn đang dùng bản mới nhất ({UpdateService.Current}).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var dlg = new UpdateForm(info);
            dlg.ShowDialog(this);
            if (dlg.ExitRequested)
            {
                ExitForUpdate = true;
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không kiểm tra được bản mới:\n" + UpdateService.Explain(ex), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
            button.Enabled = true;
        }
    }

    private UpdateSettings ReadUpdate() => new()
    {
        Source = _txtUpdateSource.Text.Trim(),
        Token = _txtUpdateToken.Text == Unchanged ? _s.Update.Token : _txtUpdateToken.Text.Trim().Length == 0 ? "" : Protector.Protect(_txtUpdateToken.Text.Trim()),
        CheckOnStartup = _chkUpdateStartup.Checked,
        SkippedVersion = _s.Update.SkippedVersion
    };

    private TabPage ApiTab()
    {
        var page = new TabPage("Kết nối API") { Padding = new Padding(10), UseVisualStyleBackColor = true };
        _lstApi.Columns.Add("Tên", 160);
        _lstApi.Columns.Add("URL gốc", 360);
        _lstApi.Columns.Add("Xác thực", 160);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Right, WrapContents = false };
        var add = new Button { Text = "＋ Thêm…", AutoSize = true, MinimumSize = new Size(100, 0) };
        var edit = new Button { Text = "Sửa…", AutoSize = true, MinimumSize = new Size(100, 0) };
        var del = new Button { Text = "Xóa", AutoSize = true, MinimumSize = new Size(100, 0) };
        buttons.Controls.AddRange([add, edit, del]);
        var hint = new Label
        {
            Text = "Khai báo một lần URL gốc + cách xác thực, rồi dùng trong bước \"Gọi API\" (vd Dynamics 365 Web API, Microsoft Graph, API nội bộ). " +
                   "Bí mật được mã hóa bằng Windows DPAPI và không nằm trong file xuất công việc.",
            Dock = DockStyle.Bottom,
            AutoSize = true,
            MaximumSize = new Size(740, 0),
            ForeColor = UiText.Muted,
            Padding = new Padding(0, 6, 0, 0)
        };
        page.Controls.Add(_lstApi);
        page.Controls.Add(buttons);
        page.Controls.Add(hint);

        add.Click += (_, _) => EditConnection(-1);
        edit.Click += (_, _) => EditConnection(_lstApi.SelectedIndices.Count > 0 ? _lstApi.SelectedIndices[0] : -2);
        _lstApi.DoubleClick += (_, _) => EditConnection(_lstApi.SelectedIndices.Count > 0 ? _lstApi.SelectedIndices[0] : -2);
        del.Click += (_, _) =>
        {
            if (_lstApi.SelectedIndices.Count == 0) return;
            int i = _lstApi.SelectedIndices[0];
            if (MessageBox.Show(this, $"Xóa kết nối \"{_s.ApiConnections[i].Name}\"? Các bước đang dùng kết nối này sẽ báo lỗi khi chạy.", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _s.ApiConnections.RemoveAt(i);
            RefreshConnections();
        };
        return page;
    }

    private void EditConnection(int index)
    {
        if (index == -2) return;
        var current = index >= 0 ? _s.ApiConnections[index] : new ApiConnection();
        var others = _s.ApiConnections.Where((_, i) => i != index).Select(c => c.Name).ToList();
        using var dlg = new ApiConnectionForm(current, others);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (index >= 0) _s.ApiConnections[index] = dlg.Connection;
        else _s.ApiConnections.Add(dlg.Connection);
        RefreshConnections();
    }

    private void RefreshConnections()
    {
        _lstApi.Items.Clear();
        foreach (var c in _s.ApiConnections)
            _lstApi.Items.Add(new ListViewItem([c.Name, c.BaseUrl, c.Auth.ToString()]));
    }

    private TabPage IntegrationTab()
    {
        var page = new TabPage("Tích hợp") { Padding = new Padding(10), UseVisualStyleBackColor = true };
        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoScroll = true };

        _cboAiModel.Items.AddRange(AiClient.Models);
        var testAi = new Button { Text = "Thử", AutoSize = true };
        testAi.Click += async (_, _) => await TestAsync("AI", async () =>
        {
            var saved = SettingsStore.Current.Ai;
            SettingsStore.Current.Ai = ReadAi();
            try
            {
                var answer = await AiClient.AskAsync("Trả lời đúng một từ: OK", null, 60_000, CancellationToken.None);
                MessageBox.Show(this, "Claude trả lời: " + answer, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                SettingsStore.Current.Ai = saved;
            }
        }, silent: true);
        col.Controls.Add(Caption("AI — Claude (cho bước \"Hỏi AI\"):"));
        col.Controls.Add(Line(Caption("   Khóa API:"), _txtAiKey, testAi));
        col.Controls.Add(Line(Caption("   Mô hình:"), _cboAiModel, Caption("Số token tối đa:"), _numAiTokens));
        col.Controls.Add(Hint("Lấy khóa API tại console.anthropic.com. Nội dung câu hỏi (và ảnh màn hình nếu bật) được gửi tới Anthropic để xử lý."));

        _cboMailSource.Items.AddRange(["Outlook trên máy (không cần mật khẩu)", "Hộp thư IMAP (Gmail, Outlook.com, Yahoo…)"]);
        _cboMailSource.SelectedIndexChanged += (_, _) => UpdateMailUi();
        _chkImapSsl.CheckedChanged += (_, _) => UpdateMailUi();
        var testMail = new Button { Text = "Thử hộp thư", AutoSize = true };
        testMail.Click += async (_, _) => await TestAsync("hộp thư", async () =>
        {
            var msg = await MailWatcher.TestAsync(ReadInbox(), CancellationToken.None);
            MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }, silent: true);
        col.Controls.Add(Caption("Hộp thư cho trình kích hoạt \"Có email mới\":"));
        col.Controls.Add(Line(Caption("   Nguồn:"), _cboMailSource, testMail));
        col.Controls.Add(Line(Caption("   Máy chủ IMAP:"), _txtImapHost, Caption("Cổng:"), _numImapPort, _chkImapSsl, _chkImapPlain));
        col.Controls.Add(Line(Caption("   Tài khoản:"), _txtImapUser, Caption("Mật khẩu:"), _txtImapPass));
        col.Controls.Add(Line(Caption("   Thư mục:"), _txtMailFolder, _chkMarkRead));
        col.Controls.Add(Hint("Outlook: cần Outlook bản cài trên máy (classic) đang đăng nhập; thư mục con nhập dạng \"Đơn hàng\" hoặc \"Hộp thư đến/Đơn hàng\". " +
                              "Gmail IMAP: imap.gmail.com, cổng 993, SSL, dùng App password. File đính kèm lưu trong thư mục dữ liệu\\email."));
        page.Controls.Add(col);
        return page;
    }

    private void UpdateMailUi()
    {
        bool imap = _cboMailSource.SelectedIndex == (int)MailSource.Imap;
        _txtImapHost.Enabled = _numImapPort.Enabled = _chkImapSsl.Enabled = _txtImapUser.Enabled = _txtImapPass.Enabled = imap;
        _chkImapPlain.Enabled = imap && !_chkImapSsl.Checked;
    }

    private AiSettings ReadAi() => new()
    {
        ApiKey = _txtAiKey.Text == Unchanged ? _s.Ai.ApiKey : _txtAiKey.Text.Trim().Length == 0 ? "" : Protector.Protect(_txtAiKey.Text.Trim()),
        Model = _cboAiModel.Text.Trim().Length == 0 ? AiClient.Models[0] : _cboAiModel.Text.Trim(),
        MaxTokens = (int)_numAiTokens.Value
    };

    private MailInboxSettings ReadInbox() => new()
    {
        Source = (MailSource)Math.Max(0, _cboMailSource.SelectedIndex),
        Host = _txtImapHost.Text.Trim(),
        Port = (int)_numImapPort.Value,
        UseSsl = _chkImapSsl.Checked,
        AllowPlaintext = _chkImapPlain.Checked,
        User = _txtImapUser.Text.Trim(),
        Password = _txtImapPass.Text == Unchanged ? _s.Inbox.Password : _txtImapPass.Text.Length == 0 ? "" : Protector.Protect(_txtImapPass.Text),
        Folder = _txtMailFolder.Text.Trim(),
        MarkAsRead = _chkMarkRead.Checked
    };

    private TabPage HolidaysTab()
    {
        var page = new TabPage("Ngày nghỉ") { Padding = new Padding(10), UseVisualStyleBackColor = true };
        var hint = new Label
        {
            Text = "Mỗi dòng một ngày. \"dd/MM\" = lặp lại hằng năm (vd 30/04), \"dd/MM/yyyy\" = một ngày cụ thể (Tết âm lịch, Giỗ Tổ, ngày nghỉ bù…).\n" +
                   "Công việc bật \"Bỏ qua ngày nghỉ lễ\" sẽ không chạy vào các ngày này; lịch \"Ngày làm việc đầu/cuối tháng\" cũng tránh các ngày này.",
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(700, 0),
            ForeColor = UiText.Muted,
            Padding = new Padding(0, 0, 0, 6)
        };
        page.Controls.Add(_txtHolidays);
        page.Controls.Add(hint);
        return page;
    }

    private TabPage NotifyTab()
    {
        var page = new TabPage("Thông báo") { Padding = new Padding(10), UseVisualStyleBackColor = true };
        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoScroll = true };

        var testTg = new Button { Text = "Gửi thử", AutoSize = true };
        testTg.Click += async (_, _) => await TestAsync("Telegram", () => NotificationService.SendTelegramAsync(ReadTelegram(), "🔔 ScheduleApp", "Tin nhắn thử từ " + Environment.MachineName, null));
        col.Controls.Add(_chkTelegram);
        col.Controls.Add(Line(Caption("   Bot token:"), _txtTgToken));
        col.Controls.Add(Line(Caption("   Chat id:"), _txtTgChat, _chkTgPhoto, testTg));
        col.Controls.Add(_chkTgCommands);
        col.Controls.Add(_lblTgWarning);
        col.Controls.Add(_chkTgUnsafe);
        col.Controls.Add(Hint("Tạo bot với @BotFather để lấy token; nhắn cho bot một tin rồi mở https://api.telegram.org/bot<token>/getUpdates để lấy chat id. " +
                              "Bật nhận lệnh để chạy / dừng công việc, xem trạng thái và chụp màn hình từ điện thoại — chỉ chat riêng của chat id trên mới điều khiển được. " +
                              "Công việc tạo qua Telegram chờ bạn duyệt trên máy (xem từng bước) rồi mới chạy."));
        _txtTgChat.TextChanged += (_, _) => UpdateTelegramWarning();
        _chkTgCommands.CheckedChanged += (_, _) => UpdateTelegramWarning();

        var testMail = new Button { Text = "Gửi thử", AutoSize = true };
        testMail.Click += async (_, _) => await TestAsync("Email", () => NotificationService.SendEmailAsync(ReadEmail(), "Thư thử", "Thư thử từ ScheduleApp trên " + Environment.MachineName, null));
        col.Controls.Add(_chkEmail);
        col.Controls.Add(Line(Caption("   Máy chủ SMTP:"), _txtHost, Caption("Cổng:"), _numSmtpPort, _chkSsl));
        col.Controls.Add(_chkSmtpPlain);
        col.Controls.Add(_lblSmtpWarning);
        _chkSsl.CheckedChanged += (_, _) => UpdateSmtpUi();
        _chkSmtpPlain.CheckedChanged += (_, _) => UpdateSmtpUi();
        col.Controls.Add(Line(Caption("   Tài khoản:"), _txtUser, Caption("Mật khẩu:"), _txtPass));
        col.Controls.Add(Line(Caption("   Người gửi:"), _txtFrom, _chkAttach));
        col.Controls.Add(Line(Caption("   Gửi tới:"), _txtTo, testMail));
        col.Controls.Add(Hint("Gmail: smtp.gmail.com, cổng 587, SSL — dùng \"App password\" thay cho mật khẩu thường. Nhiều người nhận cách nhau dấu phẩy."));

        var testHook = new Button { Text = "Gửi thử", AutoSize = true };
        testHook.Click += async (_, _) => await TestAsync("Webhook", () =>
            NotificationService.SendWebhookAsync(new WebhookSettings { Url = Protector.Unprotect(ReadWebhook().Url) }, "🔔 ScheduleApp", "Tin nhắn thử"));
        col.Controls.Add(_chkWebhook);
        col.Controls.Add(Line(Caption("   URL:"), _txtWebhook, testHook));
        col.Controls.Add(Hint("Mật khẩu, token và URL webhook được mã hóa bằng Windows DPAPI theo tài khoản của bạn. Chọn khi nào gửi trong tab \"Lỗi & thông báo\" của từng công việc."));
        page.Controls.Add(col);
        return page;
    }

    private void LoadValues()
    {
        _chkScreenshot.Checked = _s.ScreenshotOnError;
        _numKeep.Value = Math.Clamp(_s.KeepScreenshotsDays, 0, 3650);
        _chkSafe.Checked = _s.SafeMode;
        _chkAwake.Checked = _s.PreventSleepWhileRunning;
        _chkOverlay.Checked = _s.ShowRunOverlay;
        _txtLoginHosts.Text = string.Join(", ", _s.TrustedLoginHosts);
        _txtHolidays.Text = string.Join(Environment.NewLine, _s.Holidays);

        _chkTelegram.Checked = _s.Telegram.Enabled;
        _txtTgToken.Text = _s.Telegram.BotToken.Length > 0 ? Unchanged : "";
        _txtTgChat.Text = _s.Telegram.ChatId;
        _chkTgPhoto.Checked = _s.Telegram.SendScreenshot;

        _chkEmail.Checked = _s.Email.Enabled;
        _txtHost.Text = _s.Email.Host;
        _numSmtpPort.Value = Math.Clamp(_s.Email.Port, 1, 65535);
        _chkSsl.Checked = _s.Email.UseSsl;
        _txtUser.Text = _s.Email.User;
        _txtPass.Text = _s.Email.Password.Length > 0 ? Unchanged : "";
        _txtFrom.Text = _s.Email.From;
        _txtTo.Text = _s.Email.To;
        _chkAttach.Checked = _s.Email.AttachScreenshot;
        _chkSmtpPlain.Checked = _s.Email.AllowNoTls;
        UpdateSmtpUi();

        _chkWebhook.Checked = _s.Webhook.Enabled;
        _txtWebhook.Text = _s.Webhook.Url.Length > 0 ? Unchanged : "";
        _chkTgCommands.Checked = _s.Telegram.AllowCommands;
        _chkTgUnsafe.Checked = _s.Telegram.RunWithoutApproval;
        UpdateTelegramWarning();

        _txtUpdateSource.Text = _s.Update.Source;
        _txtUpdateToken.Text = _s.Update.Token.Length > 0 ? Unchanged : "";
        _chkUpdateStartup.Checked = _s.Update.CheckOnStartup;

        RefreshConnections();

        _txtAiKey.Text = _s.Ai.ApiKey.Length > 0 ? Unchanged : "";
        _cboAiModel.Text = _s.Ai.Model;
        _numAiTokens.Value = Math.Clamp(_s.Ai.MaxTokens, 64, 32_000);
        _cboMailSource.SelectedIndex = (int)_s.Inbox.Source;
        _txtImapHost.Text = _s.Inbox.Host;
        _numImapPort.Value = Math.Clamp(_s.Inbox.Port, 1, 65535);
        _chkImapSsl.Checked = _s.Inbox.UseSsl;
        _chkImapPlain.Checked = _s.Inbox.AllowPlaintext;
        _txtImapUser.Text = _s.Inbox.User;
        _txtImapPass.Text = _s.Inbox.Password.Length > 0 ? Unchanged : "";
        _txtMailFolder.Text = _s.Inbox.Folder;
        _chkMarkRead.Checked = _s.Inbox.MarkAsRead;
        UpdateMailUi();
    }

    private TelegramSettings ReadTelegram() => new()
    {
        Enabled = _chkTelegram.Checked,
        BotToken = _txtTgToken.Text == Unchanged ? _s.Telegram.BotToken : Protector.Protect(_txtTgToken.Text.Trim()),
        ChatId = _txtTgChat.Text.Trim(),
        SendScreenshot = _chkTgPhoto.Checked,
        AllowCommands = _chkTgCommands.Checked,
        RunWithoutApproval = _chkTgUnsafe.Checked
    };

    /// <summary>Cảnh báo ngay dưới ô nhận lệnh: chat id nhóm / kênh (âm) thì bot không nhận lệnh.</summary>
    private void UpdateTelegramWarning()
    {
        bool group = _chkTgCommands.Checked && long.TryParse(_txtTgChat.Text.Trim(), out long id) && id < 0;
        _lblTgWarning.Text = group ? "⚠ " + TelegramBot.GroupChatWarning : "";
        _lblTgWarning.Visible = group;
    }

    private EmailSettings ReadEmail() => new()
    {
        Enabled = _chkEmail.Checked,
        Host = _txtHost.Text.Trim(),
        Port = (int)_numSmtpPort.Value,
        UseSsl = _chkSsl.Checked,
        User = _txtUser.Text.Trim(),
        Password = _txtPass.Text == Unchanged ? _s.Email.Password : Protector.Protect(_txtPass.Text),
        From = _txtFrom.Text.Trim(),
        To = _txtTo.Text.Trim(),
        AttachScreenshot = _chkAttach.Checked,
        AllowNoTls = !_chkSsl.Checked && _chkSmtpPlain.Checked
    };

    /// <summary>Ô "Cho phép gửi không mã hóa" chỉ dùng được khi tắt SSL/TLS; tick thì hiện cảnh báo đỏ ngay dưới.</summary>
    private void UpdateSmtpUi()
    {
        _chkSmtpPlain.Enabled = !_chkSsl.Checked;
        _lblSmtpWarning.Visible = !_chkSsl.Checked && _chkSmtpPlain.Checked;
    }

    private WebhookSettings ReadWebhook() => new()
    {
        Enabled = _chkWebhook.Checked,
        Url = _txtWebhook.Text == Unchanged ? _s.Webhook.Url : Protector.Protect(_txtWebhook.Text.Trim())
    };

    private async Task TestAsync(string channel, Func<Task> send, bool silent = false)
    {
        UseWaitCursor = true;
        try
        {
            await send();
            if (!silent) MessageBox.Show(this, $"Đã gửi thử qua {channel}.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, (silent ? $"Thử {channel} thất bại:\n" : $"Gửi {channel} thất bại:\n") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    private void Save()
    {
        var holidays = _txtHolidays.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var bad = holidays.FirstOrDefault(h =>
            !DateTime.TryParseExact(h, ["d/M/yyyy", "dd/MM/yyyy"], null, System.Globalization.DateTimeStyles.None, out _) &&
            !DateTime.TryParseExact(h + "/2000", ["d/M/yyyy", "dd/MM/yyyy"], null, System.Globalization.DateTimeStyles.None, out _));
        if (bad != null)
        {
            MessageBox.Show(this, $"Ngày nghỉ \"{bad}\" không đúng định dạng dd/MM hoặc dd/MM/yyyy.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _s.ScreenshotOnError = _chkScreenshot.Checked;
        _s.KeepScreenshotsDays = (int)_numKeep.Value;
        _s.SafeMode = _chkSafe.Checked;
        _s.PreventSleepWhileRunning = _chkAwake.Checked;
        _s.ShowRunOverlay = _chkOverlay.Checked;
        _s.TrustedLoginHosts = [.. _txtLoginHosts.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        _s.Holidays = holidays;
        _s.Telegram = ReadTelegram();
        _s.Email = ReadEmail();
        _s.Webhook = ReadWebhook();
        _s.Update = ReadUpdate();
        _s.Ai = ReadAi();
        _s.Inbox = ReadInbox();
        _s.LastAlive = SettingsStore.Current.LastAlive;
        SettingsStore.Replace(_s);
        Log.Info("Đã lưu cài đặt.");
        DialogResult = DialogResult.OK;
        Close();
    }
}
