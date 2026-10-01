using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ScheduleApp.Automation;

namespace ScheduleApp.Tests;

/// <summary>
/// Máy chủ giả lập Dynamics 365 cho các bài kiểm thử chạy trên Edge headless:
/// - main.aspx: Xrm giả (form account / contact, lookup, business rule, BPF, subgrid "Contacts" tải chậm, form selector,
///   view + FetchXML, tạo nhanh, vai trò người dùng), thanh lệnh có nút bị mờ và menu "…", hộp thoại.
/// - /adfs/ls/: trang đăng nhập dựng theo cấu trúc trang đăng nhập Microsoft (loginfmt, passwd, otc, idSIButton9, KMSI).
/// - Web API "accounts" (POST / GET / DELETE).
/// Giả lập chỉ kiểm chứng logic của ScheduleApp; độ khớp với Dynamics 365 thật cần chạy D365RealTests trên môi trường thật.
/// </summary>
internal sealed class FakeD365Server : IDisposable
{
    public const string ContactA = "c0000000-0000-0000-0000-000000000001";
    public const string ContactB = "c0000000-0000-0000-0000-000000000002";
    public const string ExistingAccount = "a0000000-0000-0000-0000-0000000000aa";
    public const string SalesForm = "f0000000-0000-0000-0000-000000000002";

    /// <summary>Khóa TOTP (Base32) của tài khoản giả lập mfa@contoso.vn.</summary>
    public const string MfaSecret = "JBSWY3DPEHPK3PXP";
    public const string Password = "Pa$$w0rd";

    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, string> _accounts = new();
    private readonly CancellationTokenSource _cts = new();

    public ConcurrentBag<string> Deleted { get; } = [];

    /// <summary>Các bước trang đăng nhập đã nhận (user:…, pass, otp:ok/bad, kmsi).</summary>
    public ConcurrentQueue<string> LoginEvents { get; } = new();

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
        if (path.StartsWith("/adfs/ls", StringComparison.Ordinal)) { Write(c, 200, LoginPage, "text/html; charset=utf-8"); return; }
        if (path == "/login-log")
        {
            LoginEvents.Enqueue(c.Request.QueryString["e"] ?? "");
            Write(c, 200, "ok", "text/plain");
            return;
        }
        if (path == "/otp")
        {
            // Chấp nhận mã của bước hiện tại hoặc bước liền trước (như máy chủ thật cho phép lệch giờ).
            var code = c.Request.QueryString["code"] ?? "";
            var now = DateTimeOffset.UtcNow;
            bool ok = code == Totp.Code(MfaSecret, now) || code == Totp.Code(MfaSecret, now.AddSeconds(-30));
            LoginEvents.Enqueue("otp:" + (ok ? "ok" : "bad"));
            Write(c, 200, ok ? "ok" : "bad", "text/plain");
            return;
        }
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
        if (method == "DELETE" && System.Text.RegularExpressions.Regex.IsMatch(rest, @"^[a-z]+\([0-9a-f-]{36}\)$"))
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
          <button data-id="account|NoRelationship|Form|Mscrm.Form.account.Assign" aria-label="Assign" aria-disabled="true">Assign</button>
          <button data-id="OverflowButton" aria-label="More commands" aria-expanded="false" onclick="toggleMenu(this)">…</button>
        </div>
        <div id="menu" role="menu" style="display:none">
          <button role="menuitem" aria-label="Deactivate" data-id="account|NoRelationship|Form|Mscrm.Form.account.Deactivate"
                  onclick="closeMenu();showDialog('Xác nhận vô hiệu hóa bản ghi?', 'Xác nhận')">Deactivate</button>
          <button role="menuitem" aria-label="Share" data-id="account|NoRelationship|Form|Mscrm.Form.account.Share"
                  onclick="closeMenu();window.__shared=true">Share</button>
        </div>
        <div data-id="notificationWrapper" id="notes"></div>
        <div role="dialog" id="dlg" style="display:none"><span id="dlgText"></span>
          <button data-id="confirmButton" id="dlgOk" onclick="window.__confirmed=true;hideDialog()">OK</button>
          <button data-id="cancelButton" onclick="hideDialog()">Hủy</button>
        </div>
        <div role="dialog" id="qc" aria-label="Tạo nhanh" style="display:none"><b id="qcTitle"></b><div id="qcErr"></div>
          <button id="quickCreateSaveAndCloseBtn" data-id="quickCreateSaveAndCloseBtn" onclick="qcSave()">Lưu và đóng</button>
        </div>
        <script>
        function showNote(t) { const d = document.createElement('div'); d.setAttribute('data-id', 'warningNotification'); d.innerText = t; document.getElementById('notes').appendChild(d); }
        function showDialog(t, ok) { document.getElementById('dlgText').innerText = t; document.getElementById('dlgOk').innerText = ok || 'OK'; document.getElementById('dlg').style.display = 'block'; }
        function hideDialog() { document.getElementById('dlg').style.display = 'none'; }
        function toggleMenu(b) { const open = b.getAttribute('aria-expanded') !== 'true'; b.setAttribute('aria-expanded', String(open)); document.getElementById('menu').style.display = open ? 'block' : 'none'; }
        function closeMenu() { document.querySelector('[data-id=OverflowButton]').setAttribute('aria-expanded', 'false'); document.getElementById('menu').style.display = 'none'; }
        const contacts = {
          'c0000000-0000-0000-0000-000000000001': { contactid: 'c0000000-0000-0000-0000-000000000001', fullname: 'Nguyễn Văn A', email: 'a@contoso.vn' },
          'c0000000-0000-0000-0000-000000000002': { contactid: 'c0000000-0000-0000-0000-000000000002', fullname: 'Trần Thị B', email: 'b@contoso.vn' }
        };
        const accounts = [
          { accountid: 'a0000000-0000-0000-0000-000000000001', name: 'Contoso Hà Nội', statecode: 0, address1_city: 'Hà Nội' },
          { accountid: 'a0000000-0000-0000-0000-000000000002', name: 'Fabrikam Sài Gòn', statecode: 0, address1_city: 'Hồ Chí Minh' },
          { accountid: 'a0000000-0000-0000-0000-000000000003', name: 'Litware Hà Nội (ngừng)', statecode: 1, address1_city: 'Hà Nội' },
          { accountid: 'a0000000-0000-0000-0000-0000000000aa', name: 'Adventure Works', statecode: 0, address1_city: 'Đà Nẵng' }
        ];
        const views = [
          { table: 'savedquery', id: 'e0000000-0000-0000-0000-000000000001', name: 'Tài khoản đang hoạt động', isdefault: true,
            fetchxml: "<fetch><entity name='account'><attribute name='name'/><attribute name='accountid'/><order attribute='name'/><filter type='and'><condition attribute='statecode' operator='eq' value='0'/></filter></entity></fetch>" },
          { table: 'savedquery', id: 'e0000000-0000-0000-0000-000000000002', name: 'Tài khoản Hà Nội', isdefault: false,
            fetchxml: "<fetch><entity name='account'><attribute name='address1_city'/><filter><condition attribute='address1_city' operator='eq' value='Hà Nội'/></filter></entity></fetch>" },
          { table: 'userquery', id: 'e0000000-0000-0000-0000-000000000003', name: 'View của tôi', isdefault: false,
            fetchxml: "<fetch><entity name='account'><all-attributes/><filter type='or'><condition attribute='name' operator='like' value='%Works%'/><condition attribute='name' operator='like' value='%Fabrikam%'/></filter></entity></fetch>" }
        ];
        const formsOf = e => e === 'account'
          ? [['f0000000-0000-0000-0000-000000000001', 'Thông tin'], ['f0000000-0000-0000-0000-000000000002', 'Bán hàng']]
          : [['f0000000-0000-0000-0000-000000000003', 'Liên hệ']];
        window.__contactsOf = { 'a0000000-0000-0000-0000-0000000000aa': ['c0000000-0000-0000-0000-000000000001', 'c0000000-0000-0000-0000-000000000002'] };
        function attr(name, type, o) {
          o = o || {};
          const a = { _v: null, _dirty: false, _req: o.required || 'none',
            getName: () => name, getAttributeType: () => type, getValue: () => a._v,
            setValue: v => { a._v = v; a._dirty = true; }, _handlers: [], addOnChange: h => a._handlers.push(h), fireOnChange: () => { (o.onchange || []).forEach(f => f()); a._handlers.forEach(h => h({})); },
            getRequiredLevel: () => a._req, getIsDirty: () => a._dirty, getFormat: () => o.format || null };
          if (o.options) {
            a.getOptions = () => o.options;
            a.getText = () => (o.options.find(x => x.value === a._v) || {}).text || null;
          }
          const ctrl = { getName: () => name, getDisabled: () => !!o.disabled, getVisible: () => o.visible !== false, getEntityTypes: () => o.targets || [], getLabel: () => o.label || '' };
          a.controls = { get: () => [ctrl] };
          return a;
        }
        const gridAttr = (n, v) => ({ getName: () => n, getValue: () => v });
        function gridRow(c) {
          const e = { getEntityName: () => 'contact', getId: () => '{' + c.contactid.toUpperCase() + '}', getPrimaryAttributeValue: () => c.fullname,
            attributes: { get: () => [gridAttr('fullname', c.fullname), gridAttr('emailaddress1', c.email),
              gridAttr('parentcustomerid', [{ id: '{A0000000-0000-0000-0000-0000000000AA}', name: 'Adventure Works', entityType: 'account' }])] } };
          return { getData: () => ({ getEntity: () => e }) };
        }
        function makeForm(entity, id, o) {
          o = o || {};
          const attrs = {};
          if (entity === 'contact') {
            attrs.fullname = attr('fullname', 'string', { label: 'Họ tên' });
            attrs.lastname = attr('lastname', 'string', { required: 'required', label: 'Họ' });
            attrs.parentcustomerid = attr('parentcustomerid', 'lookup', { targets: ['account'], label: 'Công ty' });
            if (id && contacts[id]) attrs.fullname._v = contacts[id].fullname;
            if (o.createFromEntity) attrs.parentcustomerid._v = [{ id: o.createFromEntity.id, name: o.createFromEntity.name, entityType: o.createFromEntity.entityType }];
          } else {
            attrs.name = attr('name', 'string', { required: 'required', label: 'Tên tài khoản' });
            attrs.telephone1 = attr('telephone1', 'string');
            attrs.industrycode = attr('industrycode', 'optionset', { options: [{ text: 'Bán lẻ', value: 1 }, { text: 'Sản xuất', value: 2 }],
              onchange: [() => { attrs.telephone1._req = attrs.industrycode._v === 2 ? 'required' : 'none'; }] });
            attrs.revenue = attr('revenue', 'money');
            attrs.creditonhold = attr('creditonhold', 'boolean');
            attrs.foundedon = attr('foundedon', 'datetime', { format: 'date' });
            attrs.primarycontactid = attr('primarycontactid', 'lookup', { targets: ['contact'] });
            attrs.accountnumber = attr('accountnumber', 'string', { disabled: true });
            const known = accounts.find(a => a.accountid === id);
            if (known) attrs.name._v = known.name;
          }
          const grids = {};
          if (entity === 'account') {
            // Subgrid tải dữ liệu sau khi form mở (như thật): 0,5 giây đầu chưa có dòng nào.
            const rows = ((id && window.__contactsOf[id]) || []).map(cid => contacts[cid]);
            let loaded = false;
            setTimeout(() => { loaded = true; }, 500);
            grids.Contacts = { getName: () => 'Contacts', getLabel: () => 'Người liên hệ', getEntityName: () => 'contact', getControlType: () => 'subgrid',
              refresh() { window.__gridRefreshed = (window.__gridRefreshed || 0) + 1; },
              getGrid: () => ({ getRows: () => { const list = loaded ? rows.map(gridRow) : []; return { get: () => list, getLength: () => list.length }; },
                                getTotalRecordCount: () => loaded ? rows.length : -1 }) };
          }
          const forms = formsOf(entity);
          const current = forms.find(f => f[0] === String(o.formId || '').toLowerCase()) || forms[0];
          let curId = id || '';
          const tabs = [['SUMMARY_TAB', 'Tóm tắt'], ['DETAILS_TAB', 'Chi tiết']]
            .map(t => ({ getName: () => t[0], getLabel: () => t[1], _h: [], addTabStateChange(h) { this._h.push(h); }, getDisplayState: () => window.__focusedTab === t[0] ? 'expanded' : 'collapsed',
              setFocus() { window.__focusedTab = t[0]; this._h.forEach(h => h({})); } }));
          const saveHandlers = [], stageHandlers = [];
          const stages = ['Qualify', 'Develop', 'Propose'];
          let stage = 0;
          const primary = entity === 'contact' ? attrs.fullname : attrs.name;
          const fc = {
            getAttribute: n => n === undefined ? Object.values(attrs) : (attrs[n] || null),
            getControl: n => grids[n] || (attrs[n] ? attrs[n].controls.get()[0] : null),
            data: {
              entity: { addOnSave: h => saveHandlers.push(h), getEntityName: () => entity, getId: () => curId ? '{' + curId.toUpperCase() + '}' : '',
                        getPrimaryAttributeValue: () => primary._v },
              save: () => new Promise((res, rej) => setTimeout(() => {
                saveHandlers.forEach(h => h({}));
                const missing = Object.values(attrs).filter(a => a._req === 'required' && (a._v === null || a._v === ''));
                if (missing.length) { showNote('Thiếu field bắt buộc: ' + missing.map(a => a.getName()).join(', ')); return rej({ errorCode: 1, message: 'Required fields must be filled in.' }); }
                if (entity === 'account' && String(attrs.name._v).includes('LOI')) { showDialog('Lỗi plugin: tên không hợp lệ'); return rej({ errorCode: 2, message: 'Plugin: tên không hợp lệ' }); }
                if (!curId) { curId = 'a0000000-0000-0000-0000-' + String(Date.now()).slice(-12); fc.ui._type = 2; }
                Object.values(attrs).forEach(a => a._dirty = false);
                document.getElementById('notes').innerHTML = '';
                res();
              }, 100)),
              process: {
                getActiveProcess: () => ({}),
                getActiveStage: () => ({ getName: () => stages[stage] }),
                moveNext: cb => setTimeout(() => { if (stage >= stages.length - 1) return cb('end'); stage++; cb('success'); stageHandlers.forEach(h => h({ getEventArgs: () => ({ getDirection: () => 'Next' }) })); }, 50),
                movePrevious: cb => setTimeout(() => { if (stage === 0) return cb('beginning'); stage--; cb('success'); stageHandlers.forEach(h => h({ getEventArgs: () => ({ getDirection: () => 'Previous' }) })); }, 50),
                addOnStageChange: h => stageHandlers.push(h)
              }
            },
            ui: { _type: id ? 2 : 1, getFormType: () => fc.ui._type, tabs: { get: n => n === undefined ? tabs : (tabs.find(t => t.getName() === n) || null) },
                  controls: { get: () => [...Object.values(attrs).map(a => a.controls.get()[0]), ...Object.values(grids)] },
                  formSelector: { getCurrentItem: () => ({ getId: () => '{' + current[0].toUpperCase() + '}', getLabel: () => current[1] }) } }
          };
          return fc;
        }
        function evalFilter(f, r) {
          const parts = Array.from(f.children).map(c => c.tagName === 'filter' ? evalFilter(c, r) : evalCondition(c, r));
          return (f.getAttribute('type') || 'and') === 'or' ? parts.some(x => x) : parts.every(x => x);
        }
        function evalCondition(c, r) {
          const v = String(r[c.getAttribute('attribute')] ?? ''), want = c.getAttribute('value');
          if (c.getAttribute('operator') === 'eq') return v === want;
          if (c.getAttribute('operator') === 'like')
            return new RegExp('^' + want.split('%').map(s => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('.*') + '$', 'i').test(v);
          throw new Error('Toán tử chưa hỗ trợ: ' + c.getAttribute('operator'));
        }
        function runFetch(xml) {
          window.__lastFetch = xml;
          const doc = new DOMParser().parseFromString(xml, 'text/xml');
          const ent = doc.querySelector('fetch > entity');
          const kids = n => Array.from(ent.children).filter(c => c.tagName === n);
          const rows = accounts.filter(r => kids('filter').every(f => evalFilter(f, r)));
          const all = kids('all-attributes').length > 0, wanted = kids('attribute').map(a => a.getAttribute('name'));
          return rows.map(r => all ? Object.assign({}, r) : Object.fromEntries(wanted.map(a => [a, r[a]])));
        }
        let qc = null;
        function qcSave() {
          const p = qc.params;
          if (!p.lastname) { document.getElementById('qcErr').innerHTML = '<div data-id="lastname-error-message">Họ: bắt buộc nhập</div>'; return; }
          const id = 'b0000000-0000-0000-0000-' + String(Date.now()).slice(-12);
          document.getElementById('qc').style.display = 'none';
          qc.resolve({ savedEntityReference: [{ id: '{' + id.toUpperCase() + '}', name: ((p.firstname ? p.firstname + ' ' : '') + p.lastname), entityType: qc.entity }] });
        }
        const meta = { account: { EntitySetName: 'accounts', PrimaryIdAttribute: 'accountid', PrimaryNameAttribute: 'name' },
                       contact: { EntitySetName: 'contacts', PrimaryIdAttribute: 'contactid', PrimaryNameAttribute: 'fullname' } };
        window.Xrm = {
          Page: null,
          Navigation: {
            openForm: (o, params) => {
              if (o.useQuickCreateForm) return new Promise(resolve => {
                qc = { entity: o.entityName, params: params || {}, resolve };
                window.__qcParams = qc.params;
                document.getElementById('qcErr').innerHTML = '';
                document.getElementById('qcTitle').innerText = 'Tạo nhanh: ' + o.entityName;
                setTimeout(() => { document.getElementById('qc').style.display = 'block'; }, 300);
              });
              window.__lastOpen = o;
              setTimeout(() => { Xrm.Page = makeForm(o.entityName, (o.entityId || '').toLowerCase(), o); history.replaceState(null, '', '/main.aspx?pagetype=entityrecord&etn=' + o.entityName); }, 300);
              return Promise.resolve({});
            },
            navigateTo: p => { setTimeout(() => { Xrm.Page = null; history.replaceState(null, '', '/main.aspx?pagetype=entitylist&etn=' + p.entityName); }, 200); return Promise.resolve(); }
          },
          Utility: {
            getGlobalContext: () => ({ getClientUrl: () => location.origin,
              userSettings: { userName: 'Người kiểm thử', userId: '{D0000000-0000-0000-0000-000000000001}',
                              roles: { get: () => [{ id: 'r1', name: 'Salesperson' }, { id: 'r2', name: 'Nhân viên CSKH' }] } } }),
            getEntityMetadata: e => Promise.resolve(meta[e])
          },
          WebApi: {
            retrieveRecord: (e, id, q) => {
              id = String(id).replace(/[{}]/g, '').toLowerCase();
              if (e === 'savedquery' || e === 'userquery') {
                const v = views.find(x => x.table === e && x.id === id);
                return v ? Promise.resolve({ name: v.name, fetchxml: v.fetchxml }) : Promise.reject({ message: 'Không tồn tại' });
              }
              if (e === 'account') { const a = accounts.find(x => x.accountid === id); return a ? Promise.resolve(Object.assign({}, a)) : Promise.reject({ message: 'Không tồn tại' }); }
              return contacts[id] ? Promise.resolve(contacts[id]) : Promise.reject({ message: 'Không tồn tại' });
            },
            retrieveMultipleRecords: (e, q) => {
              const qs = decodeURIComponent(q);
              if (e === 'systemform') {
                const m = qs.match(/objecttypecode eq '([^']+)'/);
                return Promise.resolve({ entities: formsOf(m ? m[1] : '').map(f => ({ formid: f[0], name: f[1] })) });
              }
              if (e === 'savedquery' || e === 'userquery') {
                (window.__viewQueries = window.__viewQueries || []).push(e + qs);
                if (!/returnedtypecode eq 'account'/.test(qs)) return Promise.resolve({ entities: [] });
                const nm = qs.match(/name eq '((?:[^']|'')*)'/);
                const list = views.filter(v => v.table === e && (nm ? v.name === nm[1].replace(/''/g, "'") : (/isdefault eq true/.test(qs) ? v.isdefault : true)));
                return Promise.resolve({ entities: list.map(v => ({ name: v.name, fetchxml: v.fetchxml })) });
              }
              if (e === 'account' && qs.startsWith('?fetchXml=')) return Promise.resolve({ entities: runFetch(qs.slice('?fetchXml='.length)) });
              const m = qs.match(/eq '(.*)'/);
              const name = m ? m[1].replace(/''/g, "'") : '';
              return Promise.resolve({ entities: Object.values(contacts).filter(c => c.fullname === name) });
            }
          }
        };
        </script></body></html>
        """;

    /// <summary>
    /// Trang đăng nhập dựng theo cấu trúc trang login.microsoftonline.com (tên ô và Id nút giống thật, một nút "Next" dùng chung
    /// cho các bước). Tài khoản: test@contoso.vn (không MFA), mfa@contoso.vn (MFA mã TOTP). "?tiles" = màn hình chọn tài khoản.
    /// </summary>
    private const string LoginPage = """
        <!doctype html><html><head><meta charset="utf-8"><title>Sign in to your account</title></head><body>
        <div id="tiles" style="display:none"><div data-test-id="test@contoso.vn" onclick="pick(this)">test@contoso.vn</div><div id="otherTile" onclick="show('user')">Use another account</div></div>
        <div id="user"><input name="loginfmt" type="email"><div id="usernameError" style="display:none"></div></div>
        <div id="pass" style="display:none"><input name="passwd" type="password"><div id="passwordError" style="display:none"></div></div>
        <div id="otp" style="display:none"><input name="otc"><button id="idSubmit_SAOTCC_Continue" onclick="verify()">Verify</button><div id="idTD_Error" style="display:none"></div></div>
        <div id="kmsi" style="display:none">Stay signed in? <input type="checkbox" name="DontShowAgain"></div>
        <input type="submit" id="idSIButton9" value="Next" onclick="next()">
        <script>
        let step = 'user', who = '';
        function show(s) {
          ['tiles', 'user', 'pass', 'otp', 'kmsi'].forEach(x => document.getElementById(x).style.display = x === s ? 'block' : 'none');
          document.getElementById('idSIButton9').style.display = s === 'otp' || s === 'tiles' ? 'none' : 'inline';
          step = s;
        }
        if (location.search.includes('tiles')) show('tiles');
        const log = e => fetch('/login-log?e=' + encodeURIComponent(e));
        function fail(id, text) { const e = document.getElementById(id); e.innerText = text; e.style.display = 'block'; }
        function next() {
          if (step === 'user') {
            who = document.querySelector('[name=loginfmt]').value.trim().toLowerCase();
            log('user:' + who);
            if (who === 'test@contoso.vn' || who === 'mfa@contoso.vn') setTimeout(() => show('pass'), 200);
            else fail('usernameError', "We couldn't find an account with that username.");
          } else if (step === 'pass') {
            log('pass');
            if (document.querySelector('[name=passwd]').value !== 'Pa$$w0rd') return fail('passwordError', 'Your account or password is incorrect.');
            setTimeout(() => show(who === 'mfa@contoso.vn' ? 'otp' : 'kmsi'), 200);
          } else if (step === 'kmsi') {
            log('kmsi');
            location.href = '/main.aspx?appid=test';
          }
        }
        async function verify() {
          const r = await (await fetch('/otp?code=' + encodeURIComponent(document.querySelector('[name=otc]').value))).text();
          if (r === 'ok') show('kmsi'); else fail('idTD_Error', "You didn't enter the expected verification code.");
        }
        function pick(t) { document.querySelector('[name=loginfmt]').value = t.getAttribute('data-test-id'); show('user'); next(); }
        </script></body></html>
        """;
}
