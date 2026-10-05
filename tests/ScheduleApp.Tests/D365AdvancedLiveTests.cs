using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Testing;

namespace ScheduleApp.Tests;

/// <summary>
/// Subgrid, view (FetchXML), chọn form, trạng thái nút thanh lệnh, tạo nhanh, vai trò người dùng, đăng nhập Microsoft —
/// chạy thật trên Edge headless với <see cref="FakeD365Server"/>.
/// </summary>
public sealed class D365AdvancedLiveTests
{
    [LiveFact]
    public async Task SubgridViewFormCommandQuickCreateAndRoles()
    {
        using var server = new FakeD365Server();
        await using var browser = await LiveBrowser.StartAsync(9337, server.BaseUrl + "main.aspx?appid=test");
        var d = new D365Steps();
        const string acc = FakeD365Server.ExistingAccount;

        // ── Subgrid: dữ liệu tải chậm 0,5 giây, đếm / tìm dòng / đọc ô / mở dòng / tạo bản ghi liên quan ──
        await d.Do(D365Action.OpenForm, "account", acc);
        await d.Check(ConditionKind.D365SubgridCount, "Contacts", "2");
        await d.Check(ConditionKind.D365SubgridCount, "Người liên hệ", "2"); // theo nhãn
        await d.Check(ConditionKind.D365SubgridRow, "Contacts", "tran thi"); // không dấu
        await d.Check(ConditionKind.D365SubgridRow, "Contacts", "Lê Văn C", negate: true);
        var bad = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Check(ConditionKind.D365SubgridRow, "Contacts", "Lê Văn C", wait: 300));
        Assert.Contains("Nguyễn Văn A | Trần Thị B", bad.Message);
        await d.Do(D365Action.SubgridGetValue, "Contacts", "emailaddress1", "email", rowRef: "2");
        Assert.Equal("b@contoso.vn", d["email"]);
        await d.Do(D365Action.SubgridGetValue, "Contacts", "", "ten", rowRef: "nguyen");
        Assert.Equal("Nguyễn Văn A", d["ten"]);
        await d.Do(D365Action.SubgridGetValue, "Contacts", "parentcustomerid", "cty");
        Assert.Equal("Adventure Works", d["cty"]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Do(D365Action.SubgridGetValue, "Contacts", "khongco", "x"));
        Assert.Contains("Có: fullname, emailaddress1, parentcustomerid", ex.Message);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Do(D365Action.SubgridRefresh, "Orders"));
        Assert.Contains("Contacts (Người liên hệ)", ex.Message);
        await Assert.ThrowsAsync<TimeoutException>(() => d.Do(D365Action.SubgridOpenRow, "Contacts", rowRef: "9", timeout: 1000));
        await d.Do(D365Action.SubgridRefresh, "Contacts");
        Assert.Equal("1", await d.Js("window.__gridRefreshed"));

        await d.Do(D365Action.SubgridOpenRow, "Contacts", rowRef: "Trần");
        await d.Do(D365Action.GetRecordId, variable: "cid");
        Assert.Equal(FakeD365Server.ContactB, d["cid"]);
        await d.Check(ConditionKind.D365FieldValue, "fullname", "Trần Thị B");

        await d.Do(D365Action.OpenForm, "account", acc);
        await d.Do(D365Action.SubgridNew, "Contacts");
        await d.Check(ConditionKind.D365FieldValue, "parentcustomerid", "Adventure Works"); // createFromEntity → ánh xạ field cha
        await d.Do(D365Action.GetRecordId, variable: "newId");
        Assert.Equal("", d["newId"]);
        await d.Do(D365Action.OpenForm, "account");
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Do(D365Action.SubgridNew, "Contacts"));
        Assert.Contains("chưa lưu", ex.Message);

        // ── Chọn form chính theo tên / Id, kiểm tra form đang mở ──
        await d.Do(D365Action.OpenForm, "account", acc, form: "Bán hàng");
        Assert.Equal(FakeD365Server.SalesForm, await d.Js("window.__lastOpen.formId"));
        await d.Check(ConditionKind.D365CurrentForm, "", "Bán hàng");
        await d.Do(D365Action.OpenForm, "account", acc, form: "{F0000000-0000-0000-0000-000000000001}");
        await d.Check(ConditionKind.D365CurrentForm, "", "Thông tin");
        await d.Check(ConditionKind.D365CurrentForm, "", "Bán hàng", negate: true);
        await d.Do(D365Action.OpenForm, "account", acc, form: "thong tin"); // đang mở đúng form → không mở lại
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Do(D365Action.OpenForm, "account", acc, form: "Kế toán"));
        Assert.Contains("Có: Thông tin, Bán hàng", ex.Message);

        // ── Trạng thái nút trên thanh lệnh (kể cả nút trong menu "…") ──
        await d.Check(ConditionKind.D365Command, "Lưu", "enabled");
        await d.Check(ConditionKind.D365Command, "Assign", "disabled");
        await d.Check(ConditionKind.D365Command, "Assign", "enabled", negate: true);
        await d.Check(ConditionKind.D365Command, "Share", "visible");
        Assert.Equal("false", await d.Js("document.querySelector('[data-id=OverflowButton]').getAttribute('aria-expanded')")); // đã đóng lại menu
        await d.Check(ConditionKind.D365Command, "Mscrm.Form.account.Delete", "visible", negate: true);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Check(ConditionKind.D365Command, "Assign", "enabled", wait: 300));
        Assert.Contains("bị mờ", ex.Message);
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => d.Do(D365Action.Command, "Assign", timeout: 1000));
        Assert.Contains("bị mờ", timeout.Message);
        await d.Do(D365Action.Command, "Share");
        Assert.Equal("true", await d.Js("window.__shared === true"));

        // ── View: đọc bản ghi theo FetchXML của view, tìm theo cột chính, mở bản ghi ──
        await d.Do(D365Action.ViewQuery, "account", "", "n");
        Assert.Equal("3", d["n"]); // view mặc định: đang hoạt động
        Assert.Contains("Adventure Works", d["view.names"]);
        Assert.DoesNotContain("Litware", d["view.names"]);
        await d.Do(D365Action.ViewQuery, "account", "Tài khoản Hà Nội", "n");
        Assert.Equal("2", d["n"]);
        Assert.Equal(["a0000000-0000-0000-0000-000000000001", "a0000000-0000-0000-0000-000000000003"], d["view.ids"].Split('\n')); // view thiếu cột Id/tên → tự thêm
        await d.Do(D365Action.ViewQuery, "account", "e0000000-0000-0000-0000-000000000002", "n", rowRef: "contoso");
        Assert.Equal("1", d["n"]);
        await d.Do(D365Action.ViewQuery, "account", "Tài khoản đang hoạt động", "n", rowRef: "Litware");
        Assert.Equal("0", d["n"]); // bộ lọc của view vẫn giữ khi tìm thêm
        await d.Do(D365Action.ViewQuery, "account", "View của tôi", "n"); // view cá nhân (userquery), bộ lọc "or"
        Assert.Equal("2", d["n"]);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Do(D365Action.ViewQuery, "account", "Không có view này"));
        Assert.Contains("Không tìm thấy view", ex.Message);
        await d.Do(D365Action.ViewOpenRecord, "account", "Tài khoản đang hoạt động", rowRef: "adventure");
        await d.Do(D365Action.GetRecordId, variable: "opened");
        Assert.Equal(acc, d["opened"]);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Do(D365Action.ViewOpenRecord, "account", "", rowRef: "Không có"));
        Assert.Contains("không có bản ghi nào chứa", ex.Message);

        // ── Tạo nhanh: điền sẵn giá trị (cả lookup), bấm Lưu và đóng, lấy Id + ghi nhận để dọn ──
        await d.Do(D365Action.QuickCreate, "firstname=Lan\nlastname=Phạm\nparentcustomerid=account:{" + acc.ToUpperInvariant() + "}", "contact", "qcId");
        Assert.Matches("^b0000000-0000-0000-0000-[0-9a-f]{12}$", d["qcId"]);
        Assert.Equal(d["qcId"], d["d365.lastId"]);
        Assert.Contains($"contacts({d["qcId"]})", d[D365Client.CreatedVar]);
        Assert.Equal("Adventure Works", await d.Js("window.__qcParams.parentcustomeridname"));
        Assert.Equal("account", await d.Js("window.__qcParams.parentcustomeridtype"));
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Do(D365Action.QuickCreate, "firstname=Lan", "contact", timeout: 8000));
        Assert.Contains("Họ: bắt buộc nhập", ex.Message);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Do(D365Action.QuickCreate, "khong co dau bang", "contact"));
        Assert.Contains("field=giá trị", ex.Message);

        // ── Người dùng & vai trò ──
        await d.Do(D365Action.GetUser, variable: "u");
        Assert.Equal("Người kiểm thử", d["u"]);
        Assert.Equal("Salesperson\nNhân viên CSKH", d["d365.roles"]);
        await d.Check(ConditionKind.D365UserRole, "nhan vien cskh");
        await d.Check(ConditionKind.D365UserRole, "System Administrator", negate: true);
    }

    [LiveFact]
    public async Task MicrosoftLoginWithMfaAndExpiredSession()
    {
        using var server = new FakeD365Server();
        using var trust = new TrustedTestLogin(server.BaseUrl);   // trang đăng nhập giả lập ở http://localhost
        await using var browser = await LiveBrowser.StartAsync(9338, server.BaseUrl + "adfs/ls/?client=d365");
        var d = new D365Steps();

        // Phiên hết hạn: trang đăng nhập đang mở → bước D365 báo rõ ràng thay vì "form chưa tải".
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Do(D365Action.OpenForm, "account"));
        Assert.Contains("Phiên đăng nhập Dynamics 365 đã hết", ex.Message);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Do(D365Action.WaitForm));
        Assert.Contains("Phiên đăng nhập Dynamics 365 đã hết", ex.Message);

        // Sai mật khẩu → lỗi của trang đăng nhập
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Do(D365Action.Login, "test@contoso.vn", "sai", timeout: 15000));
        Assert.Contains("Your account or password is incorrect", ex.Message);

        // Đăng nhập không MFA → vào app, chạy được bước D365
        await LiveBrowser.GoAsync(server.BaseUrl + "adfs/ls/");
        d.Ctx.Vars["mk"] = FakeD365Server.Password;
        await d.Do(D365Action.Login, "test@contoso.vn", "{{mk}}", timeout: 20000);
        Assert.Equal(["user:test@contoso.vn", "pass", "kmsi"], server.LoginEvents.TakeLast(3));
        await d.Do(D365Action.OpenForm, "account");
        int before = server.LoginEvents.Count;
        await d.Do(D365Action.Login, "test@contoso.vn", "{{mk}}"); // đã đăng nhập → không làm gì
        Assert.Equal(before, server.LoginEvents.Count);

        // MFA: thiếu khóa TOTP → báo cần khóa; có khóa → nhập mã đúng
        await LiveBrowser.GoAsync(server.BaseUrl + "adfs/ls/");
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => d.Do(D365Action.Login, "mfa@contoso.vn", "{{mk}}", timeout: 20000));
        Assert.Contains("khóa TOTP", ex.Message);
        await LiveBrowser.GoAsync(server.BaseUrl + "adfs/ls/");
        await d.Do(D365Action.Login, "mfa@contoso.vn", "{{mk}}", rowRef: FakeD365Server.MfaSecret, timeout: 30000);
        Assert.Contains("otp:ok", server.LoginEvents);
        Assert.DoesNotContain("otp:bad", server.LoginEvents);
        await d.Do(D365Action.GetUser, variable: "u");
        Assert.Equal("Người kiểm thử", d["u"]);

        // Màn hình chọn tài khoản
        await LiveBrowser.GoAsync(server.BaseUrl + "adfs/ls/?tiles");
        await d.Do(D365Action.Login, "test@contoso.vn", "{{mk}}", timeout: 20000);
        await d.Do(D365Action.OpenForm, "account");

        await Assert.ThrowsAsync<FormatException>(() => d.Do(D365Action.Login, "test@contoso.vn", "x", rowRef: "1234!"));
    }
    /// <summary>Kịch bản mẫu C6 (vai trò, nút thanh lệnh) chạy trọn qua công việc dùng chung C1 trên Edge ẩn, URL lấy từ biến môi trường.</summary>
    [LiveFact]
    public async Task SecuritySampleRunsHeadlessThroughSharedOpener()
    {
        using var server = new FakeD365Server();
        SettingsStore.Current.BrowserPort = 9340;
        var resource = typeof(Job).Assembly.GetManifestResourceNames().Single(n => n.EndsWith("mau-kiem-thu-d365.json", StringComparison.Ordinal));
        using var stream = typeof(Job).Assembly.GetManifestResourceStream(resource)!;
        var jobs = JobStore.ImportJson(new StreamReader(stream).ReadToEnd());
        var c6 = jobs.Single(j => j.Name.StartsWith("C6", StringComparison.Ordinal));
        var runner = new FlowRunner(new FakeUi(), id => jobs.FirstOrDefault(j => j.Id == id));
        BrowserClient.ForceHeadless = true;
        try
        {
            var suite = await TestSuite.RunAsync(runner, [c6], "C6", TestSupport.NewDir(), new SuiteOptions
            {
                Environment = "Giả lập",
                Variables = new() { ["d365Url"] = server.BaseUrl + "main.aspx?appid=test" }
            });
            var result = Assert.Single(suite.Cases);
            Assert.True(result.Ok, result.FailureSummary);
            Assert.Equal(3, result.AssertsPassed);
            Assert.Contains(result.Steps, s => s.Depth == 1 && s.Description.Contains("chế độ điều khiển")); // bước của C1
        }
        finally
        {
            BrowserClient.ForceHeadless = false;
            foreach (var d in BrowserProfiles.AllDirs().Where(BrowserProfiles.InUse).ToList())
            {
                try { await BrowserClient.CloseBrowserAsync(d, CancellationToken.None); }
                catch (TimeoutException) { }
            }
            try { BrowserClient.LastLaunched?.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* đã thoát */ }
        }
    }
}
