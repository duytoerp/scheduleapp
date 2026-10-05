using System.Diagnostics;
using System.Text.Json;
using ScheduleApp.Models;
using ScheduleApp.Services;
using ScheduleApp.Services.Engine;

namespace ScheduleApp.Automation;

/// <summary>
/// Thao tác Dynamics 365 / Power Apps model-driven app qua Client API (Xrm) chạy trong tab trình duyệt điều khiển.
/// Ổn định hơn click theo CSS selector (DOM của Unified Interface đổi theo phiên bản), dùng luôn phiên đăng nhập
/// của trình duyệt (MFA, SSO) — kể cả Web API, nên không cần đăng ký ứng dụng Entra ID.
/// </summary>
internal static class D365Client
{
    private const int PollMs = 400;
    private const string WebApiVersion = "v9.2";

    /// <summary>Tên biến danh sách các bản ghi do flow tạo ra (mỗi dòng "entityset(id)") — dùng để dọn dữ liệu test.</summary>
    public const string CreatedVar = "d365.created";

    /// <summary>Tab Dynamics 365 mặc định khi bước không ghi tab: URL chứa main.aspx (Unified Interface) hoặc dynamics.com; cuối cùng là tab đang ở trang đăng nhập (phiên hết hạn — để báo lỗi rõ ràng).</summary>
    private static readonly string[] PreferredTabs = ["main.aspx", ".dynamics.com", ".crm", "login.microsoftonline.com", "/adfs/ls"];

    private static Task<string> TabAsync(string target, CancellationToken ct) => BrowserClient.PreferTabAsync(target, PreferredTabs, ct);

    public static async Task ExecuteAsync(ActionStep s, FlowContext ctx)
    {
        var ct = ctx.Ct;
        var tab = await TabAsync(s.Target, ct);
        int timeout = Math.Max(1000, s.DelayMs);

        switch (s.D365Action)
        {
            case D365Action.OpenForm:
            {
                var entity = Required(s.Text, "tên bảng (logical name, vd account, contact)").ToLowerInvariant();
                var id = NormalizeId(s.Arguments);
                var formId = await EvalAsync(tab, Script($$"""
                    const X = window.Xrm;
                    if (!X || !X.Navigation) throw new Error(__noXrm());
                    const cur = X.Page && X.Page.data && X.Page.data.entity;
                    window.__saPrev = cur || null;
                    const o = { entityName: {{Js(entity)}} };
                    const formId = await __formId({{Js(entity)}}, {{Js(s.Form)}});
                    if (formId) o.formId = formId;
                    if ({{Js(id)}}) {
                      if (cur && cur.getEntityName() === o.entityName && __id(cur.getId()) === {{Js(id)}} && (!formId || __formNow(X.Page).id === formId)) {
                        window.__saPrev = null; return formId;
                      }
                      o.entityId = {{Js(id)}};
                    }
                    X.Navigation.openForm(o);
                    return formId;
                    """), ct);
                await WaitFormAsync(tab, entity, id, newForm: id.Length == 0, timeout, ct, formId: formId);
                break;
            }

            case D365Action.OpenView:
            {
                var entity = Required(s.Text, "tên bảng (logical name, vd account)").ToLowerInvariant();
                var viewId = NormalizeId(s.Arguments);
                await EvalAsync(tab, Script($$"""
                    const X = window.Xrm;
                    if (!X || !X.Navigation) throw new Error(__noXrm());
                    const p = { pageType: 'entitylist', entityName: {{Js(entity)}} };
                    if ({{Js(viewId)}}) { p.viewId = {{Js(viewId)}}; p.viewType = 'savedquery'; }
                    X.Navigation.navigateTo(p);
                    return 'ok';
                    """), ct);
                await PollAsync(tab, $"(() => /pagetype=entitylist/i.test(location.href) && new RegExp('[?&]etn=' + {Js(entity)} + '(&|$)', 'i').test(location.href) && document.readyState === 'complete' ? 'ok' : '')()",
                    timeout, $"Danh sách {entity} chưa mở", ct);
                break;
            }

            case D365Action.WaitForm:
                await WaitFormAsync(tab, NormalizeEntity(s.Text), NormalizeId(s.Arguments), newForm: false, timeout, ct);
                break;

            case D365Action.SetField:
            {
                var field = Required(s.Text, "tên field (logical name)");
                await WaitFormAsync(tab, "", "", false, timeout, ct);
                var shown = await EvalAsync(tab, Script($$"""
                    const a = __attr({{Js(field)}});
                    const ctrls = a.controls ? a.controls.get() : [];
                    if (!{{Js(s.Force)}} && ctrls.length) {
                      if (ctrls.every(c => c.getDisabled && c.getDisabled()))
                        throw new Error('Field "' + a.getName() + '" đang bị khóa (chỉ đọc) — người dùng không nhập được. Tick "Cho phép nhập cả khi field bị khóa / ẩn" nếu cố ý.');
                      if (ctrls.every(c => c.getVisible && !c.getVisible()))
                        throw new Error('Field "' + a.getName() + '" đang bị ẩn trên form. Tick "Cho phép nhập cả khi field bị khóa / ẩn" nếu cố ý.');
                    }
                    a.setValue(await __parse(a, {{Js(s.Arguments)}}));
                    a.fireOnChange();
                    return __display(a, false);
                    """), ct, timeout);
                Log.Info($"      {field} = \"{Short(shown)}\"");
                break;
            }

            case D365Action.GetField:
            {
                var value = await ReadFieldAsync(tab, Required(s.Text, "tên field (logical name)"), ct);
                Log.Info($"      {s.Text.Trim()} = \"{Short(value)}\"");
                SetVar(ctx, s.Variable, value);
                break;
            }

            case D365Action.Save:
            {
                await WaitFormAsync(tab, "", "", false, timeout, ct);
                var json = await EvalAsync(tab, Script("""
                    const fc = __need();
                    const wasNew = fc.ui.getFormType() === 1;
                    const entity = fc.data.entity.getEntityName();
                    try { await fc.data.save(); }
                    catch (e) {
                      const msg = String((e && (e.message || e.description)) || '').trim();
                      const extra = __notifications().join(' | ');
                      throw new Error('Lưu không thành công: ' + (msg || extra || 'không rõ lý do') + (msg && extra && !extra.includes(msg) ? ' | ' + extra : ''));
                    }
                    const id = __id(fc.data.entity.getId());
                    if (!id) throw new Error('Lưu xong nhưng bản ghi chưa có Id. ' + __notifications().join(' | '));
                    let set = '';
                    if (wasNew) { try { set = (await Xrm.Utility.getEntityMetadata(entity, [])).EntitySetName || ''; } catch (e) { } }
                    return JSON.stringify({ id, entity, wasNew, set });
                    """), ct, timeout);
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                var id = r.GetProperty("id").GetString() ?? "";
                var entity = r.GetProperty("entity").GetString() ?? "";
                bool wasNew = r.GetProperty("wasNew").GetBoolean();
                var set = r.GetProperty("set").GetString() ?? "";
                ctx.Vars["d365.lastId"] = id;
                if (wasNew && set.Length > 0) TrackCreated(ctx, $"{set}({id})");
                Log.Info($"      Đã lưu {entity} {id}{(wasNew ? " (bản ghi mới)" : "")}.");
                SetVar(ctx, s.Variable, id);
                break;
            }

            case D365Action.Command:
            {
                var label = Required(s.Text, "nhãn nút hoặc command id");
                await WaitFormAsync(tab, "", "", false, timeout, ct, requireForm: false);
                var clicked = await PollAsync(tab, Script($$"""
                    const hit = __cmdFind({{Js(label)}});
                    if (hit && !__cmdDisabled(hit)) { hit.scrollIntoView({ block: 'center' }); hit.click(); return __cmdText(hit) || {{Js(label)}}; }
                    if (hit) return ''; // nút đang mờ — chờ enable rule tính xong
                    const more = __cmdMore();
                    if (more && more.getAttribute('aria-expanded') !== 'true') more.click();
                    return '';
                    """), timeout, $"Không thấy nút \"{label}\" trên thanh lệnh (hoặc nút đang bị mờ, không bấm được)", ct);
                Log.Info($"      Đã bấm \"{clicked}\".");
                break;
            }

            case D365Action.SelectTab:
            {
                var name = Required(s.Text, "tên hoặc nhãn tab");
                await WaitFormAsync(tab, "", "", false, timeout, ct);
                await EvalAsync(tab, Script($$"""
                    const fc = __need();
                    const tabs = fc.ui.tabs.get();
                    const t = fc.ui.tabs.get({{Js(name)}}) || tabs.find(t => __norm(t.getLabel()) === __norm({{Js(name)}}));
                    if (!t) throw new Error('Form không có tab "' + {{Js(name)}} + '". Có: ' + tabs.map(t => t.getLabel() + ' (' + t.getName() + ')').join(', '));
                    t.setFocus();
                    return t.getLabel();
                    """), ct, timeout);
                break;
            }

            case D365Action.BpfNext or D365Action.BpfPrevious:
            {
                await WaitFormAsync(tab, "", "", false, timeout, ct);
                var moved = await EvalAsync(tab, Script($$"""
                    const p = __need().data.process;
                    if (!p || !p.getActiveProcess()) throw new Error('Bản ghi không có quy trình nghiệp vụ (BPF) đang chạy.');
                    const before = p.getActiveStage() ? p.getActiveStage().getName() : '';
                    const status = await new Promise(res => {{(s.D365Action == D365Action.BpfNext ? "p.moveNext(res)" : "p.movePrevious(res)")}});
                    if (status !== 'success') {
                      const why = { end: 'đã ở giai đoạn cuối', invalid: 'không chuyển được (thiếu thông tin bắt buộc của giai đoạn?)',
                                    dirtyForm: 'form có thay đổi chưa lưu — thêm bước Lưu trước', crossEntity: 'giai đoạn kế thuộc bảng khác',
                                    stageGate: 'thiếu thông tin bắt buộc của giai đoạn hiện tại', beginning: 'đang ở giai đoạn đầu' }[status] || status;
                      throw new Error('BPF không chuyển giai đoạn: ' + why + '. ' + __notifications().join(' | '));
                    }
                    return before + ' → ' + (p.getActiveStage() ? p.getActiveStage().getName() : '');
                    """), ct, timeout);
                Log.Info($"      Giai đoạn: {moved}");
                break;
            }

            case D365Action.ConfirmDialog:
            {
                var label = s.Text.Trim();
                var text = await PollAsync(tab, Script($$"""
                    const want = __norm({{Js(label)}});
                    const dlg = Array.from(document.querySelectorAll('[role=dialog],[role=alertdialog]')).filter(__visible).pop();
                    if (!dlg) return '';
                    const btns = Array.from(dlg.querySelectorAll('button')).filter(__visible);
                    const name = b => (b.innerText || b.getAttribute('aria-label') || '').trim();
                    let b;
                    if (want) b = btns.find(x => __norm(name(x)) === want) || btns.find(x => __norm(name(x)).includes(want))
                               || btns.find(x => (x.getAttribute('data-id') || '').toLowerCase().includes(want));
                    else b = btns.find(x => /confirmButton|okButton|ok_id|primary/i.test((x.getAttribute('data-id') || '') + ' ' + x.className))
                          || btns.find(x => !/cancel|close|hủy|đóng/i.test(name(x) + ' ' + (x.getAttribute('data-id') || '')));
                    if (!b) throw new Error('Hộp thoại không có nút "' + {{Js(label)}} + '". Có: ' + btns.map(name).filter(Boolean).join(', '));
                    const text = (dlg.innerText || '').replace(/\s+/g, ' ').trim();
                    b.click();
                    return text || '(trống)';
                    """), timeout, "Không thấy hộp thoại nào", ct);
                Log.Info($"      Hộp thoại: \"{Short(text)}\"");
                SetVar(ctx, s.Variable, text);
                break;
            }

            case D365Action.GetRecordId:
            {
                await WaitFormAsync(tab, "", "", false, timeout, ct);
                var id = await EvalAsync(tab, Script("return __id(__need().data.entity.getId());"), ct);
                Log.Info($"      Id = {(id.Length == 0 ? "(chưa lưu)" : id)}");
                SetVar(ctx, s.Variable, id);
                break;
            }

            case D365Action.GetNotifications:
            {
                var text = await NotificationsAsync(tab, ct);
                Log.Info(text.Length == 0 ? "      Không có thông báo." : $"      Thông báo: \"{Short(text.Replace('\n', '|'))}\"");
                SetVar(ctx, s.Variable, text);
                break;
            }

            case D365Action.WebApi:
                await WebApiAsync(s, tab, timeout, ctx);
                break;

            case D365Action.Cleanup:
                await CleanupAsync(ctx, tab, ct);
                break;

            case D365Action.RunScript:
            {
                var code = Required(s.Text, "JavaScript");
                if (!code.Contains("return", StringComparison.Ordinal)) code = "return (" + code.Trim().TrimEnd(';') + ");";
                var value = await EvalAsync(tab, Script("const formContext = __fc(); const Xrm = window.Xrm;\n" + code), ct, timeout);
                if (value.Length > 0) Log.Info($"      Kết quả: {Short(value)}");
                SetVar(ctx, s.Variable, value);
                break;
            }

            case D365Action.SubgridOpenRow:
            {
                var grid = Required(s.Text, "tên subgrid");
                await WaitFormAsync(tab, "", "", false, timeout, ct);
                var json = await PollAsync(tab, Script($$"""
                    const r = __gridRow(__gridRows(__grid({{Js(grid)}})), {{Js(s.RowRef)}});
                    if (!r) return '';
                    window.__saPrev = Xrm.Page.data.entity;
                    Xrm.Navigation.openForm({ entityName: r.entity, entityId: r.id });
                    return JSON.stringify(r);
                    """), timeout, $"Subgrid \"{grid}\" không có dòng \"{RowLabel(s.RowRef)}\"", ct);
                var row = GridRow.Parse(json);
                Log.Info($"      Mở dòng {row.Index}: {row.Entity} \"{Short(row.Name)}\"");
                await WaitFormAsync(tab, row.Entity, row.Id, false, timeout, ct);
                break;
            }

            case D365Action.SubgridGetValue:
            {
                var grid = Required(s.Text, "tên subgrid");
                await WaitFormAsync(tab, "", "", false, timeout, ct);
                var json = await PollAsync(tab, Script($$"""
                    const r = __gridRow(__gridRows(__grid({{Js(grid)}})), {{Js(s.RowRef)}});
                    if (!r) return '';
                    const col = {{Js(s.Arguments.Trim())}};
                    if (!col) return JSON.stringify({ v: r.name });
                    if (!(col in r.cells)) throw new Error('Subgrid "' + {{Js(grid)}} + '" không có cột "' + col + '". Có: ' + Object.keys(r.cells).join(', '));
                    return JSON.stringify({ v: r.cells[col] });
                    """), timeout, $"Subgrid \"{grid}\" không có dòng \"{RowLabel(s.RowRef)}\"", ct);
                using var doc = JsonDocument.Parse(json);
                var value = doc.RootElement.GetProperty("v").GetString() ?? "";
                Log.Info($"      = \"{Short(value)}\"");
                SetVar(ctx, s.Variable, value);
                break;
            }

            case D365Action.SubgridNew:
            {
                var grid = Required(s.Text, "tên subgrid");
                await WaitFormAsync(tab, "", "", false, timeout, ct);
                var related = await EvalAsync(tab, Script($$"""
                    const fc = __need(), c = __grid({{Js(grid)}});
                    const id = __id(fc.data.entity.getId());
                    if (!id) throw new Error('Bản ghi chưa lưu — thêm bước Lưu trước khi tạo bản ghi liên quan từ subgrid.');
                    const rel = c.getEntityName();
                    window.__saPrev = fc.data.entity;
                    Xrm.Navigation.openForm({ entityName: rel, createFromEntity: {
                      entityType: fc.data.entity.getEntityName(), id, name: String(fc.data.entity.getPrimaryAttributeValue() ?? '') } });
                    return rel;
                    """), ct);
                await WaitFormAsync(tab, related, "", newForm: true, timeout, ct);
                break;
            }

            case D365Action.SubgridRefresh:
            {
                var grid = Required(s.Text, "tên subgrid");
                await WaitFormAsync(tab, "", "", false, timeout, ct);
                await EvalAsync(tab, Script($"__grid({Js(grid)}).refresh(); return 'ok';"), ct);
                break;
            }

            case D365Action.ViewQuery or D365Action.ViewOpenRecord:
            {
                var entity = Required(s.Text, "tên bảng (logical name, vd account)").ToLowerInvariant();
                var result = await ViewQueryAsync(tab, entity, s.Arguments, s.RowRef, timeout, ct);
                ctx.Vars["view.count"] = result.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                ctx.Vars["view.ids"] = string.Join("\n", result.Rows.Select(r => r.Id));
                ctx.Vars["view.names"] = string.Join("\n", result.Rows.Select(r => r.Name));
                Log.Info($"      View \"{result.View}\": {result.Rows.Count} bản ghi" +
                         (result.Rows.Count > 0 ? $" ({Short(string.Join(", ", result.Rows.Take(5).Select(r => r.Name)))}{(result.Rows.Count > 5 ? ", …" : "")})" : ""));
                if (s.D365Action == D365Action.ViewQuery)
                {
                    SetVar(ctx, s.Variable, ctx.Vars["view.count"]);
                    break;
                }
                if (result.Rows.Count == 0)
                    throw new InvalidOperationException($"View \"{result.View}\" của {entity} không có bản ghi nào" +
                                                        (string.IsNullOrWhiteSpace(s.RowRef) ? "." : $" chứa \"{s.RowRef.Trim()}\"."));
                var first = result.Rows[0];
                await EvalAsync(tab, Script($$"""
                    window.__saPrev = Xrm.Page && Xrm.Page.data ? Xrm.Page.data.entity : null;
                    Xrm.Navigation.openForm({ entityName: {{Js(entity)}}, entityId: {{Js(first.Id)}} });
                    return 'ok';
                    """), ct);
                await WaitFormAsync(tab, entity, first.Id, false, timeout, ct);
                break;
            }

            case D365Action.QuickCreate:
                await QuickCreateAsync(s, tab, timeout, ctx);
                break;

            case D365Action.Login:
                await LoginAsync(s, timeout, ctx);
                break;

            case D365Action.GetUser:
            {
                var user = await UserAsync(tab, ct);
                ctx.Vars["d365.user"] = user.Name;
                ctx.Vars["d365.userId"] = user.Id;
                ctx.Vars["d365.roles"] = string.Join("\n", user.Roles);
                Log.Info($"      Người dùng: {user.Name} · vai trò: {(user.Roles.Count == 0 ? "(không đọc được)" : string.Join(", ", user.Roles))}");
                SetVar(ctx, s.Variable, user.Name);
                break;
            }
        }
    }

    private static string RowLabel(string rowRef) => string.IsNullOrWhiteSpace(rowRef) ? "1" : rowRef.Trim();

    /// <summary>Một dòng của subgrid: thứ tự (từ 1), bảng, Id, giá trị cột chính và các ô.</summary>
    internal sealed record GridRow(int Index, string Entity, string Id, string Name, IReadOnlyDictionary<string, string> Cells)
    {
        public static GridRow Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return From(doc.RootElement);
        }

        public static GridRow From(JsonElement r) => new(
            r.GetProperty("index").GetInt32(),
            r.GetProperty("entity").GetString() ?? "",
            r.GetProperty("id").GetString() ?? "",
            r.GetProperty("name").GetString() ?? "",
            r.GetProperty("cells").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase));
    }

    // ───────────────────────────── Subgrid, view, form, nút, người dùng (cho điều kiện / kiểm tra) ─────────────────────────────

    /// <summary>Các dòng đang tải của subgrid và tổng số bản ghi (theo view của subgrid, không giới hạn theo trang).</summary>
    public static async Task<(int Total, List<GridRow> Rows)> SubgridAsync(string target, string grid, CancellationToken ct)
    {
        var tab = await TabAsync(target, ct);
        var json = await EvalAsync(tab, Script($$"""
            const c = __grid({{Js(Required(grid, "tên subgrid"))}});
            const g = c.getGrid();
            const rows = __gridRows(c);
            let total = g.getTotalRecordCount ? g.getTotalRecordCount() : -1;
            if (typeof total !== 'number' || total < rows.length) total = rows.length;
            return JSON.stringify({ total, rows });
            """), ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (root.GetProperty("total").GetInt32(), root.GetProperty("rows").EnumerateArray().Select(GridRow.From).ToList());
    }

    /// <summary>Dòng subgrid chứa <paramref name="text"/> (cột chính hoặc bất kỳ ô nào, không phân biệt hoa thường / dấu).</summary>
    internal static bool RowContains(GridRow row, string text)
    {
        var want = Fold(text);
        return want.Length == 0 || Fold(row.Name).Contains(want) || row.Cells.Values.Any(v => Fold(v).Contains(want));
    }

    private static string Fold(string s) => Vision.ScreenOcr.RemoveDiacritics(s ?? "").Replace('đ', 'd').Replace('Đ', 'D').Trim().ToLowerInvariant();

    /// <summary>Tên form chính đang mở (theo form selector), hoặc trống nếu không đọc được.</summary>
    public static async Task<string> CurrentFormAsync(string target, CancellationToken ct)
    {
        var tab = await TabAsync(target, ct);
        return await EvalAsync(tab, Script("return __formNow(__need()).label;"), ct);
    }

    /// <summary>Người dùng đang đăng nhập.</summary>
    public sealed record UserInfo(string Name, string Id, IReadOnlyList<string> Roles);

    public static async Task<UserInfo> UserAsync(string target, CancellationToken ct)
    {
        var tab = await TabAsync(target, ct);
        var json = await EvalAsync(tab, Script("""
            const X = window.Xrm;
            if (!X || !X.Utility) throw new Error(__noXrm());
            const us = X.Utility.getGlobalContext().userSettings;
            let roles = [];
            const r = us.roles;
            if (r) roles = (typeof r.get === 'function' ? r.get() : Array.from(r)).map(x => String((x && (x.name || x.text)) || x));
            return JSON.stringify({ name: us.userName || '', id: __id(us.userId), roles });
            """), ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new UserInfo(root.GetProperty("name").GetString() ?? "", root.GetProperty("id").GetString() ?? "",
            root.GetProperty("roles").EnumerateArray().Select(x => x.GetString() ?? "").ToList());
    }

    /// <summary>Người dùng có vai trò <paramref name="role"/> (so tên không phân biệt hoa thường / dấu).</summary>
    internal static bool HasRole(UserInfo user, string role) => user.Roles.Any(r => Fold(r) == Fold(role));

    /// <summary>
    /// Trạng thái nút trên thanh lệnh: { visible, enabled, disabled }. Không thấy trên thanh lệnh thì mở menu "Thêm lệnh (…)" để tìm,
    /// xong thì đóng lại.
    /// </summary>
    public static async Task<Dictionary<string, string>> CommandStateAsync(string target, string label, CancellationToken ct)
    {
        Required(label, "nhãn nút hoặc command id");
        var tab = await TabAsync(target, ct);
        bool opened = false;
        var sw = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var r = await EvalAsync(tab, Script($$"""
                    if (!document.querySelector('[data-id*="CommandBar" i], [data-lp-id*="commandbar" i], [role=menubar]')) throw new Error('Thanh lệnh chưa tải xong.');
                    const b = __cmdFind({{Js(label)}});
                    if (b) return JSON.stringify({ found: true, disabled: __cmdDisabled(b) });
                    const m = __cmdMore();
                    if (m && m.getAttribute('aria-expanded') !== 'true') { m.click(); return 'opened'; }
                    return m ? 'open' : 'none';
                    """), ct);
                if (r == "opened")
                {
                    opened = true;
                    sw.Restart();
                }
                // Menu "…" vừa mở: các mục hiện ra sau một chút — chờ tối đa 2 giây rồi mới kết luận là không có nút.
                if (r is "opened" or "open" && sw.ElapsedMilliseconds < 2000)
                {
                    await Task.Delay(PollMs, ct);
                    continue;
                }
                bool found = r.StartsWith('{'), disabled = found && r.Contains("\"disabled\":true", StringComparison.Ordinal);
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["visible"] = found ? "true" : "false",
                    ["enabled"] = found && !disabled ? "true" : "false",
                    ["disabled"] = disabled ? "true" : "false"
                };
            }
        }
        finally
        {
            if (opened)
            {
                try { await EvalAsync(tab, Script("const m = __cmdMore(); if (m && m.getAttribute('aria-expanded') === 'true') m.click(); return 'ok';"), ct); }
                catch (InvalidOperationException) { }
            }
        }
    }

    /// <summary>Đúng nếu nút ở trạng thái <paramref name="state"/> (visible, enabled, disabled).</summary>
    public static bool HasCommandState(IReadOnlyDictionary<string, string> st, string state) =>
        ActionStep.D365CommandStates.ContainsKey(state.Trim())
            ? st.GetValueOrDefault(state.Trim().ToLowerInvariant()) == "true"
            : throw new InvalidOperationException($"Trạng thái nút \"{state}\" không hợp lệ — dùng: {string.Join(", ", ActionStep.D365CommandStates.Keys)}.");

    // ───────────────────────────── Danh sách (view) qua Web API ─────────────────────────────

    /// <summary>Kết quả đọc view: tên view và các bản ghi (Id, giá trị cột chính) theo đúng bộ lọc / sắp xếp của view.</summary>
    public sealed record ViewResult(string View, IReadOnlyList<(string Id, string Name)> Rows);

    /// <summary>
    /// Đọc bản ghi của một view (savedquery hoặc view cá nhân userquery) bằng FetchXML của chính view đó — kiểm tra được view
    /// lọc đúng dữ liệu. <paramref name="view"/>: tên hoặc Id (trống = view mặc định của bảng); <paramref name="search"/>: lọc thêm
    /// theo cột chính (chứa chữ), như ô tìm nhanh.
    /// </summary>
    private static async Task<ViewResult> ViewQueryAsync(string tab, string entity, string view, string search, int timeoutMs, CancellationToken ct)
    {
        var json = await EvalAsync(tab, Script($$"""
            const X = window.Xrm;
            if (!X || !X.WebApi) throw new Error(__noXrm());
            const E = {{Js(entity)}}, want = {{Js(view.Trim())}}, search = {{Js(search.Trim())}};
            const md = await X.Utility.getEntityMetadata(E, []);
            const lit = s => encodeURIComponent("'" + s.replace(/'/g, "''") + "'");
            const sel = '?$select=name,fetchxml';
            const find = async (table, filter) => (await X.WebApi.retrieveMultipleRecords(table, sel + '&$filter=' + filter + '&$top=1')).entities[0] || null;
            let v = null;
            if (/^\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?$/i.test(want)) {
              const id = __id(want);
              try { v = await X.WebApi.retrieveRecord('savedquery', id, sel); } catch (e) { }
              if (!v) { try { v = await X.WebApi.retrieveRecord('userquery', id, sel); } catch (e) { } }
            } else if (want) {
              v = await find('savedquery', "returnedtypecode eq '" + E + "' and querytype eq 0 and name eq " + lit(want))
                || await find('userquery', "returnedtypecode eq '" + E + "' and name eq " + lit(want));
            } else {
              v = await find('savedquery', "returnedtypecode eq '" + E + "' and querytype eq 0 and isdefault eq true");
            }
            if (!v) throw new Error('Không tìm thấy view ' + (want ? '"' + want + '"' : 'mặc định') + ' của bảng ' + E + '.');
            const doc = new DOMParser().parseFromString(v.fetchxml, 'text/xml');
            const ent = doc.querySelector('fetch > entity');
            if (!ent) throw new Error('FetchXML của view "' + v.name + '" không đọc được.');
            const kids = n => Array.from(ent.children).filter(c => c.tagName === n);
            const ensure = n => { if (!kids('all-attributes').length && !kids('attribute').some(a => a.getAttribute('name') === n)) { const a = doc.createElement('attribute'); a.setAttribute('name', n); ent.appendChild(a); } };
            ensure(md.PrimaryIdAttribute);
            ensure(md.PrimaryNameAttribute);
            if (search) {
              // Gói bộ lọc của view và điều kiện tìm vào một filter "and" (giữ nguyên ý nghĩa bộ lọc gốc).
              const and = doc.createElement('filter');
              and.setAttribute('type', 'and');
              kids('filter').forEach(f => and.appendChild(f));
              const c = doc.createElement('condition');
              c.setAttribute('attribute', md.PrimaryNameAttribute);
              c.setAttribute('operator', 'like');
              c.setAttribute('value', '%' + search + '%');
              and.appendChild(c);
              ent.appendChild(and);
            }
            const xml = new XMLSerializer().serializeToString(doc);
            const r = await X.WebApi.retrieveMultipleRecords(E, '?fetchXml=' + encodeURIComponent(xml));
            return JSON.stringify({ view: v.name, rows: r.entities.map(e => ({ id: __id(e[md.PrimaryIdAttribute]), name: String(e[md.PrimaryNameAttribute] ?? '') })) });
            """), ct, timeoutMs);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new ViewResult(root.GetProperty("view").GetString() ?? "",
            root.GetProperty("rows").EnumerateArray().Select(r => (r.GetProperty("id").GetString() ?? "", r.GetProperty("name").GetString() ?? "")).ToList());
    }

    // ───────────────────────────── Tạo nhanh (quick create) ─────────────────────────────

    /// <summary>
    /// Mở form tạo nhanh với giá trị điền sẵn (mỗi dòng field=giá trị; lookup = bảng:guid), bấm "Lưu và đóng" rồi lấy Id bản ghi
    /// từ kết quả của Xrm.Navigation.openForm.
    /// </summary>
    private static async Task QuickCreateAsync(ActionStep s, string tab, int timeout, FlowContext ctx)
    {
        var ct = ctx.Ct;
        var entity = Required(s.Arguments, "tên bảng (logical name) cần tạo nhanh").ToLowerInvariant();
        var lines = s.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var set = await EvalAsync(tab, Script($$"""
            const X = window.Xrm;
            if (!X || !X.Navigation) throw new Error(__noXrm());
            const E = {{Js(entity)}}, params = {};
            for (const line of {{JsonSerializer.Serialize(lines)}}) {
              const i = line.indexOf('=');
              if (i <= 0) throw new Error('Dòng "' + line + '" không đúng dạng field=giá trị.');
              const k = line.slice(0, i).trim(), v = line.slice(i + 1).trim();
              const m = v.match(/^([a-z0-9_]+)\s*:\s*\{?([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\}?$/i);
              if (m) {
                const t = m[1].toLowerCase(), md = await X.Utility.getEntityMetadata(t, []);
                let name = '';
                try { name = (await X.WebApi.retrieveRecord(t, m[2], '?$select=' + md.PrimaryNameAttribute))[md.PrimaryNameAttribute] || ''; }
                catch (e) { throw new Error('Không tìm thấy bản ghi ' + t + ' ' + m[2] + ': ' + (e.message || e)); }
                params[k] = m[2].toLowerCase(); params[k + 'name'] = name; params[k + 'type'] = t;
              } else params[k] = v;
            }
            window.__saQc = { done: false };
            window.__saQcAt = 0;
            X.Navigation.openForm({ entityName: E, useQuickCreateForm: true }, params).then(r => {
              const ref = r && r.savedEntityReference && r.savedEntityReference[0];
              window.__saQc = { done: true, id: ref ? __id(ref.id) : '', name: ref ? String(ref.name || '') : '' };
            }, e => { window.__saQc = { done: true, error: String((e && (e.message || e.description)) || e) }; });
            let set = '';
            try { set = (await X.Utility.getEntityMetadata(E, [])).EntitySetName || ''; } catch (e) { }
            return set;
            """), ct);

        var json = await PollAsync(tab, Script("""
            const q = window.__saQc || {};
            if (q.done) return JSON.stringify(q);
            const b = Array.from(document.querySelectorAll('button')).filter(__visible)
              .find(x => /quickCreateSaveAndCloseBtn/i.test((x.id || '') + ' ' + (x.getAttribute('data-id') || '')));
            if (b && !window.__saQcAt) { window.__saQcAt = Date.now(); b.click(); return ''; }
            if (window.__saQcAt && Date.now() - window.__saQcAt > 3000) {
              const notes = __notifications();
              if (notes.length) throw new Error('Tạo nhanh không lưu được: ' + notes.join(' | '));
            }
            return '';
            """), timeout, $"Form tạo nhanh {entity} chưa lưu xong (không thấy nút \"Lưu và đóng\" của form tạo nhanh?)", ct);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("error", out var err)) throw new InvalidOperationException("Tạo nhanh không thành công: " + err.GetString());
        var id = r.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (id.Length == 0) throw new InvalidOperationException("Form tạo nhanh đã đóng nhưng không có bản ghi nào được lưu.");
        ctx.Vars["d365.lastId"] = id;
        if (set.Length > 0) TrackCreated(ctx, $"{set}({id})");
        Log.Info($"      Đã tạo nhanh {entity} \"{Short(r.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "")}\" ({id}).");
        SetVar(ctx, s.Variable, id);
    }

    // ───────────────────────────── Đăng nhập Microsoft (Entra ID) ─────────────────────────────

    /// <summary>Tab ưu tiên khi đăng nhập: trang đăng nhập Microsoft, rồi tới tab Dynamics 365.</summary>
    private static readonly string[] LoginTabs = ["login.microsoftonline.com", "login.live.com", "login.windows.net", "/adfs/ls", "main.aspx", ".dynamics.com", ".crm"];

    /// <summary>Trang đăng nhập Microsoft được nhận mật khẩu / mã TOTP (đúng tên máy, qua https).</summary>
    private static readonly string[] MicrosoftLoginHosts = ["login.microsoftonline.com", "login.microsoft.com"];

    /// <summary>
    /// Chỉ cho kiểm thử: origin (vd http://127.0.0.1:5000) của trang đăng nhập giả lập được coi là tin cậy. Mặc định rỗng —
    /// không nới quy tắc https + tên máy cho người dùng.
    /// </summary>
    internal static List<string> TestLoginOrigins { get; } = [];

    /// <summary>
    /// Trang <paramref name="url"/> được nhận mật khẩu / mã TOTP không: https và tên máy đúng bằng login.microsoftonline.com,
    /// login.microsoft.com hoặc một máy trong <paramref name="trustedHosts"/> (trang đăng nhập riêng của tổ chức — ADFS / SSO).
    /// So khớp nguyên tên máy, không theo chuỗi con (login.microsoftonline.com.evil.vn, evil.vn/login.microsoftonline.com bị từ chối).
    /// </summary>
    internal static bool IsTrustedLoginPage(string url, IEnumerable<string> trustedHosts)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        var origin = uri.GetLeftPart(UriPartial.Authority);
        lock (TestLoginOrigins)
            if (TestLoginOrigins.Any(o => o.TrimEnd('/').Equals(origin, StringComparison.OrdinalIgnoreCase))) return true;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0) return false;
        return MicrosoftLoginHosts.Concat(trustedHosts.Select(HostOf)).Any(h => h.Length > 0 && uri.IdnHost.Equals(h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Tên máy người dùng khai báo: "adfs.contoso.com" hoặc dán cả URL "https://adfs.contoso.com/adfs/ls".</summary>
    private static string HostOf(string entry)
    {
        var e = (entry ?? "").Trim().TrimEnd('/');
        if (e.Contains("://", StringComparison.Ordinal)) return Uri.TryCreate(e, UriKind.Absolute, out var u) ? u.IdnHost : "";
        return e.Contains('/') || e.Contains(':') ? "" : e;
    }

    /// <summary>Mật khẩu / khóa TOTP lưu trong bước: đã mã hóa (dpapi:) thì giải mã; chữ thường (bước cũ, {{secret:…}} đã thay) giữ nguyên.</summary>
    private static string StepSecret(string stored, string what) =>
        Protector.TryUnprotect(stored, out var plain) ? plain
            : throw new InvalidOperationException($"Không giải mã được {what} lưu trong bước Đăng nhập (công việc chép từ máy / tài khoản Windows khác) — " +
                                                  "nhập lại trong bước, hoặc lưu trong 🔑 Bí mật rồi dùng {{secret:Tên}}.");

    /// <summary>Trạng thái trang đăng nhập (xem <see cref="LoginAsync"/>).</summary>
    private const string LoginProbe = """
        const vis = sel => { const e = document.querySelector(sel); return e && __visible(e) ? e : null; };
        if (!__onLogin() && window.Xrm && Xrm.Utility && Xrm.Utility.getGlobalContext) return 'app';
        if (document.readyState !== 'complete') return 'loading';
        const err = ['#usernameError', '#passwordError', '#idTD_Error', '#errorText', '.alert-error'].map(vis).find(e => e && (e.innerText || '').trim());
        if (err) return 'error:' + err.innerText.replace(/\s+/g, ' ').trim();
        if (vis('input[name=DontShowAgain]') || vis('#KmsiCheckboxField')) return 'kmsi';
        if (vis('input[name=otc]')) return 'otp';
        if (vis('[data-value="PhoneAppOTP"]')) return 'chooseotp';
        if (vis('#idDiv_SAOTCAS_Title') || vis('#idRichContext_DisplaySign')) return 'push';
        if (vis('input[name=passwd]')) return 'password';
        if (vis('input[name=loginfmt]')) return 'user';
        if (vis('#otherTile') || Array.from(document.querySelectorAll('[data-test-id]')).some(__visible)) return 'pick';
        return 'other:' + location.href;
        """;

    /// <summary>
    /// Đăng nhập trang Microsoft (login.microsoftonline.com) bằng tài khoản test: email → mật khẩu → mã TOTP (nếu tài khoản có MFA
    /// bằng ứng dụng xác thực) → "Duy trì đăng nhập". Xong khi trang Dynamics 365 tải được Xrm. Đã đăng nhập sẵn thì không làm gì.
    /// </summary>
    private static async Task LoginAsync(ActionStep s, int timeout, FlowContext ctx)
    {
        var ct = ctx.Ct;
        var user = Required(s.Text, "tài khoản đăng nhập (email)");
        var password = StepSecret(s.Arguments, "mật khẩu");
        var totpSecret = StepSecret(s.RowRef, "khóa TOTP").Trim();
        Log.Mask(password); // nhập thẳng (không qua {{secret:…}}) cũng không hiện trong log / báo cáo
        Log.Mask(totpSecret);
        if (totpSecret.Length > 0) Totp.DecodeBase32(totpSecret); // báo lỗi khóa sai ngay từ đầu
        var sw = Stopwatch.StartNew();
        string last = "";
        int repeats = 0;
        while (true)
        {
            string state;
            try
            {
                var tab = await BrowserClient.PreferTabAsync(s.Target, LoginTabs, ct);
                state = await EvalAsync(tab, Script(LoginProbe), ct);
                if (state == "app")
                {
                    Log.Info(sw.ElapsedMilliseconds < 1500 ? "      Đã đăng nhập sẵn." : $"      Đăng nhập xong ({ActionStep.FormatMs((int)sw.ElapsedMilliseconds)}).");
                    return;
                }
                if (state.StartsWith("error:", StringComparison.Ordinal))
                    throw new InvalidOperationException("Trang đăng nhập báo lỗi: " + state[6..]);
                if (state == "push")
                    throw new InvalidOperationException("Tài khoản đang đòi duyệt đăng nhập trên điện thoại (Authenticator push) — với tài khoản test, " +
                                                        "hãy thêm phương thức \"ứng dụng xác thực\" và nhập khóa TOTP vào bước Đăng nhập.");

                // Cùng một bước lặp lại nhiều lần (vd mật khẩu bị từ chối mà trang không báo lỗi) → dừng thay vì gửi mãi.
                repeats = state == last ? repeats + 1 : 0;
                last = state;
                if (repeats >= 6 && state is "user" or "password" or "otp")
                    throw new InvalidOperationException($"Trang đăng nhập không chuyển sang bước tiếp sau khi nhập {LoginStepName(state)}.");

                string? action = state switch
                {
                    "user" => $"__fill('input[name=loginfmt]', {Js(user)}); __press('#idSIButton9', 'input[type=submit]');",
                    "password" => password.Length == 0
                        ? throw new InvalidOperationException("Trang đăng nhập hỏi mật khẩu — nhập mật khẩu (nên dùng {{secret:Tên}}).")
                        : $"__fill('input[name=passwd]', {Js(password)}); __press('#idSIButton9', 'input[type=submit]');",
                    "otp" => totpSecret.Length == 0
                        ? throw new InvalidOperationException("Trang đăng nhập hỏi mã xác thực (MFA) — nhập khóa TOTP của tài khoản test vào bước Đăng nhập.")
                        : $"__fill('input[name=otc]', {Js(await FreshCodeAsync(totpSecret, ct))}); __press('#idSubmit_SAOTCC_Continue', '#idSIButton9', 'input[type=submit]');",
                    "chooseotp" => "__press('[data-value=\"PhoneAppOTP\"]');",
                    "kmsi" => "__press('#idSIButton9', 'input[type=submit]');",
                    "pick" => $$"""
                        const t = Array.from(document.querySelectorAll('[data-test-id]')).find(e => (e.getAttribute('data-test-id') || '').toLowerCase() === {{Js(user.ToLowerInvariant())}});
                        if (t) t.click(); else __press('#otherTile');
                        """,
                    _ => null
                };
                if (action != null)
                {
                    if (state is "password" or "otp")
                    {
                        // Chỉ điền mật khẩu / mã vào đúng trang đăng nhập tin cậy; trang đổi địa chỉ giữa chừng thì không điền.
                        var origin = TrustedLoginOrigin(await EvalAsync(tab, Script("return location.href;"), ct));
                        action = $"if (location.origin.toLowerCase() !== {Js(origin)}) return 'moved';\n" + action;
                    }
                    if (await EvalAsync(tab, Script(LoginActions + action + "\nreturn 'ok';"), ct) == "ok")
                        Log.Info($"      Đăng nhập: {LoginStepName(state)}");
                }
            }
            catch (InvalidOperationException ex) when (IsTransient(ex.Message))
            {
                state = "loading"; // trang đang chuyển
            }
            if (sw.ElapsedMilliseconds >= timeout)
                throw new TimeoutException($"Chưa đăng nhập xong sau {ActionStep.FormatMs(timeout)} (bước cuối: {LoginStepName(last)}" +
                                           (last.StartsWith("other:", StringComparison.Ordinal) ? $", trang đang mở: {last[6..]}" : "") + "). " +
                                           "Trang đăng nhập của tổ chức có thể khác trang Microsoft chuẩn (ADFS, SSO riêng) — đăng nhập tay một lần trong hồ sơ trình duyệt.");
            await Task.Delay(state is "loading" || state.StartsWith("other", StringComparison.Ordinal) ? 500 : 1500, ct);
        }
    }

    /// <summary>Origin (scheme://máy[:cổng]) của trang đăng nhập tin cậy; trang khác → lỗi, không điền gì.</summary>
    private static string TrustedLoginOrigin(string href)
    {
        if (!IsTrustedLoginPage(href, SettingsStore.Current.TrustedLoginHosts))
        {
            var shown = Uri.TryCreate(href, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : href;
            throw new InvalidOperationException($"Trang đang hỏi mật khẩu ({shown}) không phải trang đăng nhập Microsoft (https://login.microsoftonline.com) " +
                                                "hay trang đăng nhập riêng đã khai báo trong ⚙ Cài đặt → Chung — không điền mật khẩu / mã xác thực.");
        }
        return new Uri(href.Trim()).GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    private static string LoginStepName(string state) => state switch
    {
        "user" => "tài khoản",
        "password" => "mật khẩu",
        "otp" => "mã xác thực",
        "chooseotp" => "chọn cách xác thực bằng mã",
        "kmsi" => "duy trì đăng nhập",
        "pick" => "chọn tài khoản",
        "" => "chưa có",
        _ => "chờ trang tải"
    };

    /// <summary>Mã TOTP còn hạn ít nhất 5 giây (tránh gửi mã sắp hết hạn).</summary>
    private static async Task<string> FreshCodeAsync(string secret, CancellationToken ct)
    {
        int left = Totp.SecondsLeft(DateTimeOffset.UtcNow);
        if (left < 5) await Task.Delay(TimeSpan.FromSeconds(left + 1), ct);
        return Totp.Code(secret, DateTimeOffset.UtcNow);
    }

    private const string LoginActions = """
        const __fill = (sel, v) => {
          const e = document.querySelector(sel);
          Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(e, v);
          e.dispatchEvent(new Event('input', { bubbles: true }));
          e.dispatchEvent(new Event('change', { bubbles: true }));
        };
        const __press = (...sels) => {
          for (const s of sels) { const b = document.querySelector(s); if (b && __visible(b)) { b.click(); return true; } }
          return false;
        };
        """;

    // ───────────────────────────── Đọc trạng thái (dùng cho điều kiện / kiểm tra) ─────────────────────────────

    /// <summary>Giá trị hiển thị của field ("tên:raw" = giá trị gốc: Id lookup, số của option set, ngày ISO).</summary>
    public static async Task<string> ReadFieldAsync(string target, string field, CancellationToken ct)
    {
        var tab = await TabAsync(target, ct);
        field = field.Trim();
        bool raw = field.EndsWith(":raw", StringComparison.OrdinalIgnoreCase);
        if (raw) field = field[..^4].Trim();
        return await EvalAsync(tab, Script($"return __display(__attr({Js(field)}), {Js(raw)});"), ct);
    }

    /// <summary>Trạng thái field: { required: "none|required|recommended", disabled, visible, dirty, empty }.</summary>
    public static async Task<Dictionary<string, string>> FieldStateAsync(string target, string field, CancellationToken ct)
    {
        var tab = await TabAsync(target, ct);
        var json = await EvalAsync(tab, Script($$"""
            const a = __attr({{Js(field.Trim())}});
            const ctrls = a.controls ? a.controls.get() : [];
            const v = a.getValue();
            return JSON.stringify({
              required: a.getRequiredLevel ? a.getRequiredLevel() : 'none',
              disabled: ctrls.length > 0 && ctrls.every(c => c.getDisabled && c.getDisabled()),
              visible: ctrls.some(c => !c.getVisible || c.getVisible()),
              dirty: !!(a.getIsDirty && a.getIsDirty()),
              empty: v === null || v === undefined || v === '' || (Array.isArray(v) && v.length === 0)
            });
            """), ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name,
            p => p.Value.ValueKind switch { JsonValueKind.True => "true", JsonValueKind.False => "false", _ => p.Value.ToString() },
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Đúng nếu field ở trạng thái <paramref name="state"/> (required, recommended, disabled, visible, dirty, empty).</summary>
    public static bool HasState(IReadOnlyDictionary<string, string> st, string state) => state.Trim().ToLowerInvariant() switch
    {
        "required" => st.GetValueOrDefault("required") == "required",
        "recommended" => st.GetValueOrDefault("required") == "recommended",
        "disabled" or "readonly" => st.GetValueOrDefault("disabled") == "true",
        "visible" => st.GetValueOrDefault("visible") == "true",
        "dirty" => st.GetValueOrDefault("dirty") == "true",
        "empty" => st.GetValueOrDefault("empty") == "true",
        var x => throw new InvalidOperationException($"Trạng thái \"{x}\" không hợp lệ — dùng: {string.Join(", ", ActionStep.D365FieldStates.Keys)}.")
    };

    /// <summary>Thông báo đang hiện trên form (thanh thông báo, lỗi field, hộp thoại lỗi) — mỗi thông báo một dòng.</summary>
    public static async Task<string> NotificationsAsync(string target, CancellationToken ct)
    {
        var tab = await TabAsync(target, ct);
        return await EvalAsync(tab, Script("return __notifications().join('\\n');"), ct);
    }

    /// <summary>Số bản ghi trả về của truy vấn Web API (vd accounts?$filter=name eq 'ABC').</summary>
    public static async Task<int> CountAsync(string target, string query, CancellationToken ct)
    {
        var tab = await TabAsync(target, ct);
        var r = await FetchAsync(tab, "GET", Required(query, "truy vấn Web API"), "", 30_000, ct);
        if (r.Status is < 200 or >= 300) throw new InvalidOperationException($"Web API trả về {r.Status}: {Short(ApiError(r.Body))}");
        using var doc = JsonDocument.Parse(r.Body);
        if (doc.RootElement.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array) return v.GetArrayLength();
        return 1; // truy vấn một bản ghi theo Id
    }

    // ───────────────────────────── Đọc cấu trúc form (cho ô "Chọn từ form") ─────────────────────────────

    /// <summary>Một field trên form đang mở.</summary>
    public sealed record FieldInfo(string Name, string Label, string Type, string Required, bool Disabled, bool Visible, string Value,
        IReadOnlyList<string> Options);

    /// <summary>Form đang mở: bảng, Id, field, tab và các nút đang hiện trên thanh lệnh.</summary>
    public sealed record FormInfo(string Entity, string Id, bool IsNew, IReadOnlyList<FieldInfo> Fields,
        IReadOnlyList<(string Name, string Label)> Tabs, IReadOnlyList<string> Commands,
        IReadOnlyList<(string Name, string Label, string Entity)> Subgrids);

    /// <summary>Đọc form Dynamics 365 đang mở trong trình duyệt điều khiển.</summary>
    public static async Task<FormInfo> DescribeFormAsync(string target, CancellationToken ct)
    {
        var tab = await TabAsync(target, ct);
        var json = await EvalAsync(tab, Script("""
            const fc = __need();
            const fields = fc.getAttribute().map(a => {
              const ctrls = a.controls ? a.controls.get() : [];
              const labelled = ctrls.find(c => c.getLabel && c.getLabel());
              let options = [];
              if (a.getOptions) { try { options = (a.getOptions() || []).map(o => o.text); } catch (e) { } }
              let value = '';
              try { value = __display(a, false); } catch (e) { }
              return {
                name: a.getName(), label: labelled ? labelled.getLabel() : '', type: a.getAttributeType(),
                required: a.getRequiredLevel ? a.getRequiredLevel() : 'none',
                disabled: ctrls.length > 0 && ctrls.every(c => c.getDisabled && c.getDisabled()),
                visible: ctrls.some(c => !c.getVisible || c.getVisible()),
                value, options
              };
            });
            const tabs = fc.ui.tabs.get().map(t => ({ name: t.getName(), label: t.getLabel() }));
            const commands = [];
            document.querySelectorAll('[data-id*="CommandBar" i] button, [data-lp-id*="commandbar" i] button, [role=menubar] button').forEach(b => {
              if (!__visible(b) || /overflowbutton|morecommands/i.test(b.getAttribute('data-id') || '')) return;
              const t = (b.getAttribute('aria-label') || b.innerText || '').replace(/\s+/g, ' ').trim();
              if (t && !commands.includes(t)) commands.push(t);
            });
            const subgrids = (fc.ui.controls ? fc.ui.controls.get() : []).filter(c => c.getGrid)
              .map(c => ({ name: c.getName(), label: (c.getLabel && c.getLabel()) || '', entity: (c.getEntityName && c.getEntityName()) || '' }));
            return JSON.stringify({ entity: fc.data.entity.getEntityName(), id: __id(fc.data.entity.getId()), isNew: fc.ui.getFormType() === 1, fields, tabs, commands, subgrids });
            """), ct);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var fields = r.GetProperty("fields").EnumerateArray().Select(f => new FieldInfo(
            f.GetProperty("name").GetString() ?? "",
            f.GetProperty("label").GetString() ?? "",
            f.GetProperty("type").GetString() ?? "",
            f.GetProperty("required").GetString() ?? "none",
            f.GetProperty("disabled").GetBoolean(),
            f.GetProperty("visible").GetBoolean(),
            f.GetProperty("value").GetString() ?? "",
            f.GetProperty("options").EnumerateArray().Select(o => o.GetString() ?? "").ToList()))
            .OrderBy(f => f.Label.Length == 0).ThenBy(f => f.Label.Length > 0 ? f.Label : f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var tabs = r.GetProperty("tabs").EnumerateArray()
            .Select(t => (t.GetProperty("name").GetString() ?? "", t.GetProperty("label").GetString() ?? "")).ToList();
        var commands = r.GetProperty("commands").EnumerateArray().Select(c => c.GetString() ?? "").ToList();
        var subgrids = r.GetProperty("subgrids").EnumerateArray()
            .Select(g => (g.GetProperty("name").GetString() ?? "", g.GetProperty("label").GetString() ?? "", g.GetProperty("entity").GetString() ?? "")).ToList();
        return new FormInfo(r.GetProperty("entity").GetString() ?? "", r.GetProperty("id").GetString() ?? "", r.GetProperty("isNew").GetBoolean(),
            fields, tabs, commands, subgrids);
    }

    // ───────────────────────────── Ghi thao tác trên Dynamics 365 ─────────────────────────────

    /// <summary>Một thao tác người dùng làm trên D365 lúc ghi (k = open / set / save / command / tab / dialog / bpfNext / bpfPrev).</summary>
    public sealed record RecordedEvent(string Kind, string Entity = "", string Id = "", bool IsNew = false, string Field = "", string Value = "",
        string Label = "", long Time = 0);

    /// <summary>
    /// Bắt đầu ghi trong tab D365: gắn OnChange vào mọi field, OnSave, đổi tab, đổi giai đoạn BPF của từng form được mở,
    /// và bắt click nút trên thanh lệnh / hộp thoại. Gọi lại an toàn (trang tải lại thì cài lại).
    /// </summary>
    public static async Task StartRecordingAsync(string target, CancellationToken ct)
    {
        var tab = await TabAsync(target, ct);
        await EvalAsync(tab, Script(RecorderScript), ct);
    }

    /// <summary>Lấy các thao tác ghi được từ lần đọc trước; null nếu trình ghi không còn trong trang (trang vừa tải lại).</summary>
    public static async Task<List<RecordedEvent>?> DrainRecordingAsync(string target, CancellationToken ct)
    {
        var tab = await TabAsync(target, ct);
        var json = await EvalAsync(tab, "JSON.stringify(window.__saRecOn ? (window.__saRec || []).splice(0) : null)", ct);
        if (json is "" or "null") return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e =>
        {
            string S(string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return new RecordedEvent(S("k"), S("entity"), S("id"), e.TryGetProperty("isNew", out var n) && n.ValueKind == JsonValueKind.True,
                S("field"), S("value"), S("label"), e.TryGetProperty("t", out var t) && t.TryGetInt64(out var ms) ? ms : 0);
        }).ToList();
    }

    public static async Task StopRecordingAsync(string target, CancellationToken ct)
    {
        var tab = await TabAsync(target, ct);
        await EvalAsync(tab, "(() => { window.__saRecOn = false; clearInterval(window.__saRecTimer); return 'ok'; })()", ct);
    }

    /// <summary>
    /// Đổi thao tác ghi được thành các bước: gộp nhiều lần sửa liên tiếp một field thành một bước, form mở ra ngay sau khi
    /// bấm nút (vd "+ Mới") thành "Chờ form tải xong" thay vì mở lại form.
    /// </summary>
    public static List<ActionStep> ToSteps(IEnumerable<RecordedEvent> events)
    {
        var steps = new List<ActionStep>();
        RecordedEvent? previous = null;
        foreach (var e in events)
        {
            ActionStep? step = null;
            switch (e.Kind)
            {
                case "open":
                {
                    if (previous is { Kind: "open" } && previous.Entity == e.Entity && previous.Id == e.Id) continue; // ghi lại từ đầu trên cùng form
                    bool afterClick = previous is { Kind: "command" or "dialog" } && e.Time - previous.Time is >= 0 and < 5000;
                    step = Step(afterClick ? D365Action.WaitForm : D365Action.OpenForm, e.Entity, afterClick || e.IsNew ? "" : e.Id);
                    break;
                }
                case "set":
                    if (steps.Count > 0 && steps[^1] is { Type: StepType.Dynamics, D365Action: D365Action.SetField } last
                        && last.Text.Equals(e.Field, StringComparison.OrdinalIgnoreCase))
                    {
                        last.Arguments = e.Value;
                        previous = e;
                        continue;
                    }
                    step = Step(D365Action.SetField, e.Field, e.Value);
                    break;
                case "save":
                    if (steps.Count > 0 && steps[^1] is { Type: StepType.Dynamics, D365Action: D365Action.Save }) { previous = e; continue; }
                    step = Step(D365Action.Save);
                    break;
                case "command":
                    step = Step(D365Action.Command, e.Label);
                    break;
                case "tab":
                    step = Step(D365Action.SelectTab, e.Label);
                    break;
                case "dialog":
                    step = Step(D365Action.ConfirmDialog, e.Label);
                    break;
                case "bpfNext":
                    step = Step(D365Action.BpfNext);
                    break;
                case "bpfPrev":
                    step = Step(D365Action.BpfPrevious);
                    break;
            }
            if (step != null) steps.Add(step);
            previous = e;
        }
        return steps;

        static ActionStep Step(D365Action action, string text = "", string args = "")
        {
            var s = ActionStep.CreateDefault(StepType.Dynamics);
            s.D365Action = action;
            s.Text = text;
            s.Arguments = args;
            return s;
        }
    }

    /// <summary>Bước "Kiểm tra" giá trị hiện tại của field (dùng khi tạo kiểm tra từ form đang mở).</summary>
    public static ActionStep AssertFieldStep(FieldInfo f)
    {
        var s = ActionStep.CreateDefault(StepType.Assert);
        s.Condition = ConditionKind.D365FieldValue;
        s.Text = f.Name;
        s.CompareOp = f.Value.Length == 0 ? CompareOp.IsEmpty : CompareOp.Equals;
        s.Arguments = f.Value;
        s.Message = (f.Label.Length > 0 ? f.Label : f.Name) + (f.Value.Length == 0 ? " để trống" : $" = {f.Value}");
        return s;
    }

    private const string RecorderScript = """
        window.__saRec = window.__saRec || [];
        window.__saSeen = window.__saSeen || new WeakSet();
        const push = e => { if (window.__saRecOn) { e.t = Date.now(); window.__saRec.push(e); } };
        let current = null;
        const attach = () => {
          const fc = __fc();
          if (!fc || fc.data.entity === current) return;
          current = fc.data.entity;
          push({ k: 'open', entity: current.getEntityName(), id: __id(current.getId()), isNew: fc.ui.getFormType() === 1 });
          if (window.__saSeen.has(current)) return;
          window.__saSeen.add(current);
          fc.getAttribute().forEach(a => a.addOnChange(() => push({ k: 'set', field: a.getName(), value: __display(a, false) })));
          if (current.addOnSave) current.addOnSave(() => push({ k: 'save' }));
          fc.ui.tabs.get().forEach(t => t.addTabStateChange && t.addTabStateChange(() => {
            if (!t.getDisplayState || t.getDisplayState() === 'expanded') push({ k: 'tab', label: t.getLabel() });
          }));
          const p = fc.data.process;
          if (p && p.addOnStageChange) p.addOnStageChange(ctx => {
            const args = ctx && ctx.getEventArgs ? ctx.getEventArgs() : null;
            push({ k: args && args.getDirection && args.getDirection() === 'Previous' ? 'bpfPrev' : 'bpfNext' });
          });
        };
        if (!window.__saClickHooked) {
          window.__saClickHooked = true;
          document.addEventListener('click', ev => {
            const b = ev.target && ev.target.closest ? ev.target.closest('button,[role=menuitem],[role=button]') : null;
            if (!b) return;
            const label = (b.getAttribute('aria-label') || b.innerText || '').replace(/\s+/g, ' ').trim();
            const dataId = b.getAttribute('data-id') || '';
            if (b.closest('[role=dialog],[role=alertdialog]')) { push({ k: 'dialog', label }); return; }
            if (!b.closest('[data-id*="CommandBar" i],[data-lp-id*="commandbar" i],[role=menubar],[role=menu]')) return;
            if (/overflowbutton|morecommands/i.test(dataId)) return;
            if (/save(primary)?$|saveandclose$/i.test(dataId)) return; // ghi bằng sự kiện OnSave
            push({ k: 'command', label: label || dataId });
          }, true);
        }
        window.__saRecOn = true;
        clearInterval(window.__saRecTimer);
        window.__saRecTimer = setInterval(() => { try { attach(); } catch (e) { } }, 400);
        attach();
        return 'ok';
        """;

    // ───────────────────────────── Web API & dọn dữ liệu ─────────────────────────────

    private sealed record FetchResult(int Status, string Body, string EntityId);

    private static async Task<FetchResult> FetchAsync(string tab, string method, string path, string body, int timeoutMs, CancellationToken ct)
    {
        var json = await EvalAsync(tab, Script($$"""
            const base = Xrm.Utility.getGlobalContext().getClientUrl() + '/api/data/{{WebApiVersion}}/';
            const path = {{Js(path.Trim())}};
            const method = {{Js(method)}};
            const init = { method, credentials: 'include', headers: {
              'Accept': 'application/json', 'OData-MaxVersion': '4.0', 'OData-Version': '4.0',
              'Prefer': 'odata.include-annotations="*"' } };
            if (method !== 'GET' && method !== 'DELETE') { init.headers['Content-Type'] = 'application/json; charset=utf-8'; init.body = {{Js(body)}}; }
            const r = await fetch(/^https?:/i.test(path) ? path : base + path.replace(/^\/+/, ''), init);
            return JSON.stringify({ status: r.status, body: await r.text(), entityId: r.headers.get('OData-EntityId') || '' });
            """), ct, timeoutMs);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new FetchResult(root.GetProperty("status").GetInt32(), root.GetProperty("body").GetString() ?? "", root.GetProperty("entityId").GetString() ?? "");
    }

    private static async Task WebApiAsync(ActionStep s, string tab, int timeout, FlowContext ctx)
    {
        var method = string.IsNullOrWhiteSpace(s.Method) ? "GET" : s.Method.Trim().ToUpperInvariant();
        var path = Required(s.Arguments, "đường dẫn Web API (vd accounts?$select=name)");
        var r = await FetchAsync(tab, method, path, s.Text, timeout, ctx.Ct);
        ctx.Vars["http.status"] = r.Status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ctx.Vars["http.body"] = r.Body;
        Log.Info($"      {method} {path} → {r.Status}");
        if ((r.Status is < 200 or >= 300) && !s.Force)
            throw new InvalidOperationException($"Web API trả về {r.Status}: {Short(ApiError(r.Body))}");

        // POST tạo bản ghi: OData-EntityId = https://…/api/data/v9.2/accounts(guid) → ghi nhớ để dọn khi kết thúc test.
        string id = "";
        if (method == "POST" && RelativeEntityUrl(r.EntityId) is { } created)
        {
            id = NormalizeId(created[(created.IndexOf('(') + 1)..^1]);
            ctx.Vars["d365.lastId"] = id;
            TrackCreated(ctx, created);
            Log.Info($"      Đã tạo {created}");
        }
        SetVar(ctx, s.Variable, id.Length > 0 ? id : r.Body);
    }

    /// <summary>"…/api/data/v9.2/accounts(00000000-…)" → "accounts(00000000-…)"; null nếu không phải URL bản ghi.</summary>
    internal static string? RelativeEntityUrl(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url ?? "", @"([A-Za-z0-9_]+)\(\{?([0-9a-fA-F-]{36})\}?\)\s*$");
        return m.Success ? $"{m.Groups[1].Value}({m.Groups[2].Value.ToLowerInvariant()})" : null;
    }

    private static void TrackCreated(FlowContext ctx, string entityUrl)
    {
        var current = ctx.Vars.GetValueOrDefault(CreatedVar) ?? "";
        if (current.Split('\n').Contains(entityUrl, StringComparer.OrdinalIgnoreCase)) return;
        ctx.Vars[CreatedVar] = current.Length == 0 ? entityUrl : current.TrimEnd('\n') + "\n" + entityUrl;
    }

    /// <summary>Xóa các bản ghi trong {{d365.created}} (bản tạo sau xóa trước). Bản ghi đã bị xóa (404) được bỏ qua.</summary>
    public static async Task<int> CleanupAsync(FlowContext ctx, string target, CancellationToken ct)
    {
        var items = (ctx.Vars.GetValueOrDefault(CreatedVar) ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse().ToList();
        if (items.Count == 0)
        {
            Log.Info("      Không có dữ liệu test nào cần xóa.");
            return 0;
        }
        var tab = await TabAsync(target, ct);
        int deleted = 0;
        var failed = new List<string>();
        foreach (var item in items)
        {
            var r = await FetchAsync(tab, "DELETE", item, "", 30_000, ct);
            if (r.Status is >= 200 and < 300 or 404) deleted++;
            else failed.Add($"{item}: {r.Status} {Short(ApiError(r.Body))}");
        }
        ctx.Vars[CreatedVar] = string.Join("\n", failed.Select(f => f[..f.IndexOf(':')]).Reverse());
        Log.Info($"      🧹 Đã xóa {deleted}/{items.Count} bản ghi test.");
        if (failed.Count > 0) throw new InvalidOperationException("Không xóa được: " + string.Join("; ", failed));
        return deleted;
    }

    private static string ApiError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)) return m.GetString() ?? body;
        }
        catch (JsonException) { }
        return body;
    }

    // ───────────────────────────── Chờ form ─────────────────────────────

    /// <summary>
    /// Chờ form có Xrm.Page sẵn sàng. Khi vừa mở form (<see cref="D365Action.OpenForm"/>): chờ form mới thay form cũ
    /// và đúng bảng / Id (hoặc là form tạo mới).
    /// </summary>
    private static async Task WaitFormAsync(string tab, string entity, string id, bool newForm, int timeoutMs, CancellationToken ct,
        bool requireForm = true, string formId = "")
    {
        var probe = Script($$"""
            if (__onLogin()) throw new Error(__noXrm());
            if (document.readyState !== 'complete') return '';
            const fc = __fc();
            if (!fc) return {{Js(requireForm)}} ? '' : 'ok';
            if ({{Js(entity)}} && window.__saPrev && fc.data.entity === window.__saPrev) return '';
            const name = fc.data.entity.getEntityName(), id = __id(fc.data.entity.getId());
            if ({{Js(entity)}} && name !== {{Js(entity)}}) return '';
            if ({{Js(id)}} && id !== {{Js(id)}}) return '';
            if ({{Js(newForm)}} && fc.ui.getFormType() !== 1) return '';
            if ({{Js(formId)}} && __formNow(fc).id && __formNow(fc).id !== {{Js(formId)}}) return '';
            window.__saPrev = null;
            return 'ok';
            """);
        await PollAsync(tab, probe, timeoutMs,
            entity.Length > 0 ? $"Form {entity}{(id.Length > 0 ? " " + id : newForm ? " (tạo mới)" : "")} chưa mở" : "Form Dynamics 365 chưa tải xong", ct);
    }

    /// <summary>Chạy <paramref name="script"/> lặp lại tới khi trả về chuỗi khác rỗng; hết giờ thì báo lỗi (kèm nội dung hộp thoại đang mở nếu có).</summary>
    private static async Task<string> PollAsync(string tab, string script, int timeoutMs, string timeoutMessage, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string? lastError = null;
        while (true)
        {
            try
            {
                var r = await EvalAsync(tab, script, ct);
                if (r.Length > 0) return r;
                lastError = null;
            }
            catch (InvalidOperationException ex) when (IsTransient(ex.Message))
            {
                lastError = ex.Message; // trang đang chuyển — thử lại
            }
            if (sw.ElapsedMilliseconds >= timeoutMs)
            {
                string dialog = "";
                try { dialog = await EvalAsync(tab, Script("return __dialogText();"), ct); } catch (InvalidOperationException) { }
                throw new TimeoutException($"{timeoutMessage} sau {ActionStep.FormatMs(timeoutMs)}." +
                                           (dialog.Length > 0 ? $" Đang có hộp thoại: \"{Short(dialog)}\"." : "") +
                                           (lastError != null ? " " + lastError : ""));
            }
            await Task.Delay(PollMs, ct);
        }
    }

    /// <summary>Lỗi do trang đang tải / chuyển form (chưa có hàm trợ giúp, context bị hủy…), không phải lỗi thật của bước.</summary>
    private static bool IsTransient(string message) =>
        message.Contains("is not defined", StringComparison.Ordinal) || message.Contains("Cannot read properties of", StringComparison.Ordinal)
        || message.Contains("context was destroyed", StringComparison.OrdinalIgnoreCase);

    private static Task<string> EvalAsync(string tab, string script, CancellationToken ct, int timeoutMs = 30_000) =>
        BrowserClient.EvalAsync(tab, script, ct, timeoutMs + 5_000);

    // ───────────────────────────── Tiện ích ─────────────────────────────

    private static void SetVar(FlowContext ctx, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(name)) ctx.SetVar(name, value);
    }

    private static string Required(string value, string what) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"Chưa nhập {what}.") : value.Trim();

    /// <summary>Id bản ghi dạng chuẩn: bỏ ngoặc nhọn, chữ thường.</summary>
    internal static string NormalizeId(string id) => id.Trim().Trim('{', '}').Trim().ToLowerInvariant();

    private static string NormalizeEntity(string entity) => entity.Trim().ToLowerInvariant();

    private static string Js(string value) => JsonSerializer.Serialize(value ?? "");
    private static string Js(bool value) => value ? "true" : "false";

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s.Replace("\r", "").Replace("\n", " ⏎ ");

    /// <summary>Bọc đoạn mã (có thể dùng await, return) cùng các hàm trợ giúp đọc/ghi form.</summary>
    internal static string Script(string body) => "(async () => {\n" + Helpers + "\n" + body + "\n})()";

    /// <summary>Hàm trợ giúp chạy trong trang: lấy formContext, đọc/đổi giá trị theo kiểu field, đọc thông báo.</summary>
    private const string Helpers = """
        const __fc = () => { const X = window.Xrm; return X && X.Page && X.Page.data && X.Page.data.entity && X.Page.ui ? X.Page : null; };
        const __onLogin = () => /\/\/(login\.microsoftonline\.com|login\.live\.com|login\.windows\.net)\//i.test(location.href + '/') || /\/adfs\/ls/i.test(location.href);
        const __noXrm = () => __onLogin()
          ? 'Phiên đăng nhập Dynamics 365 đã hết — trình duyệt đang ở trang đăng nhập Microsoft. Thêm bước "Đăng nhập Microsoft" (tài khoản test) trước bước này, hoặc đăng nhập lại trong hồ sơ trình duyệt.'
          : 'Trang không phải Dynamics 365 (không có Xrm) — mở app trước bằng bước Trình duyệt.';
        const __need = () => { const fc = __fc(); if (!fc) throw new Error(__onLogin() ? __noXrm() : 'Trang hiện tại không phải form Dynamics 365 (chưa có Xrm.Page) — mở form bằng bước "Mở form bản ghi" hoặc chờ form tải xong.'); return fc; };
        const __norm = s => String(s ?? '').normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').replace(/\s+/g, ' ').trim().toLowerCase();
        const __visible = e => !!e && (e.offsetParent !== null || getComputedStyle(e).position === 'fixed');
        const __id = v => String(v ?? '').replace(/[{}]/g, '').toLowerCase();
        const __formNow = fc => {
          const fs = fc && fc.ui && fc.ui.formSelector;
          const it = fs && fs.getCurrentItem ? fs.getCurrentItem() : null;
          return it ? { id: __id(it.getId()), label: String(it.getLabel() || '') } : { id: '', label: '' };
        };
        const __formId = async (entity, form) => {
          form = String(form ?? '').trim();
          if (!form) return '';
          if (/^\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?$/i.test(form)) return __id(form);
          const q = "?$select=formid,name&$filter=objecttypecode eq '" + entity + "' and type eq 2";
          const forms = (await Xrm.WebApi.retrieveMultipleRecords('systemform', q)).entities;
          const f = forms.find(x => x.name === form) || forms.find(x => __norm(x.name) === __norm(form));
          if (!f) throw new Error('Bảng ' + entity + ' không có form chính "' + form + '". Có: ' + (forms.map(x => x.name).join(', ') || '(không đọc được danh sách form)'));
          return __id(f.formid);
        };
        const __grid = name => {
          const fc = __need();
          const all = fc.ui.controls ? fc.ui.controls.get() : [];
          const c = fc.getControl(String(name).trim()) || all.find(x => x.getGrid && x.getLabel && __norm(x.getLabel()) === __norm(name));
          if (c && c.getGrid) return c;
          const grids = all.filter(x => x.getGrid).map(x => x.getName() + (x.getLabel && x.getLabel() ? ' (' + x.getLabel() + ')' : ''));
          throw new Error('Form không có subgrid "' + name + '". Có: ' + (grids.join(', ') || '(không có subgrid nào)'));
        };
        const __cellText = v => {
          if (v === null || v === undefined) return '';
          if (Array.isArray(v)) return v.map(x => (x && typeof x === 'object') ? String(x.name || x.text || x.id || '') : String(x)).join('; ');
          if (v instanceof Date) return __fmtDate(v, v.getHours() || v.getMinutes());
          if (typeof v === 'object') return String(v.name || v.text || v.id || '');
          return String(v);
        };
        const __gridRows = c => c.getGrid().getRows().get().map((r, i) => {
          const e = r.getData().getEntity();
          const cells = {};
          const attrs = e.attributes ? (typeof e.attributes.get === 'function' ? e.attributes.get() : Array.from(e.attributes)) : [];
          attrs.forEach(a => { try { cells[a.getName()] = __cellText(a.getValue()); } catch (x) { } });
          return { index: i + 1, entity: e.getEntityName(), id: __id(e.getId()), name: String(e.getPrimaryAttributeValue() ?? ''), cells };
        });
        const __gridRow = (rows, ref) => {
          ref = String(ref ?? '').trim() || '1';
          if (/^\d+$/.test(ref)) return rows[parseInt(ref, 10) - 1] || null;
          const n = __norm(ref);
          return rows.find(r => __norm(r.name) === n) || rows.find(r => __norm(r.name).includes(n))
              || rows.find(r => Object.values(r.cells).some(v => __norm(v).includes(n))) || null;
        };
        const __cmdText = e => __norm(e.getAttribute('aria-label') || e.getAttribute('title') || e.innerText || '');
        const __cmdAll = () => Array.from(document.querySelectorAll('button, [role=menuitem], [role=button], [role=menuitemcheckbox]')).filter(__visible);
        const __cmdFind = label => {
          const want = __norm(label), raw = String(label).trim().toLowerCase(), all = __cmdAll();
          return all.find(e => __cmdText(e) === want) || all.find(e => __norm(e.innerText || '') === want)
              || all.find(e => (e.getAttribute('data-id') || '').toLowerCase().includes(raw) && raw.length > 3) || null;
        };
        const __cmdMore = () => __cmdAll().find(e => /overflowbutton|moreCommands/i.test(e.getAttribute('data-id') || '')
          || /^(more commands|thêm lệnh|more)$/i.test((e.getAttribute('aria-label') || '').trim())) || null;
        const __cmdDisabled = e => !!e.disabled || e.getAttribute('aria-disabled') === 'true';
        const __attr = name => {
          const a = __need().getAttribute(String(name).trim());
          if (!a) throw new Error('Form không có field "' + name + '" (dùng tên logic, vd name, telephone1, parentcustomerid).');
          return a;
        };
        const __pad = n => String(n).padStart(2, '0');
        const __fmtDate = (d, withTime) => __pad(d.getDate()) + '/' + __pad(d.getMonth() + 1) + '/' + d.getFullYear() + (withTime ? ' ' + __pad(d.getHours()) + ':' + __pad(d.getMinutes()) : '');
        const __display = (a, raw) => {
          const v = a.getValue();
          if (v === null || v === undefined) return '';
          switch (a.getAttributeType()) {
            case 'lookup': return v.map(x => raw ? __id(x.id) : (x.name || '')).join('; ');
            case 'optionset': return raw ? String(v) : String(a.getText() ?? v);
            case 'multiselectoptionset': return raw ? v.join(';') : (a.getText() || []).join('; ');
            case 'boolean': return v ? 'true' : 'false';
            case 'datetime': return raw ? v.toISOString() : __fmtDate(v, (a.getFormat && a.getFormat()) !== 'date' && (v.getHours() || v.getMinutes()));
            default: return String(v);
          }
        };
        const __num = s => {
          let t = String(s).replace(/[\s ]/g, '');
          const dot = t.lastIndexOf('.'), comma = t.lastIndexOf(',');
          if (dot >= 0 && comma >= 0) t = dot > comma ? t.replace(/,/g, '') : t.replace(/\./g, '').replace(',', '.');
          else if (comma >= 0) t = /^-?\d{1,3}(,\d{3})+$/.test(t) ? t.replace(/,/g, '') : t.replace(',', '.');
          else if (/^-?\d{1,3}(\.\d{3}){2,}$/.test(t)) t = t.replace(/\./g, '');
          const n = Number(t);
          return t === '' || isNaN(n) ? null : n;
        };
        const __date = s => {
          let m = s.match(/^(\d{1,2})[\/.-](\d{1,2})[\/.-](\d{4})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?)?$/);
          if (m) return new Date(+m[3], +m[2] - 1, +m[1], +(m[4] || 0), +(m[5] || 0), +(m[6] || 0));
          m = s.match(/^(\d{4})-(\d{1,2})-(\d{1,2})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?)?$/);
          if (m) return new Date(+m[1], +m[2] - 1, +m[3], +(m[4] || 0), +(m[5] || 0), +(m[6] || 0));
          const d = new Date(s);
          if (isNaN(d)) throw new Error('Ngày "' + s + '" không hợp lệ (dùng dd/MM/yyyy hoặc dd/MM/yyyy HH:mm).');
          return d;
        };
        const __option = (a, s) => {
          const opts = a.getOptions ? (a.getOptions() || []) : [];
          if (/^-?\d+$/.test(s.trim()) && (opts.length === 0 || opts.some(o => o.value === parseInt(s, 10)))) return parseInt(s, 10);
          const n = __norm(s);
          const o = opts.find(o => __norm(o.text) === n) || opts.find(o => __norm(o.text).startsWith(n));
          if (!o) throw new Error('Field "' + a.getName() + '" không có lựa chọn "' + s + '". Có: ' + opts.map(o => o.text + ' (' + o.value + ')').join(', '));
          return o.value;
        };
        const __lookup = async (a, s) => {
          const ctrl = a.controls ? a.controls.get()[0] : null;
          let types = ((ctrl && ctrl.getEntityTypes && ctrl.getEntityTypes()) || []).map(t => t.toLowerCase());
          const m = s.match(/^(?:([a-z0-9_]+)\s*:\s*)?\{?([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\}?$/i);
          if (m) {
            const type = (m[1] || types[0] || '').toLowerCase();
            if (!type) throw new Error('Không biết bảng của field "' + a.getName() + '" — nhập dạng tenbang:guid, vd account:' + m[2]);
            const md = await Xrm.Utility.getEntityMetadata(type, []);
            let name = '';
            try { name = (await Xrm.WebApi.retrieveRecord(type, m[2], '?$select=' + md.PrimaryNameAttribute))[md.PrimaryNameAttribute] || ''; }
            catch (e) { throw new Error('Không tìm thấy bản ghi ' + type + ' ' + m[2] + ': ' + (e.message || e)); }
            return [{ id: m[2].toLowerCase(), entityType: type, name }];
          }
          let name = s;
          const pm = s.match(/^([a-z0-9_]+)\s*:\s*(.+)$/i);
          if (pm && types.includes(pm[1].toLowerCase())) { types = [pm[1].toLowerCase()]; name = pm[2].trim(); }
          if (!types.length) throw new Error('Không biết bảng của field "' + a.getName() + '" — nhập dạng tenbang:Tên bản ghi hoặc tenbang:guid.');
          const found = [];
          for (const type of types) {
            const md = await Xrm.Utility.getEntityMetadata(type, []);
            const q = '?$select=' + md.PrimaryIdAttribute + ',' + md.PrimaryNameAttribute + '&$filter=' + md.PrimaryNameAttribute
                    + ' eq ' + encodeURIComponent("'" + name.replace(/'/g, "''") + "'") + '&$top=3';
            const r = await Xrm.WebApi.retrieveMultipleRecords(type, q);
            for (const e of r.entities) found.push({ id: __id(e[md.PrimaryIdAttribute]), entityType: type, name: e[md.PrimaryNameAttribute] });
          }
          if (found.length === 0) throw new Error('Không tìm thấy bản ghi tên "' + name + '" (' + types.join(', ') + ') cho field "' + a.getName() + '".');
          if (found.length > 1) throw new Error('Có nhiều bản ghi tên "' + name + '" — nhập Id để chọn đúng, vd ' + found[0].entityType + ':' + found[0].id);
          return [found[0]];
        };
        const __parse = async (a, s) => {
          s = String(s ?? '').trim();
          if (s === '') return null;
          switch (a.getAttributeType()) {
            case 'boolean': {
              const n = __norm(s);
              if (['1', 'true', 'yes', 'co', 'x', 'y'].includes(n)) return true;
              if (['0', 'false', 'no', 'khong', 'n'].includes(n)) return false;
              const opts = a.getOptions ? (a.getOptions() || []) : [];
              const o = opts.find(o => __norm(o.text) === n);
              if (o) return !!o.value;
              throw new Error('Giá trị "' + s + '" không hợp lệ cho field Có/Không "' + a.getName() + '" (dùng true/false, 1/0, có/không).');
            }
            case 'optionset': return __option(a, s);
            case 'multiselectoptionset': return s.split(/[;,]/).map(x => x.trim()).filter(Boolean).map(x => __option(a, x));
            case 'integer': case 'decimal': case 'double': case 'money': {
              const n = __num(s);
              if (n === null) throw new Error('"' + s + '" không phải số (field ' + a.getName() + ').');
              return a.getAttributeType() === 'integer' ? Math.round(n) : n;
            }
            case 'datetime': return __date(s);
            case 'lookup': return await __lookup(a, s);
            default: return s;
          }
        };
        const __dialogText = () => Array.from(document.querySelectorAll('[role=dialog],[role=alertdialog]')).filter(__visible)
          .map(d => (d.innerText || '').replace(/\s+/g, ' ').trim()).filter(Boolean).join(' | ');
        const __notifications = () => {
          const els = Array.from(document.querySelectorAll('[data-id*="otification" i], [data-id*="error-message" i], [data-id*="errorDialog" i], [role=alert]')).filter(__visible);
          const leaf = els.filter(e => !els.some(o => o !== e && e.contains(o) && (o.innerText || '').trim()));
          const out = [];
          for (const e of leaf) { const t = (e.innerText || '').replace(/\s+/g, ' ').trim(); if (t && !out.includes(t)) out.push(t); }
          const d = __dialogText();
          if (d && !out.includes(d)) out.push(d);
          return out;
        };
        """;
}
