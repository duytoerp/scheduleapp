using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.Tests;

/// <summary>
/// Chạy các bước "Dynamics 365" / "Kiểm tra" trên Edge headless với một máy chủ giả lập model-driven app:
/// trang main.aspx có Xrm (form account, lookup contact, business rule, BPF, thanh lệnh, hộp thoại) và Web API (accounts).
/// </summary>
public sealed class D365LiveTests
{
    private const int BrowserPort = 9335;

    [LiveFact]
    public async Task DescribeFormAndRecordUserActions()
    {
        var edge = new[]
        {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe")
        }.FirstOrDefault(File.Exists);
        Assert.True(edge != null, "Máy không có Microsoft Edge.");

        using var server = new FakeD365Server();
        SettingsStore.Current.BrowserPort = BrowserPort + 1;
        var profile = Path.Combine(TestSupport.NewDir(), "edge-d365-rec");
        var proc = Process.Start(new ProcessStartInfo(edge!,
            $"--headless=new --remote-debugging-port={BrowserPort + 1} --user-data-dir=\"{profile}\" --no-first-run \"{server.BaseUrl}main.aspx?appid=test\"")
            { UseShellExecute = false });
        BrowserClient.Track(proc, BrowserPort + 1, profile);
        try
        {
            using var http = new HttpClient();
            for (int i = 0; i < 50; i++)
            {
                try { if ((await http.GetAsync($"http://127.0.0.1:{BrowserPort + 1}/json/version")).IsSuccessStatusCode) break; } catch (HttpRequestException) { }
                await Task.Delay(200);
            }
            await Task.Delay(1000);
            var ct = CancellationToken.None;
            Task<string> Js(string code) => BrowserClient.EvalAsync("main.aspx", code, ct);

            await Js("Xrm.Navigation.openForm({ entityName: 'account' }); 1");
            await Task.Delay(600);

            // Đọc form: nhãn, kiểu, lựa chọn, tab, nút thanh lệnh
            var form = await D365Client.DescribeFormAsync("", ct);
            Assert.Equal("account", form.Entity);
            Assert.True(form.IsNew);
            var name = form.Fields.Single(f => f.Name == "name");
            Assert.Equal("Tên tài khoản", name.Label);
            Assert.Equal("required", name.Required);
            Assert.Equal(["Bán lẻ", "Sản xuất"], form.Fields.Single(f => f.Name == "industrycode").Options);
            Assert.True(form.Fields.Single(f => f.Name == "accountnumber").Disabled);
            Assert.Equal(form.Fields[0], name); // field có nhãn xếp trước
            Assert.Equal(2, form.Tabs.Count);
            Assert.Contains("Lưu", form.Commands);

            // Ghi: người dùng nhập, bấm nút, chuyển tab, BPF, lưu, mở bản ghi khác
            await D365Client.StartRecordingAsync("", ct);
            await D365Client.StartRecordingAsync("", ct); // gọi lại không bị ghi đôi
            await Js("""
                const fc = Xrm.Page;
                const set = (n, v) => { const a = fc.getAttribute(n); a.setValue(v); a.fireOnChange(); };
                set('name', 'Con'); set('name', 'Contoso');
                set('industrycode', 2);
                set('telephone1', '0901');
                document.querySelector('[data-id=OverflowButton]').click();
                document.querySelector('[aria-label=Deactivate]').click();
                document.getElementById('dlgOk').click();
                fc.ui.tabs.get('DETAILS_TAB').setFocus();
                1
                """);
            await Js("new Promise(r => Xrm.Page.data.process.moveNext(r))");
            await Js("Xrm.Page.data.save().then(() => 1)");
            await Js("Xrm.Navigation.openForm({ entityName: 'account', entityId: '" + FakeD365Server.ExistingAccount + "' }); 1");
            await Task.Delay(1000);
            await Js("{ const a = Xrm.Page.getAttribute('telephone1'); a.setValue('028'); a.fireOnChange(); } 1");

            var events = await D365Client.DrainRecordingAsync("", ct);
            Assert.NotNull(events);
            Assert.Empty((await D365Client.DrainRecordingAsync("", ct))!); // đã lấy hết
            var steps = D365Client.ToSteps(events!);
            Assert.Equal(
            [
                "D365: mở form account (mới)",
                "D365: name = \"Contoso\"",
                "D365: industrycode = \"Sản xuất\"",
                "D365: telephone1 = \"0901\"",
                "D365: bấm \"Deactivate\"",
                "D365: hộp thoại → bấm \"Xác nhận\"",
                "D365: chuyển tab \"Chi tiết\"",
                "D365: BPF sang giai đoạn kế",
                "D365: lưu bản ghi",
                $"D365: mở form account [{FakeD365Server.ExistingAccount}]",
                "D365: telephone1 = \"028\""
            ], steps.Select(s => s.Describe()));

            await D365Client.StopRecordingAsync("", ct);
            await Js("{ const a = Xrm.Page.getAttribute('telephone1'); a.setValue('1'); a.fireOnChange(); } 1");
            Assert.Null(await D365Client.DrainRecordingAsync("", ct));

            var assert = D365Client.AssertFieldStep(name with { Value = "Contoso" });
            Assert.Equal("Kiểm tra: Tên tài khoản = Contoso", assert.Describe());
        }
        finally
        {
            try { proc?.Kill(true); } catch (InvalidOperationException) { }
        }
    }

    private static ActionStep Step(StepType t, Action<ActionStep> cfg)
    {
        var s = ActionStep.CreateDefault(t);
        s.DelayAfterMs = 0;
        cfg(s);
        return s;
    }

    [LiveFact]
    public async Task ModelDrivenAppSteps()
    {
        var edge = new[]
        {
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe")
        }.FirstOrDefault(File.Exists);
        Assert.True(edge != null, "Máy không có Microsoft Edge.");

        using var server = new FakeD365Server();
        SettingsStore.Current.BrowserPort = BrowserPort;
        var profile = Path.Combine(TestSupport.NewDir(), "edge-d365");
        var proc = Process.Start(new ProcessStartInfo(edge!,
            $"--headless=new --remote-debugging-port={BrowserPort} --user-data-dir=\"{profile}\" --no-first-run \"{server.BaseUrl}main.aspx?appid=test\"")
            { UseShellExecute = false });
        BrowserClient.Track(proc, BrowserPort, profile);
        try
        {
            using var http = new HttpClient();
            for (int i = 0; i < 50; i++)
            {
                try { if ((await http.GetAsync($"http://127.0.0.1:{BrowserPort}/json/version")).IsSuccessStatusCode) break; } catch (HttpRequestException) { }
                await Task.Delay(200);
            }
            await Task.Delay(1000);

            var job = new Job { Name = "d365" };
            var ctx = new FlowContext(job, new FakeUi(), RunOptions.Default, _ => null, CancellationToken.None);
            ctx.Vars["ten"] = "Contoso Việt Nam";

            async Task Do(D365Action a, string text = "", string args = "", string variable = "", bool force = false, string method = "GET", int timeout = 5000)
            {
                var s = ActionStep.CreateDefault(StepType.Dynamics);
                s.D365Action = a; s.Text = text; s.Arguments = args; s.Variable = variable; s.Force = force; s.Method = method; s.DelayMs = timeout;
                await StepExecutor.ExecuteAsync(ctx.ExpandStep(s), job, ctx);
            }

            async Task Check(ConditionKind kind, string text, string args = "", CompareOp op = CompareOp.Equals, bool negate = false)
            {
                var s = ActionStep.CreateDefault(StepType.Assert);
                s.Condition = kind; s.Text = text; s.Arguments = args; s.CompareOp = op; s.Negate = negate; s.DelayMs = 2000;
                await StepExecutor.ExecuteAsync(ctx.ExpandStep(s), job, ctx);
            }

            // Mở form tạo mới, nhập các kiểu field
            await Do(D365Action.OpenForm, "account");
            await Do(D365Action.SetField, "name", "{{ten}}");
            await Check(ConditionKind.D365FieldState, "telephone1", "required", negate: true);
            await Do(D365Action.SetField, "industrycode", "san xuat"); // so nhãn không dấu → business rule bắt buộc telephone1
            await Check(ConditionKind.D365FieldState, "telephone1", "required");
            await Check(ConditionKind.D365FieldValue, "industrycode", "Sản xuất");
            await Check(ConditionKind.D365FieldValue, "industrycode:raw", "2");
            await Do(D365Action.SetField, "revenue", "1.234.567,5");
            await Do(D365Action.GetField, "revenue", variable: "dt");
            Assert.Equal("1234567.5", ctx.Vars["dt"]);
            await Do(D365Action.SetField, "creditonhold", "có");
            await Check(ConditionKind.D365FieldValue, "creditonhold", "true");
            await Do(D365Action.SetField, "foundedon", "15/03/2024");
            await Check(ConditionKind.D365FieldValue, "foundedon", "15/03/2024");
            await Do(D365Action.SetField, "primarycontactid", "Nguyễn Văn A");
            await Check(ConditionKind.D365FieldValue, "primarycontactid", "Nguyễn Văn A");
            await Check(ConditionKind.D365FieldValue, "primarycontactid:raw", FakeD365Server.ContactA);
            await Do(D365Action.SetField, "primarycontactid", "contact:{" + FakeD365Server.ContactB.ToUpperInvariant() + "}");
            await Check(ConditionKind.D365FieldValue, "primarycontactid", "Trần Thị B");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Do(D365Action.SetField, "primarycontactid", "Không có ai"));
            Assert.Contains("Không tìm thấy bản ghi", ex.Message);
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Do(D365Action.SetField, "accountnumber", "ACC-1"));
            Assert.Contains("bị khóa", ex.Message);
            await Do(D365Action.SetField, "accountnumber", "ACC-1", force: true);
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Do(D365Action.SetField, "khongco", "x"));
            Assert.Contains("không có field", ex.Message);

            // Lưu thiếu field bắt buộc → lỗi + thông báo trên form
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Do(D365Action.Save));
            Assert.Contains("Lưu không thành công", ex.Message);
            await Check(ConditionKind.D365Notification, "bắt buộc");
            await Do(D365Action.GetNotifications, variable: "tb");
            Assert.Contains("telephone1", ctx.Vars["tb"]);

            await Do(D365Action.SetField, "telephone1", "0901 234 567");
            await Check(ConditionKind.D365FieldState, "telephone1", "dirty");
            await Do(D365Action.Save, variable: "id");
            Assert.Matches("^[0-9a-f-]{36}$", ctx.Vars["id"]);
            Assert.Equal($"accounts({ctx.Vars["id"]})", ctx.Vars[D365Client.CreatedVar]);
            await Check(ConditionKind.D365FieldState, "telephone1", "dirty", negate: true);
            await Do(D365Action.GetRecordId, variable: "id2");
            Assert.Equal(ctx.Vars["id"], ctx.Vars["id2"]);

            // Tab, BPF, thanh lệnh (nút trong "Thêm lệnh"), hộp thoại
            await Do(D365Action.SelectTab, "Chi tiết");
            await Do(D365Action.RunScript, "window.__focusedTab", variable: "tab");
            Assert.Equal("DETAILS_TAB", ctx.Vars["tab"]);
            await Do(D365Action.BpfNext);
            await Do(D365Action.RunScript, "return formContext.data.process.getActiveStage().getName();", variable: "stage");
            Assert.Equal("Develop", ctx.Vars["stage"]);
            await Do(D365Action.BpfPrevious);
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Do(D365Action.BpfPrevious));
            Assert.Contains("giai đoạn đầu", ex.Message);
            await Do(D365Action.Command, "Deactivate");
            await Do(D365Action.ConfirmDialog, variable: "dlg");
            Assert.Contains("Xác nhận vô hiệu hóa", ctx.Vars["dlg"]);
            await Do(D365Action.RunScript, "window.__confirmed === true", variable: "ok");
            Assert.Equal("true", ctx.Vars["ok"]);
            await Assert.ThrowsAsync<TimeoutException>(() => Do(D365Action.ConfirmDialog, timeout: 800));

            // Lỗi plugin khi lưu: hộp thoại lỗi
            await Do(D365Action.SetField, "name", "LOI plugin");
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Do(D365Action.Save));
            Assert.Contains("Plugin: tên không hợp lệ", ex.Message);
            await Do(D365Action.ConfirmDialog, "OK");

            // Web API bằng phiên trình duyệt + đếm bản ghi + dọn dữ liệu
            await Do(D365Action.WebApi, "{\"name\":\"API test\"}", "accounts", variable: "apiId", method: "POST");
            Assert.Matches("^[0-9a-f-]{36}$", ctx.Vars["apiId"]);
            Assert.Equal(2, ctx.Vars[D365Client.CreatedVar].Split('\n').Length);
            await Check(ConditionKind.D365RecordCount, "accounts?$filter=name eq 'API test'", "1", CompareOp.GreaterOrEqual);
            await Do(D365Action.WebApi, "", "accounts?$select=name", variable: "list");
            Assert.Contains("API test", ctx.Vars["list"]);
            Assert.Equal("200", ctx.Vars["http.status"]);
            await Do(D365Action.Cleanup);
            Assert.Equal("", ctx.Vars[D365Client.CreatedVar]);
            Assert.Contains($"accounts({ctx.Vars["apiId"]})", server.Deleted);
            Assert.Contains($"accounts({ctx.Vars["id"]})", server.Deleted);
            ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Do(D365Action.WebApi, "", "khongco"));
            Assert.Contains("404", ex.Message);

            // Mở bản ghi có sẵn, mở danh sách
            await Do(D365Action.OpenForm, "account", FakeD365Server.ExistingAccount.ToUpperInvariant());
            await Do(D365Action.GetRecordId, variable: "cur");
            Assert.Equal(FakeD365Server.ExistingAccount, ctx.Vars["cur"]);
            await Do(D365Action.OpenView, "account");
            await Do(D365Action.RunScript, "return formContext ? 'form' : 'list';", variable: "page");
            Assert.Equal("list", ctx.Vars["page"]);

            // Phần tử trên trang (CSS)
            await Check(ConditionKind.BrowserElement, "[data-id=CommandBar]");

            // Chạy như bộ kiểm thử: kiểm tra sai → báo cáo có ảnh chụp trang (qua DevTools) và junit.xml; dữ liệu test được dọn.
            var testJob = new Job
            {
                Name = "Tạo account",
                Group = "CRM",
                IsTestCase = true,
                CleanupTestData = true,
                Steps =
                [
                    Step(StepType.Dynamics, s => { s.D365Action = D365Action.WebApi; s.Method = "POST"; s.Arguments = "accounts"; s.Text = "{\"name\":\"Suite\"}"; }),
                    Step(StepType.Dynamics, s => { s.D365Action = D365Action.OpenForm; s.Text = "account"; }),
                    Step(StepType.Dynamics, s => { s.D365Action = D365Action.SetField; s.Text = "name"; s.Arguments = "Thực tế"; }),
                    Step(StepType.Assert, s => { s.Condition = ConditionKind.D365FieldValue; s.Text = "name"; s.Arguments = "Mong đợi"; s.DelayMs = 500; s.Message = "Tên đúng"; }),
                    Step(StepType.Assert, s => { s.Condition = ConditionKind.D365FieldState; s.Text = "name"; s.Arguments = "required"; s.DelayMs = 0; })
                ]
            };
            var runner = new FlowRunner(new FakeUi(), _ => null);
            var suite = await Services.Testing.TestSuite.RunAsync(runner, [testJob], "CRM");
            Assert.False(suite.Ok);
            var result = Assert.Single(suite.Cases);
            Assert.Equal(1, result.AssertsPassed);
            Assert.Equal(1, result.AssertsFailed);
            var failed = result.Steps.Single(s => !s.Ok);
            Assert.Contains("\"Thực tế\"", failed.Detail);
            Assert.NotNull(failed.Screenshot);
            var png = await File.ReadAllBytesAsync(failed.Screenshot!);
            Assert.Equal(0x89, png[0]); // PNG
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(suite.ReportPath)!, "junit.xml")));
            Assert.Single(server.Deleted, d => d.StartsWith("accounts(") && !d.Contains(ctx.Vars["apiId"]) && !d.Contains(ctx.Vars["id"]));
        }
        finally
        {
            try { proc?.Kill(true); } catch (InvalidOperationException) { }
        }
    }
}

