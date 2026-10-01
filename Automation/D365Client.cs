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

    /// <summary>Tab Dynamics 365 mặc định khi bước không ghi tab: URL chứa main.aspx (Unified Interface) hoặc dynamics.com.</summary>
    private static readonly string[] PreferredTabs = ["main.aspx", ".dynamics.com", ".crm"];

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
                await EvalAsync(tab, Script($$"""
                    const X = window.Xrm;
                    if (!X || !X.Navigation) throw new Error('Trang không phải Dynamics 365 (không có Xrm.Navigation) — mở app trước bằng bước Trình duyệt.');
                    const cur = X.Page && X.Page.data && X.Page.data.entity;
                    window.__saPrev = cur || null;
                    const o = { entityName: {{Js(entity)}} };
                    if ({{Js(id)}}) {
                      if (cur && cur.getEntityName() === o.entityName && __id(cur.getId()) === {{Js(id)}}) { window.__saPrev = null; return 'same'; }
                      o.entityId = {{Js(id)}};
                    }
                    X.Navigation.openForm(o);
                    return 'ok';
                    """), ct);
                await WaitFormAsync(tab, entity, id, newForm: id.Length == 0, timeout, ct);
                break;
            }

            case D365Action.OpenView:
            {
                var entity = Required(s.Text, "tên bảng (logical name, vd account)").ToLowerInvariant();
                var viewId = NormalizeId(s.Arguments);
                await EvalAsync(tab, Script($$"""
                    const X = window.Xrm;
                    if (!X || !X.Navigation) throw new Error('Trang không phải Dynamics 365 (không có Xrm.Navigation).');
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
                    const want = __norm({{Js(label)}}), raw = {{Js(label)}}.toLowerCase();
                    const textOf = e => __norm(e.getAttribute('aria-label') || e.getAttribute('title') || e.innerText || '');
                    const all = Array.from(document.querySelectorAll('button, [role=menuitem], [role=button], [role=menuitemcheckbox]')).filter(__visible);
                    const hit = all.find(e => textOf(e) === want) || all.find(e => __norm(e.innerText || '') === want)
                             || all.find(e => (e.getAttribute('data-id') || '').toLowerCase().includes(raw) && raw.length > 3);
                    if (hit) { hit.scrollIntoView({ block: 'center' }); hit.click(); return textOf(hit) || raw; }
                    const more = all.find(e => /overflowbutton|moreCommands/i.test(e.getAttribute('data-id') || '')
                                             || /^(more commands|thêm lệnh|more)$/i.test((e.getAttribute('aria-label') || '').trim()));
                    if (more && more.getAttribute('aria-expanded') !== 'true') more.click();
                    return '';
                    """), timeout, $"Không thấy nút \"{label}\" trên thanh lệnh", ct);
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
        }
    }

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
        IReadOnlyList<(string Name, string Label)> Tabs, IReadOnlyList<string> Commands);

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
            return JSON.stringify({ entity: fc.data.entity.getEntityName(), id: __id(fc.data.entity.getId()), isNew: fc.ui.getFormType() === 1, fields, tabs, commands });
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
        return new FormInfo(r.GetProperty("entity").GetString() ?? "", r.GetProperty("id").GetString() ?? "", r.GetProperty("isNew").GetBoolean(),
            fields, tabs, commands);
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
        bool requireForm = true)
    {
        var probe = Script($$"""
            if (document.readyState !== 'complete') return '';
            const fc = __fc();
            if (!fc) return {{Js(requireForm)}} ? '' : 'ok';
            if ({{Js(entity)}} && window.__saPrev && fc.data.entity === window.__saPrev) return '';
            const name = fc.data.entity.getEntityName(), id = __id(fc.data.entity.getId());
            if ({{Js(entity)}} && name !== {{Js(entity)}}) return '';
            if ({{Js(id)}} && id !== {{Js(id)}}) return '';
            if ({{Js(newForm)}} && fc.ui.getFormType() !== 1) return '';
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
        const __need = () => { const fc = __fc(); if (!fc) throw new Error('Trang hiện tại không phải form Dynamics 365 (chưa có Xrm.Page) — mở form bằng bước "Mở form bản ghi" hoặc chờ form tải xong.'); return fc; };
        const __norm = s => String(s ?? '').normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').replace(/\s+/g, ' ').trim().toLowerCase();
        const __visible = e => !!e && (e.offsetParent !== null || getComputedStyle(e).position === 'fixed');
        const __id = v => String(v ?? '').replace(/[{}]/g, '').toLowerCase();
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
