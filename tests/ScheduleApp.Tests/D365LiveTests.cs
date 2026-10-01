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

/// <summary>Máy chủ giả lập Dynamics 365: trang main.aspx với Xrm giả và Web API "accounts".</summary>
internal sealed class FakeD365Server : IDisposable
{
    public const string ContactA = "c0000000-0000-0000-0000-000000000001";
    public const string ContactB = "c0000000-0000-0000-0000-000000000002";
    public const string ExistingAccount = "a0000000-0000-0000-0000-0000000000aa";

    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, string> _accounts = new();
    private readonly CancellationTokenSource _cts = new();

    public ConcurrentBag<string> Deleted { get; } = [];
    public string BaseUrl { get; }

    public FakeD365Server()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        BaseUrl = $"http://localhost:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext c;
            try { c = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            try { Handle(c); }
            catch (Exception ex) { Write(c, 500, ex.Message); }
        }
    }

    private void Handle(HttpListenerContext c)
    {
        var path = c.Request.Url!.AbsolutePath;
        var method = c.Request.HttpMethod;
        if (path == "/main.aspx") { Write(c, 200, Page, "text/html; charset=utf-8"); return; }
        if (!path.StartsWith("/api/data/v9.2/", StringComparison.Ordinal)) { Write(c, 404, "{}"); return; }
        var rest = Uri.UnescapeDataString(path["/api/data/v9.2/".Length..]);

        if (rest == "accounts" && method == "POST")
        {
            using var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8);
            var body = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
            var id = Guid.NewGuid().ToString();
            _accounts[id] = body.RootElement.GetProperty("name").GetString() ?? "";
            c.Response.Headers["OData-EntityId"] = $"{BaseUrl}api/data/v9.2/accounts({id})";
            Write(c, 204, "");
            return;
        }
        if (rest == "accounts" && method == "GET")
        {
            var filter = Uri.UnescapeDataString(c.Request.QueryString["$filter"] ?? "");
            var m = System.Text.RegularExpressions.Regex.Match(filter, "eq '(.*)'");
            var rows = _accounts.Where(a => !m.Success || a.Value == m.Groups[1].Value.Replace("''", "'"))
                .Select(a => $"{{\"accountid\":\"{a.Key}\",\"name\":{System.Text.Json.JsonSerializer.Serialize(a.Value)}}}");
            Write(c, 200, "{\"value\":[" + string.Join(",", rows) + "]}");
            return;
        }
        if (method == "DELETE" && System.Text.RegularExpressions.Regex.IsMatch(rest, @"^accounts\([0-9a-f-]{36}\)$"))
        {
            Deleted.Add(rest);
            Write(c, 204, "");
            return;
        }
        Write(c, 404, "{\"error\":{\"code\":\"0x80060888\",\"message\":\"Resource not found for the segment '" + rest + "'.\"}}");
    }

    private static void Write(HttpListenerContext c, int status, string body, string type = "application/json; charset=utf-8")
    {
        c.Response.StatusCode = status;
        c.Response.ContentType = type;
        var bytes = Encoding.UTF8.GetBytes(body);
        if (status != 204) c.Response.OutputStream.Write(bytes);
        c.Response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { }
    }

    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><title>Dynamics 365 giả lập</title></head><body>
        <div data-id="CommandBar" role="menubar">
          <button data-id="account|NoRelationship|Form|Mscrm.Form.account.Save" aria-label="Lưu">Lưu</button>
          <button data-id="OverflowButton" aria-label="More commands" aria-expanded="false"
                  onclick="this.setAttribute('aria-expanded','true');document.getElementById('menu').style.display='block'">…</button>
        </div>
        <div id="menu" role="menu" style="display:none">
          <button role="menuitem" aria-label="Deactivate" data-id="account|NoRelationship|Form|Mscrm.Form.account.Deactivate"
                  onclick="document.getElementById('menu').style.display='none';showDialog('Xác nhận vô hiệu hóa bản ghi?', 'Xác nhận')">Deactivate</button>
        </div>
        <div data-id="notificationWrapper" id="notes"></div>
        <div role="dialog" id="dlg" style="display:none"><span id="dlgText"></span>
          <button data-id="confirmButton" id="dlgOk" onclick="window.__confirmed=true;hideDialog()">OK</button>
          <button data-id="cancelButton" onclick="hideDialog()">Hủy</button>
        </div>
        <script>
        function showNote(t) { const d = document.createElement('div'); d.setAttribute('data-id', 'warningNotification'); d.innerText = t; document.getElementById('notes').appendChild(d); }
        function showDialog(t, ok) { document.getElementById('dlgText').innerText = t; document.getElementById('dlgOk').innerText = ok || 'OK'; document.getElementById('dlg').style.display = 'block'; }
        function hideDialog() { document.getElementById('dlg').style.display = 'none'; }
        const contacts = {
          'c0000000-0000-0000-0000-000000000001': { contactid: 'c0000000-0000-0000-0000-000000000001', fullname: 'Nguyễn Văn A' },
          'c0000000-0000-0000-0000-000000000002': { contactid: 'c0000000-0000-0000-0000-000000000002', fullname: 'Trần Thị B' }
        };
        function attr(name, type, o) {
          o = o || {};
          const a = { _v: null, _dirty: false, _req: o.required || 'none',
            getName: () => name, getAttributeType: () => type, getValue: () => a._v,
            setValue: v => { a._v = v; a._dirty = true; }, fireOnChange: () => (o.onchange || []).forEach(f => f()),
            getRequiredLevel: () => a._req, getIsDirty: () => a._dirty, getFormat: () => o.format || null };
          if (o.options) {
            a.getOptions = () => o.options;
            a.getText = () => (o.options.find(x => x.value === a._v) || {}).text || null;
          }
          const ctrl = { getDisabled: () => !!o.disabled, getVisible: () => o.visible !== false, getEntityTypes: () => o.targets || [] };
          a.controls = { get: () => [ctrl] };
          return a;
        }
        function makeForm(entity, id) {
          const attrs = {};
          attrs.name = attr('name', 'string', { required: 'required' });
          attrs.telephone1 = attr('telephone1', 'string');
          attrs.industrycode = attr('industrycode', 'optionset', { options: [{ text: 'Bán lẻ', value: 1 }, { text: 'Sản xuất', value: 2 }],
            onchange: [() => { attrs.telephone1._req = attrs.industrycode._v === 2 ? 'required' : 'none'; }] });
          attrs.revenue = attr('revenue', 'money');
          attrs.creditonhold = attr('creditonhold', 'boolean');
          attrs.foundedon = attr('foundedon', 'datetime', { format: 'date' });
          attrs.primarycontactid = attr('primarycontactid', 'lookup', { targets: ['contact'] });
          attrs.accountnumber = attr('accountnumber', 'string', { disabled: true });
          let curId = id || '';
          const tabs = [['SUMMARY_TAB', 'Tóm tắt'], ['DETAILS_TAB', 'Chi tiết']]
            .map(t => ({ getName: () => t[0], getLabel: () => t[1], setFocus: () => { window.__focusedTab = t[0]; } }));
          const stages = ['Qualify', 'Develop', 'Propose'];
          let stage = 0;
          const fc = {
            getAttribute: n => attrs[n] || null,
            data: {
              entity: { getEntityName: () => entity, getId: () => curId ? '{' + curId.toUpperCase() + '}' : '' },
              save: () => new Promise((res, rej) => setTimeout(() => {
                const missing = Object.values(attrs).filter(a => a._req === 'required' && (a._v === null || a._v === ''));
                if (missing.length) { showNote('Thiếu field bắt buộc: ' + missing.map(a => a.getName()).join(', ')); return rej({ errorCode: 1, message: 'Required fields must be filled in.' }); }
                if (String(attrs.name._v).includes('LOI')) { showDialog('Lỗi plugin: tên không hợp lệ'); return rej({ errorCode: 2, message: 'Plugin: tên không hợp lệ' }); }
                if (!curId) { curId = 'a0000000-0000-0000-0000-' + String(Date.now()).slice(-12); fc.ui._type = 2; }
                Object.values(attrs).forEach(a => a._dirty = false);
                document.getElementById('notes').innerHTML = '';
                res();
              }, 100)),
              process: {
                getActiveProcess: () => ({}),
                getActiveStage: () => ({ getName: () => stages[stage] }),
                moveNext: cb => setTimeout(() => { if (stage >= stages.length - 1) return cb('end'); stage++; cb('success'); }, 50),
                movePrevious: cb => setTimeout(() => { if (stage === 0) return cb('beginning'); stage--; cb('success'); }, 50)
              }
            },
            ui: { _type: id ? 2 : 1, getFormType: () => fc.ui._type, tabs: { get: n => n === undefined ? tabs : (tabs.find(t => t.getName() === n) || null) } }
          };
          return fc;
        }
        const meta = { account: { EntitySetName: 'accounts', PrimaryIdAttribute: 'accountid', PrimaryNameAttribute: 'name' },
                       contact: { EntitySetName: 'contacts', PrimaryIdAttribute: 'contactid', PrimaryNameAttribute: 'fullname' } };
        window.Xrm = {
          Page: null,
          Navigation: {
            openForm: o => { setTimeout(() => { Xrm.Page = makeForm(o.entityName, (o.entityId || '').toLowerCase()); history.replaceState(null, '', '/main.aspx?pagetype=entityrecord&etn=' + o.entityName); }, 300); return Promise.resolve({}); },
            navigateTo: p => { setTimeout(() => { Xrm.Page = null; history.replaceState(null, '', '/main.aspx?pagetype=entitylist&etn=' + p.entityName); }, 200); return Promise.resolve(); }
          },
          Utility: { getGlobalContext: () => ({ getClientUrl: () => location.origin }), getEntityMetadata: e => Promise.resolve(meta[e]) },
          WebApi: {
            retrieveRecord: (e, id) => contacts[id.toLowerCase()] ? Promise.resolve(contacts[id.toLowerCase()]) : Promise.reject({ message: 'Không tồn tại' }),
            retrieveMultipleRecords: (e, q) => {
              const m = decodeURIComponent(q).match(/eq '(.*)'/);
              const name = m ? m[1].replace(/''/g, "'") : '';
              return Promise.resolve({ entities: Object.values(contacts).filter(c => c.fullname === name) });
            }
          }
        };
        </script></body></html>
        """;
}
