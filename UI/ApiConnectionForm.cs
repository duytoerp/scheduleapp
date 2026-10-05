using ScheduleApp.Models;
using ScheduleApp.Services;

namespace ScheduleApp.UI;

/// <summary>Soạn một kết nối API: URL gốc, cách xác thực (kể cả Microsoft Entra ID cho Dynamics 365), header mặc định.</summary>
internal sealed class ApiConnectionForm : BaseForm
{
    private const string Unchanged = "••••••••";
    private static readonly ApiAuthType[] AuthTypes = Enum.GetValues<ApiAuthType>();

    private readonly ApiConnection _original;
    private readonly IReadOnlyCollection<string> _otherNames;
    private readonly TextBox _txtName = new() { Width = 260 };
    private readonly TextBox _txtBase = new() { Width = 460 };
    private readonly ComboBox _cboAuth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly Label _lblUser = Caption("");
    private readonly TextBox _txtUser = new() { Width = 320 };
    private readonly Label _lblSecret = Caption("");
    private readonly TextBox _txtSecret = new() { Width = 320, UseSystemPasswordChar = true };
    private readonly Label _lblTenant = Caption("Tenant ID / tên miền:");
    private readonly TextBox _txtTenant = new() { Width = 320 };
    private readonly Label _lblScope = Caption("Scope:");
    private readonly TextBox _txtScope = new() { Width = 460, PlaceholderText = "trống = <URL gốc>/.default" };
    private readonly Label _lblTokenUrl = Caption("URL lấy token:");
    private readonly TextBox _txtTokenUrl = new() { Width = 460 };
    private readonly TextBox _txtHeaders = new() { Width = 460, Height = 60, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly Label _lblHint = new() { AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = UiText.Muted, Margin = new Padding(3, 8, 3, 3) };

    public ApiConnection Connection { get; private set; }

    public ApiConnectionForm(ApiConnection connection, IReadOnlyCollection<string> otherNames)
    {
        _original = connection;
        _otherNames = otherNames;
        Connection = connection;
        SuspendLayout();
        Text = "Kết nối API";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        _cboAuth.Items.AddRange([
            "Không xác thực", "Bearer token", "Tên đăng nhập + mật khẩu (Basic)", "Khóa API trong header",
            "Tài khoản Windows (NTLM / Kerberos)", "Microsoft Entra ID — Dynamics 365, Graph", "OAuth 2.0 client credentials"
        ]);

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        void Row(Control label, Control field)
        {
            grid.Controls.Add(label);
            grid.Controls.Add(field);
        }
        Row(Caption("Tên kết nối:"), _txtName);
        Row(Caption("URL gốc:"), _txtBase);
        Row(Caption("Xác thực:"), _cboAuth);
        Row(_lblTenant, _txtTenant);
        Row(_lblUser, _txtUser);
        Row(_lblSecret, _txtSecret);
        Row(_lblScope, _txtScope);
        Row(_lblTokenUrl, _txtTokenUrl);
        Row(Caption("Header mặc định\n(mỗi dòng Tên: giá trị):"), _txtHeaders);
        grid.Controls.Add(_lblHint);
        grid.SetColumnSpan(_lblHint, 2);

        var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(90, 0) };
        var cancel = new Button { Text = "Hủy", AutoSize = true, MinimumSize = new Size(90, 0), DialogResult = DialogResult.Cancel };
        var test = new Button { Text = "Thử kết nối", AutoSize = true };
        var d365 = new Button { Text = "Mẫu Dynamics 365", AutoSize = true };
        ok.Click += (_, _) => Save();
        test.Click += async (_, _) => await TestAsync(test);
        d365.Click += (_, _) => FillDynamicsTemplate();
        CancelButton = cancel;
        AcceptButton = null;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([cancel, ok, test, d365]);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        root.Controls.Add(grid);
        root.Controls.Add(buttons);
        Controls.Add(root);
        ResumeLayout(true);

        _txtName.Text = connection.Name;
        _txtBase.Text = connection.BaseUrl;
        _cboAuth.SelectedIndex = Array.IndexOf(AuthTypes, connection.Auth);
        _txtUser.Text = connection.User;
        _txtSecret.Text = connection.Secret.Length > 0 ? Unchanged : "";
        _txtTenant.Text = connection.TenantId;
        _txtScope.Text = connection.Scope;
        _txtTokenUrl.Text = connection.TokenUrl;
        _txtHeaders.Text = Protector.Unprotect(connection.Headers).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        _cboAuth.SelectedIndexChanged += (_, _) => UpdateUi();
        UpdateUi();
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 8, 3) };

    private ApiAuthType CurrentAuth => AuthTypes[Math.Max(0, _cboAuth.SelectedIndex)];

    private void UpdateUi()
    {
        var a = CurrentAuth;
        bool oauth = a is ApiAuthType.EntraId or ApiAuthType.OAuthClientCredentials;
        _lblUser.Visible = _txtUser.Visible = a is ApiAuthType.Basic or ApiAuthType.ApiKey || oauth;
        _lblSecret.Visible = _txtSecret.Visible = a is ApiAuthType.Bearer or ApiAuthType.Basic or ApiAuthType.ApiKey || oauth;
        _lblTenant.Visible = _txtTenant.Visible = a == ApiAuthType.EntraId;
        _lblScope.Visible = _txtScope.Visible = oauth;
        _lblTokenUrl.Visible = _txtTokenUrl.Visible = a == ApiAuthType.OAuthClientCredentials;
        _lblUser.Text = a switch
        {
            ApiAuthType.Basic => "Tên đăng nhập:",
            ApiAuthType.ApiKey => "Tên header (trống = x-api-key):",
            _ => "Client ID (Application ID):"
        };
        _lblSecret.Text = a switch
        {
            ApiAuthType.Bearer => "Token:",
            ApiAuthType.Basic => "Mật khẩu:",
            ApiAuthType.ApiKey => "Khóa API:",
            _ => "Client secret:"
        };
        _lblHint.Text = a switch
        {
            ApiAuthType.EntraId =>
                "Dynamics 365 / Dataverse: đăng ký ứng dụng trong Azure Portal (App registrations) → tạo Client secret → thêm ứng dụng làm " +
                "Application User trong Power Platform admin center và gán security role. URL gốc vd https://contoso.crm5.dynamics.com/api/data/v9.2/ " +
                "— scope tự lấy là https://contoso.crm5.dynamics.com/.default. Microsoft Graph: URL gốc https://graph.microsoft.com/v1.0/ .",
            ApiAuthType.Windows => "Dùng tài khoản Windows đang đăng nhập — phù hợp Dynamics 365 on-premises, SharePoint / API nội bộ trong domain.",
            _ => "Bước \"Gọi API\" chọn kết nối này rồi chỉ cần nhập phần sau URL gốc, vd accounts?$select=name&$top=5. " +
                 "Mật khẩu / token / secret và header mặc định được mã hóa bằng Windows DPAPI."
        };
    }

    private void FillDynamicsTemplate()
    {
        if (_txtName.Text.Trim().Length == 0) _txtName.Text = "Dynamics365";
        if (_txtBase.Text.Trim().Length == 0) _txtBase.Text = "https://<ten-to-chuc>.crm5.dynamics.com/api/data/v9.2/";
        _cboAuth.SelectedIndex = Array.IndexOf(AuthTypes, ApiAuthType.EntraId);
        _txtHeaders.Text = string.Join(Environment.NewLine, "Accept: application/json", "OData-MaxVersion: 4.0", "OData-Version: 4.0",
            "Prefer: odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"");
    }

    private ApiConnection Read() => new()
    {
        Name = _txtName.Text.Trim(),
        BaseUrl = _txtBase.Text.Trim(),
        Auth = CurrentAuth,
        User = _txtUser.Visible ? _txtUser.Text.Trim() : "",
        Secret = !_txtSecret.Visible ? "" : _txtSecret.Text == Unchanged ? _original.Secret : _txtSecret.Text.Length == 0 ? "" : Protector.Protect(_txtSecret.Text),
        TenantId = _txtTenant.Visible ? _txtTenant.Text.Trim() : "",
        Scope = _txtScope.Visible ? _txtScope.Text.Trim() : "",
        TokenUrl = _txtTokenUrl.Visible ? _txtTokenUrl.Text.Trim() : "",
        // Header thường chứa khóa / token → lưu cả chuỗi đã mã hóa.
        Headers = Protector.Protect(_txtHeaders.Text.Replace("\r\n", "\n").Trim())
    };

    private string? Validate(ApiConnection c)
    {
        if (c.Name.Length == 0) return "Hãy nhập tên kết nối.";
        if (_otherNames.Contains(c.Name, StringComparer.CurrentCultureIgnoreCase)) return $"Đã có kết nối tên \"{c.Name}\".";
        if (c.BaseUrl.Length > 0 && !Uri.TryCreate(c.BaseUrl, UriKind.Absolute, out _)) return "URL gốc không hợp lệ (cần dạng https://…).";
        try { _ = ApiClient.ParseHeaders(Protector.Unprotect(c.Headers)).ToList(); }
        catch (FormatException ex) { return ex.Message; }
        if (c.Auth == ApiAuthType.EntraId && (c.TenantId.Length == 0 || c.User.Length == 0)) return "Hãy nhập Tenant ID và Client ID.";
        if (c.Auth == ApiAuthType.OAuthClientCredentials && (c.TokenUrl.Length == 0 || c.User.Length == 0)) return "Hãy nhập URL lấy token và Client ID.";
        return null;
    }

    private async Task TestAsync(Button button)
    {
        var c = Read();
        if (Validate(c) is { } error) { MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        button.Enabled = false;
        UseWaitCursor = true;
        try
        {
            var result = await ApiClient.TestAsync(c, CancellationToken.None);
            MessageBox.Show(this, "GET " + c.BaseUrl + "\n\n" + result, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Kết nối thất bại:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UseWaitCursor = false;
            button.Enabled = true;
        }
    }

    private void Save()
    {
        var c = Read();
        if (Validate(c) is { } error) { MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        Connection = c;
        DialogResult = DialogResult.OK;
        Close();
    }
}
