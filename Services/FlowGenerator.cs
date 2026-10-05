using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ScheduleApp.Automation;
using ScheduleApp.Models;
using ScheduleApp.Native;

namespace ScheduleApp.Services;

/// <summary>
/// Tạo / sửa flow từ mô tả bằng lời qua Claude. AI trả flow qua công cụ "build_flow" (các bước theo đúng mô hình <see cref="ActionStep"/>),
/// ScheduleApp kiểm tra từng bước (khối Nếu/Lặp, cú pháp phím và bộ chọn, công việc được gọi, kết nối API…) rồi gửi lỗi lại để AI tự sửa.
/// Giữ hội thoại để người dùng yêu cầu sửa tiếp ("thêm bước gửi thông báo", "dùng Edge thay Chrome"…).
/// </summary>
public sealed class FlowGenerator
{
    public enum Mode
    {
        /// <summary>AI trả về toàn bộ flow (viết mới hoặc sửa flow đang có).</summary>
        Replace,
        /// <summary>AI chỉ trả về các bước chèn vào vị trí <see cref="Context.InsertAt"/>.</summary>
        Insert
    }

    /// <summary>Thông tin về công việc đang soạn gửi kèm yêu cầu.</summary>
    public sealed class Context
    {
        public string JobName { get; init; } = "";
        public IReadOnlyList<ActionStep> Steps { get; init; } = [];
        /// <summary>Chế độ chèn: số bước đứng trước vị trí chèn.</summary>
        public int InsertAt { get; init; }
        public IReadOnlyList<VariableDef> Variables { get; init; } = [];
        public IReadOnlyList<Job> OtherJobs { get; init; } = [];
        /// <summary>Kết nối API đã khai báo: (tên, mô tả URL gốc / kiểu xác thực).</summary>
        public IReadOnlyList<(string Name, string Info)> Connections { get; init; } = [];
        /// <summary>Cửa sổ đang mở (chỉ gửi khi người dùng cho phép).</summary>
        public IReadOnlyList<string> OpenWindows { get; set; } = [];
        public bool EmailTrigger { get; init; }
        public bool NotificationsEnabled { get; init; }
    }

    public sealed class Result
    {
        public Mode Mode { get; init; }
        public string Name { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<ActionStep> Steps { get; } = [];
        public List<VariableDef> Variables { get; } = [];
        /// <summary>Việc người dùng cần kiểm tra / điền (do AI ghi).</summary>
        public List<string> Notes { get; } = [];
        /// <summary>Lỗi còn lại sau khi AI đã tự sửa — vẫn áp dụng được nhưng cần sửa tay.</summary>
        public List<string> Problems { get; } = [];
        /// <summary>Số lần AI phải tự sửa lỗi.</summary>
        public int Repairs { get; set; }
        /// <summary>Lịch chạy khi người dùng có nói lúc nào chạy ("8h sáng mỗi ngày"…); null = không nói.</summary>
        public ScheduleConfig? Schedule { get; set; }
        public bool SkipHolidays { get; set; }
        /// <summary>Kích hoạt theo sự kiện (phím tắt, email mới, file mới…) khi người dùng có nói.</summary>
        public List<JobTrigger> Triggers { get; } = [];
    }

    private const int MaxRepairs = 2;
    private const int TimeoutMs = 300_000;
    private const string ImagePlaceholder = "(hình mẫu có sẵn — giữ nguyên)";

    private readonly Context _ctx;
    private readonly JsonArray _messages = [];
    private string? _pendingToolId;

    public FlowGenerator(Context ctx) => _ctx = ctx;

    /// <summary>Đã có kết quả (lần gửi tiếp theo là yêu cầu sửa).</summary>
    public bool HasResult => _pendingToolId != null;

    /// <summary>Số lần gọi API (kiểm thử).</summary>
    internal int Calls { get; private set; }

    public event Action<string>? Progress;

    /// <summary>Gửi yêu cầu (lần đầu, hoặc yêu cầu sửa kết quả trước) và trả về flow đã kiểm tra.</summary>
    public async Task<Result> SendAsync(string prompt, Mode mode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("Hãy mô tả việc cần tự động hóa.");
        int rollback = _messages.Count;
        var pending = _pendingToolId;
        try
        {
            var content = new JsonArray();
            if (_pendingToolId != null) content.Add(ToolResult(_pendingToolId, "Đã hiển thị flow cho người dùng.", false));
            content.Add(new JsonObject { ["type"] = "text", ["text"] = rollback == 0 ? FirstMessage(prompt, mode) : FollowUp(prompt, mode) });
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });

            for (int attempt = 0; ; attempt++)
            {
                Progress?.Invoke(attempt == 0 ? "Đang tạo flow…" : $"Đang tự sửa {(attempt == 1 ? "" : "lần " + attempt + " ")}các lỗi AI mắc phải…");
                var (toolId, input) = await CallAsync(ct);
                _pendingToolId = toolId;
                var result = Parse(input, mode);
                result.Repairs = attempt;
                if (result.Problems.Count == 0 || attempt >= MaxRepairs) return result;

                var problems = "Flow chưa chạy được vì các lỗi sau. Hãy gọi lại build_flow với flow đã sửa (trả về đầy đủ như lần trước):\n- " +
                               string.Join("\n- ", result.Problems);
                _messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray { ToolResult(toolId, problems, true) } });
            }
        }
        catch
        {
            // Bỏ phần hội thoại dở dang để lần gửi sau vẫn hợp lệ.
            while (_messages.Count > rollback) _messages.RemoveAt(_messages.Count - 1);
            _pendingToolId = pending;
            throw;
        }
    }

    private async Task<(string Id, JsonObject Input)> CallAsync(CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = AiClient.Model,
            ["max_tokens"] = 16_000,
            // Phần hướng dẫn dài và cố định → cache để các lần tự sửa / yêu cầu sửa rẻ và nhanh hơn.
            ["system"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = SystemPrompt, ["cache_control"] = new JsonObject { ["type"] = "ephemeral" } }
            },
            ["tools"] = new JsonArray { JsonNode.Parse(ToolSchema) },
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = "build_flow" },
            ["messages"] = _messages.DeepClone()
        };
        Calls++;
        var root = await AiClient.PostAsync(body, TimeoutMs, ct);
        if (root.TryGetProperty("stop_reason", out var stop) && stop.GetString() == "max_tokens")
            throw new InvalidOperationException("Flow quá dài nên câu trả lời của AI bị cắt giữa chừng — hãy chia yêu cầu thành nhiều phần nhỏ (dùng chế độ \"Chỉ thêm bước\").");

        var content = root.GetProperty("content");
        _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = JsonNode.Parse(content.GetRawText()) });
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use" &&
                block.TryGetProperty("input", out var input) && JsonNode.Parse(input.GetRawText()) is JsonObject obj)
                return (block.GetProperty("id").GetString() ?? "", obj);
        }
        throw new InvalidOperationException("AI không trả về flow — hãy thử lại hoặc mô tả cụ thể hơn.");
    }

    private static JsonObject ToolResult(string id, string text, bool isError)
    {
        var o = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = text };
        if (isError) o["is_error"] = true;
        return o;
    }

    // ───────────────────────────── Nội dung gửi đi ─────────────────────────────

    private string FirstMessage(string prompt, Mode mode)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Yêu cầu của người dùng").AppendLine(prompt.Trim()).AppendLine();
        sb.AppendLine("# Chế độ");
        sb.AppendLine(ModeText(mode)).AppendLine();

        sb.AppendLine("# Bối cảnh");
        sb.AppendLine($"- Bây giờ: {DateTime.Now.ToString("dddd dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("vi-VN"))}");
        if (_ctx.JobName.Trim().Length > 0) sb.AppendLine($"- Tên công việc hiện tại: \"{_ctx.JobName.Trim()}\"");
        sb.AppendLine("- Biến đã khai báo: " + (_ctx.Variables.Count == 0
            ? "(chưa có)"
            : string.Join(", ", _ctx.Variables.Select(v => $"{v.Name} = \"{Short(v.Value, 60)}\""))));
        if (_ctx.OtherJobs.Count > 0)
        {
            sb.AppendLine("- Công việc khác gọi được bằng CallJob (Target = đúng tên):");
            foreach (var j in _ctx.OtherJobs.Take(40))
                sb.AppendLine($"  - \"{j.Name}\" ({j.Steps.Count} bước): " +
                              string.Join("; ", j.Steps.Where(s => s.Enabled && !s.IsControl).Take(4).Select(s => Short(s.Describe(), 70))));
        }
        else sb.AppendLine("- Chưa có công việc nào khác để gọi bằng CallJob.");
        sb.AppendLine(_ctx.Connections.Count == 0
            ? "- Kết nối API: chưa khai báo (HttpRequest phải dùng URL đầy đủ; nếu API cần đăng nhập, ghi notes hướng dẫn tạo kết nối trong ⚙ Cài đặt → Kết nối API)."
            : "- Kết nối API (Connection): " + string.Join("; ", _ctx.Connections.Select(c => $"\"{c.Name}\" — {c.Info}")));
        if (_ctx.EmailTrigger)
            sb.AppendLine("- Công việc chạy khi có email mới: có sẵn {{email.subject}}, {{email.from}}, {{email.body}}, " +
                          "{{email.attachments}} (danh sách đường dẫn file đính kèm, mỗi dòng một file), {{email.attachmentDir}}.");
        sb.AppendLine(_ctx.NotificationsEnabled ? "- Kênh thông báo (Notify): đã bật." : "- Kênh thông báo (Notify): chưa bật — nếu dùng Notify, ghi notes nhắc bật trong ⚙ Cài đặt → Thông báo.");
        if (!AiClient.IsConfigured) sb.AppendLine("- Khóa Claude cho bước AskAi: chưa có.");
        if (_ctx.OpenWindows.Count > 0)
        {
            sb.AppendLine("- Cửa sổ đang mở (tiêu đề [tiến trình]):");
            foreach (var w in _ctx.OpenWindows.Take(40)) sb.AppendLine("  - " + Short(w, 100));
        }

        if (_ctx.Steps.Count > 0)
        {
            sb.AppendLine().AppendLine($"# Flow hiện tại ({_ctx.Steps.Count} bước; _ref = số thứ tự bước, bắt đầu từ 0)");
            var arr = new JsonArray();
            for (int i = 0; i < _ctx.Steps.Count; i++) arr.Add(Compact(_ctx.Steps[i], i));
            sb.AppendLine(arr.ToJsonString(JsonDefaults.Options));
        }
        return sb.ToString();
    }

    private string FollowUp(string prompt, Mode mode) =>
        "Yêu cầu sửa tiếp: " + prompt.Trim() + "\n\n" +
        (mode == Mode.Replace
            ? "Sửa flow vừa đề xuất theo yêu cầu và trả về TOÀN BỘ flow sau khi sửa."
            : $"Trả về các bước chèn vào sau bước thứ {_ctx.InsertAt} của flow hiện tại (thay cho đề xuất trước).");

    private string ModeText(Mode mode) => mode switch
    {
        Mode.Insert =>
            $"CHÈN: chỉ trả về các bước MỚI, sẽ được chèn vào sau bước thứ {_ctx.InsertAt} (đếm từ 1) của flow hiện tại. " +
            "Khối Nếu/Lặp trong phần chèn phải tự đóng đủ. Dùng được biến đã có trong flow hiện tại. Không lặp lại các bước đã có.",
        _ when _ctx.Steps.Count > 0 =>
            "VIẾT LẠI / SỬA: flow hiện tại ở cuối tin nhắn. Sửa hoặc bổ sung theo yêu cầu và trả về TOÀN BỘ flow mới. " +
            "Bước giữ lại hoặc chỉ sửa ít từ flow cũ thì giữ trường \"_ref\" của nó; bỏ bước nào thì không trả về bước đó.",
        _ => "TẠO MỚI: trả về toàn bộ flow."
    };

    /// <summary>Bước ở dạng gọn (chỉ các trường khác mặc định) để gửi cho AI; hình mẫu thay bằng chữ giữ chỗ.</summary>
    internal static JsonObject Compact(ActionStep s, int? reference = null)
    {
        var full = JsonSerializer.SerializeToNode(s, JsonDefaults.Options)!.AsObject();
        var defaults = JsonSerializer.SerializeToNode(ActionStep.CreateDefault(s.Type), JsonDefaults.Options)!.AsObject();
        var o = new JsonObject();
        if (reference != null) o["_ref"] = reference.Value;
        foreach (var (key, value) in full)
        {
            if (key == nameof(ActionStep.ImageData))
            {
                if (s.ImageData.Length > 0) o[key] = ImagePlaceholder;
                continue;
            }
            if (key is nameof(ActionStep.Breakpoint) or nameof(ActionStep.JobRef) or nameof(ActionStep.MediaDurationMs) or nameof(ActionStep.MonitorId)) continue;
            if (key == nameof(ActionStep.Type) || !JsonNode.DeepEquals(value, defaults[key])) o[key] = value?.DeepClone();
        }
        return o;
    }

    // ───────────────────────────── Đọc & kiểm tra kết quả ─────────────────────────────

    // Chỉ bỏ các trường [JsonIgnore] luôn-bỏ; GIỮ các trường [JsonIgnore(WhenWritingDefault/WhenWritingNull)]
    // (Monitor, MonitorId, ImageOffset…) để không đánh rơi giá trị AI trả về (vd "Monitor": 2 bị thành 0).
    private static readonly Dictionary<string, string> StepProperties =
        typeof(ActionStep).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always })
            .ToDictionary(p => p.Name, p => p.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Lenient = new(JsonDefaults.Options)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    internal Result Parse(JsonObject input, Mode mode)
    {
        var r = new Result { Mode = mode, Name = Str(input, "name").Trim(), Summary = Str(input, "summary").Trim() };
        if (input["notes"] is JsonArray notes)
            r.Notes.AddRange(notes.Select(n => n?.ToString().Trim() ?? "").Where(n => n.Length > 0));
        if (input["variables"] is JsonArray vars)
            foreach (var v in vars.OfType<JsonObject>())
            {
                var name = Str(v, "name").Trim().Trim('{', '}').Trim();
                if (name.Length > 0 && r.Variables.All(x => !x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    r.Variables.Add(new VariableDef { Name = name, Value = Str(v, "value") });
            }
        if (input["schedule"] is JsonObject schedule) ParseSchedule(schedule, r);
        if (input["triggers"] is JsonArray triggers) ParseTriggers(triggers, r);

        if (input["steps"] is not JsonArray steps || steps.Count == 0)
        {
            r.Problems.Add("Flow không có bước nào (trường steps rỗng).");
            return r;
        }
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i] is not JsonObject obj)
            {
                r.Problems.Add($"Bước {i + 1}: không phải object JSON.");
                continue;
            }
            var step = ParseStep(obj, i + 1, r.Problems);
            if (step != null) r.Steps.Add(step);
        }
        if (r.Problems.Count > 0) return r;

        for (int i = 0; i < r.Steps.Count; i++) Check(r.Steps[i], i + 1, r);

        // Cấu trúc khối kiểm tra trên flow hoàn chỉnh (chế độ chèn: flow hiện tại + phần chèn).
        var whole = mode == Mode.Replace ? r.Steps : [.. _ctx.Steps.Take(_ctx.InsertAt), .. r.Steps, .. _ctx.Steps.Skip(_ctx.InsertAt)];
        var structure = FlowStructure.Build(whole);
        foreach (var e in structure.Errors)
            r.Problems.Add(mode == Mode.Replace ? e : e + $" (đánh số theo flow sau khi chèn {r.Steps.Count} bước vào sau bước {_ctx.InsertAt})");

        bool usesBrowser = whole.Any(s =>
            (s.Type == StepType.Browser && s.BrowserAction != BrowserAction.Launch) || s.Type == StepType.Dynamics ||
            (s.HasCondition && s.Condition.ToString().StartsWith("D365", StringComparison.Ordinal)) || (s.HasCondition && s.Condition == ConditionKind.BrowserElement));
        if (usesBrowser && !whole.Any(s => s is { Type: StepType.CallJob, JobRef: not null } or { Type: StepType.Browser, BrowserAction: BrowserAction.Launch }))
            r.Problems.Add("Flow dùng trình duyệt (bước Browser / Dynamics / điều kiện D365) nhưng không có bước Browser với BrowserAction \"Launch\" " +
                           "(mở trình duyệt ở chế độ điều khiển) hay CallJob tới công việc mở trình duyệt trước đó.");
        return r;
    }

    private ActionStep? ParseStep(JsonObject obj, int n, List<string> problems)
    {
        var typeName = Str(obj, "Type");
        if (!Enum.TryParse<StepType>(typeName, ignoreCase: true, out var type) || !Enum.IsDefined(type))
        {
            problems.Add($"Bước {n}: Type \"{typeName}\" không hợp lệ.");
            return null;
        }
        // Bắt đầu từ giá trị mặc định của loại bước (timeout, nghỉ sau bước…) rồi ghi đè các trường AI trả về.
        var merged = JsonSerializer.SerializeToNode(ActionStep.CreateDefault(type), JsonDefaults.Options)!.AsObject();
        int? reference = null;
        foreach (var (key, value) in obj)
        {
            if (key.Equals("_ref", StringComparison.OrdinalIgnoreCase))
            {
                if (value is JsonValue jv && (jv.TryGetValue(out int refIndex) || int.TryParse(jv.ToString(), out refIndex))) reference = refIndex;
                continue;
            }
            if (value != null && StepProperties.TryGetValue(key, out var canonical) && canonical != nameof(ActionStep.Type))
                merged[canonical] = value.DeepClone();
        }

        ActionStep step;
        try
        {
            step = merged.Deserialize<ActionStep>(Lenient)!;
        }
        catch (JsonException ex)
        {
            problems.Add($"Bước {n} ({type}): giá trị không hợp lệ ở {ex.Path?.TrimStart('$', '.')} — {FirstLine(ex.Message)}");
            return null;
        }
        step.Type = type;
        step.Breakpoint = false;

        // Hình mẫu không gửi cho AI → lấy lại từ bước gốc cùng _ref.
        // Chữ giữ chỗ (hoặc chữ bất kỳ không phải base64 của ảnh PNG).
        bool placeholder = step.ImageData.Length > 0 && (step.ImageData.Length < 64 || step.ImageData.Any(char.IsWhiteSpace));
        if (reference is int r && r >= 0 && r < _ctx.Steps.Count && (step.ImageData.Length == 0 || placeholder))
        {
            var original = _ctx.Steps[r];
            if (original.ImageData.Length > 0)
            {
                step.ImageData = original.ImageData;
                step.ImageWidth = original.ImageWidth;
                step.ImageHeight = original.ImageHeight;
                step.ImageScale = original.ImageScale;
                step.ImageOffsetX = original.ImageOffsetX;
                step.ImageOffsetY = original.ImageOffsetY;
            }
            if (original.Type == StepType.CallJob && step.Type == StepType.CallJob && step.Target == original.Target) step.JobRef = original.JobRef;
            // Mã màn hình thật không gửi cho AI → giữ lại nếu AI không đổi số màn hình.
            if (step.MonitorId == null && step.Type == original.Type && step.Monitor == original.Monitor) step.MonitorId = original.MonitorId;
        }
        else if (placeholder)
        {
            step.ImageData = "";
        }
        return step;
    }

    /// <summary>Kiểm tra từng bước — các lỗi này AI sửa được.</summary>
    private void Check(ActionStep s, int n, Result r)
    {
        void Problem(string message) => r.Problems.Add($"Bước {n} ({s.Type}{(s.Type == StepType.Browser ? "/" + s.BrowserAction : "")}): {message}");
        bool Empty(string v) => string.IsNullOrWhiteSpace(v);

        void Selector(string text)
        {
            if (Empty(text)) { Problem("thiếu bộ chọn phần tử UI trong Text."); return; }
            if (text.Contains("{{")) return;
            try { UiElementFinder.Validate(text); }
            catch (FormatException ex) { Problem($"bộ chọn \"{text}\" sai: {ex.Message}"); }
        }

        bool imageCondition = s.Type is StepType.If or StepType.Loop && s.HasCondition && s.Condition == ConditionKind.ImageOnScreen;
        if ((s.IsImageStep || imageCondition) && s.ImageData.Length == 0)
            Problem("cần hình mẫu do người dùng tự chụp nên AI không tạo được — dùng ClickText/WaitForText (chữ trên màn hình) hoặc ClickElement thay thế.");

        switch (s.Type)
        {
            case StepType.LaunchApp or StepType.WaitForWindow or StepType.FocusWindow or StepType.CloseApp or StepType.RunCommand:
                if (Empty(s.Target)) Problem("thiếu Target.");
                break;
            case StepType.TypeText or StepType.LogMessage or StepType.AskAi or StepType.ClickText or StepType.WaitForText:
                if (Empty(s.Text)) Problem("thiếu Text.");
                break;
            case StepType.KeyPress:
                try { InputSimulator.Validate(s.Text); }
                catch (FormatException ex) { Problem($"tổ hợp phím \"{s.Text}\" sai: {ex.Message}"); }
                break;
            case StepType.ClickElement or StepType.SetElementText or StepType.WaitForElement:
                Selector(s.Text);
                break;
            case StepType.SetVariable:
                if (Empty(s.Variable)) Problem("thiếu Variable (tên biến).");
                if (s.VarSource == VarSource.Element) Selector(s.Text);
                if (s.VarSource is VarSource.Command or VarSource.File && Empty(s.Target)) Problem("thiếu Target.");
                if (s.VarSource == VarSource.JsonPath && Empty(s.Arguments)) Problem("thiếu Arguments (đường dẫn JSON).");
                break;
            case StepType.If or StepType.Loop or StepType.Assert when s.HasCondition:
                if (s.Condition == ConditionKind.ElementExists) Selector(s.Text);
                if (s.Condition is ConditionKind.WindowExists or ConditionKind.ProcessRunning or ConditionKind.FileExists && Empty(s.Target)) Problem("thiếu Target.");
                if (s.Condition == ConditionKind.TextOnScreen && Empty(s.Text)) Problem("thiếu Text (chữ cần tìm).");
                if (s.Condition is ConditionKind.BrowserElement or ConditionKind.D365FieldValue or ConditionKind.D365FieldState or ConditionKind.D365RecordCount
                        or ConditionKind.D365SubgridCount or ConditionKind.D365SubgridRow or ConditionKind.D365Command or ConditionKind.D365UserRole && Empty(s.Text))
                    Problem($"điều kiện {s.Condition} thiếu Text.");
                if (s.Condition == ConditionKind.D365FieldState && !ActionStep.D365FieldStates.ContainsKey(s.Arguments.Trim()))
                    Problem($"Arguments phải là một trong: {string.Join(", ", ActionStep.D365FieldStates.Keys)}.");
                if (s.Condition == ConditionKind.D365Command && !ActionStep.D365CommandStates.ContainsKey(s.Arguments.Trim()))
                    Problem($"Arguments phải là một trong: {string.Join(", ", ActionStep.D365CommandStates.Keys)}.");
                if (s.Condition is ConditionKind.D365RecordCount or ConditionKind.D365SubgridCount &&
                    !double.TryParse(s.Arguments.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _) && !s.Arguments.Contains("{{"))
                    Problem("Arguments phải là số để so sánh.");
                break;
            case StepType.Dynamics:
            {
                string? missing = s.D365Action switch
                {
                    D365Action.OpenForm or D365Action.OpenView or D365Action.ViewQuery or D365Action.ViewOpenRecord when Empty(s.Text) => "Text (tên bảng, logical name)",
                    D365Action.SetField or D365Action.GetField when Empty(s.Text) => "Text (tên field, logical name)",
                    D365Action.Command when Empty(s.Text) => "Text (nhãn nút hoặc command id)",
                    D365Action.SelectTab when Empty(s.Text) => "Text (tên tab)",
                    D365Action.RunScript when Empty(s.Text) => "Text (JavaScript)",
                    D365Action.SubgridOpenRow or D365Action.SubgridGetValue or D365Action.SubgridNew or D365Action.SubgridRefresh when Empty(s.Text) => "Text (tên subgrid)",
                    D365Action.WebApi when Empty(s.Arguments) => "Arguments (đường dẫn Web API)",
                    D365Action.QuickCreate when Empty(s.Arguments) => "Arguments (tên bảng)",
                    D365Action.Login when Empty(s.Text) => "Text (email tài khoản)",
                    _ => null
                };
                if (missing != null) Problem("thiếu " + missing + ".");
                if (s.D365Action == D365Action.QuickCreate && !s.Text.Contains('=')) Problem("Text phải gồm các dòng \"field=giá trị\".");
                if (s.D365Action == D365Action.WebApi && !ActionStep.HttpMethods.Contains(s.Method.Trim().ToUpperInvariant()))
                    Problem($"Method \"{s.Method}\" không hợp lệ (GET, POST, PATCH, PUT, DELETE).");
                if (s.D365Action == D365Action.Login && s.Arguments.Trim().Length > 0 && !s.Arguments.Contains("{{secret:"))
                    Problem("mật khẩu (Arguments) phải là {{secret:Tên}}, không ghi mật khẩu thật.");
                break;
            }
            case StepType.Loop when s.LoopKind is LoopKind.Rows or LoopKind.Lines or LoopKind.Files:
                if (Empty(s.Target)) Problem("thiếu Target (file / thư mục / biến danh sách).");
                break;
            case StepType.Label or StepType.Goto:
                if (Empty(s.Target)) Problem("thiếu Target (tên nhãn).");
                break;
            case StepType.Browser:
                if (s.BrowserAction != BrowserAction.Launch && Empty(s.Text))
                    Problem(s.BrowserAction switch
                    {
                        BrowserAction.Navigate => "thiếu Text (URL).",
                        BrowserAction.RunScript => "thiếu Text (JavaScript).",
                        _ => "thiếu Text (bộ chọn CSS / xpath: / text:)."
                    });
                if (s.BrowserAction == BrowserAction.ReadText && Empty(s.Variable)) Problem("thiếu Variable nhận giá trị.");
                break;
            case StepType.CallJob:
            {
                var name = s.Target.Trim();
                var job = _ctx.OtherJobs.FirstOrDefault(j => j.Name.Trim().Equals(name, StringComparison.CurrentCultureIgnoreCase))
                          ?? (name.Length >= 3 ? _ctx.OtherJobs.FirstOrDefault(j => j.Name.Contains(name, StringComparison.CurrentCultureIgnoreCase)) : null);
                if (job == null)
                {
                    Problem($"không có công việc tên \"{name}\". " + (_ctx.OtherJobs.Count == 0
                        ? "Chưa có công việc nào khác — hãy viết các bước trực tiếp thay vì CallJob."
                        : "Các công việc có sẵn: " + string.Join(", ", _ctx.OtherJobs.Select(j => $"\"{j.Name}\""))));
                    break;
                }
                s.JobRef = job.Id;
                s.Target = job.Name;
                break;
            }
            case StepType.HttpRequest:
                if (Empty(s.Target)) Problem("thiếu Target (URL hoặc đường dẫn sau URL gốc của kết nối).");
                if (!ActionStep.HttpMethods.Contains(s.Method.Trim().ToUpperInvariant())) Problem($"Method \"{s.Method}\" không hợp lệ (GET, POST, PATCH, PUT, DELETE).");
                else s.Method = s.Method.Trim().ToUpperInvariant();
                if (!Empty(s.Connection))
                {
                    var conn = _ctx.Connections.FirstOrDefault(c => c.Name.Equals(s.Connection.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (conn.Name == null)
                        Problem($"không có kết nối API \"{s.Connection}\". " + (_ctx.Connections.Count == 0
                            ? "Chưa khai báo kết nối nào — dùng URL đầy đủ, để trống Connection và ghi notes hướng dẫn tạo kết nối."
                            : "Các kết nối có sẵn: " + string.Join(", ", _ctx.Connections.Select(c => $"\"{c.Name}\""))));
                    else s.Connection = conn.Name;
                }
                break;
            case StepType.WriteData when s.IsTextWrite:
                if (Empty(s.Target)) Problem("thiếu Target (đường dẫn file văn bản).");
                break;
            case StepType.PlayMedia:
                if (s.MediaLines.Count == 0) Problem("thiếu Text (danh sách file video, mỗi dòng một file).");
                break;
            case StepType.WriteData:
                if (Empty(s.Target)) Problem("thiếu Target (đường dẫn file .xlsx / .csv).");
                if (Empty(s.Text) || !s.Text.Contains('=')) Problem("Text phải gồm các dòng \"Tên cột=giá trị\".");
                if (s.DataAction == DataAction.UpdateRow && Empty(s.RowRef)) Problem("UpdateRow cần RowRef (vd {{row.rowNumber}} hoặc Cột=giá trị).");
                break;
        }
    }

    /// <summary>Lịch chạy AI trả về (dạng gọn, xem mục "Lịch chạy" trong hướng dẫn) → <see cref="ScheduleConfig"/>.</summary>
    private static void ParseSchedule(JsonObject o, Result r)
    {
        void Problem(string message) => r.Problems.Add("schedule: " + message);
        var typeName = Str(o, "type").Trim();
        if (typeName.Length == 0) return;
        if (!Enum.TryParse<ScheduleType>(typeName, ignoreCase: true, out var type) || !Enum.IsDefined(type) || int.TryParse(typeName, out _))
        {
            Problem($"type \"{typeName}\" không hợp lệ (Once, Daily, Weekly, Monthly, Interval, Manual).");
            return;
        }
        var s = new ScheduleConfig { Type = type };
        r.SkipHolidays = Str(o, "skipHolidays").Equals("true", StringComparison.OrdinalIgnoreCase);
        if (type == ScheduleType.Manual)
        {
            r.Schedule = s;
            return;
        }

        var timeText = Str(o, "time").Trim();
        TimeSpan? time = timeText.Length == 0 ? null : TimeOf(timeText);
        if (timeText.Length > 0 && time == null) Problem($"time \"{timeText}\" phải dạng HH:mm.");
        var dateText = Str(o, "date").Trim();
        DateTime? date = null;
        if (dateText.Length > 0)
        {
            if (DateTime.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) date = d;
            else Problem($"date \"{dateText}\" phải dạng yyyy-MM-dd.");
        }

        var days = new List<DayOfWeek>();
        if (o["days"] is JsonArray dayList)
            foreach (var item in dayList)
            {
                var text = item?.ToString() ?? "";
                if (DayOf(text) is { } day) { if (!days.Contains(day)) days.Add(day); }
                else Problem($"ngày \"{text}\" không hợp lệ (Mon, Tue, Wed, Thu, Fri, Sat, Sun).");
            }

        switch (type)
        {
            case ScheduleType.Once:
                // Không ghi ngày: hôm nay, hoặc ngày mai nếu giờ đó đã qua.
                var at = (date ?? DateTime.Today) + (time ?? new TimeSpan(8, 0, 0));
                if (date == null && at <= DateTime.Now) at = at.AddDays(1);
                if (at <= DateTime.Now) Problem($"thời điểm chạy {at:HH:mm dd/MM/yyyy} đã qua.");
                s.StartAt = at;
                break;
            case ScheduleType.Interval:
                int every = Int(o, "everyMinutes");
                if (every < 1) Problem("Interval cần everyMinutes ≥ 1.");
                s.IntervalMinutes = Math.Max(1, every);
                s.StartAt = (date ?? DateTime.Today) + (time ?? TimeSpan.Zero);
                var between = Str(o, "between").Trim();
                if (between.Length > 0)
                {
                    var parts = between.Split(['-', '–'], 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && TimeOf(parts[0]) is { } from && TimeOf(parts[1]) is { } to && to > from)
                    {
                        s.UseTimeWindow = true;
                        s.WindowStart = from;
                        s.WindowEnd = to;
                        s.Days = days.Count > 0 ? days : [.. Enum.GetValues<DayOfWeek>()];
                    }
                    else Problem($"between \"{between}\" phải dạng HH:mm-HH:mm (giờ đầu < giờ cuối).");
                }
                break;
            default:
                s.StartAt = (date ?? DateTime.Today) + (time ?? new TimeSpan(8, 0, 0));
                if (type == ScheduleType.Weekly)
                {
                    if (days.Count == 0) Problem("Weekly cần days (vd [\"Mon\", \"Fri\"]).");
                    s.Days = days;
                }
                if (type == ScheduleType.Monthly)
                {
                    var mode = Str(o, "monthly").Trim();
                    if (mode.Length > 0 && (!Enum.TryParse<MonthlyMode>(mode, true, out var mm) || !Enum.IsDefined(mm)))
                        Problem($"monthly \"{mode}\" không hợp lệ (DayOfMonth, LastDay, FirstWorkday, LastWorkday, NthWeekday).");
                    else if (mode.Length > 0) s.MonthlyMode = Enum.Parse<MonthlyMode>(mode, true);
                    s.DayOfMonth = Math.Clamp(Int(o, "dayOfMonth", 1), 1, 31);
                    s.WeekOfMonth = Math.Clamp(Int(o, "weekOfMonth", 1), 1, 5);
                    if (Str(o, "weekday").Trim() is { Length: > 0 } wd)
                    {
                        if (DayOf(wd) is { } day) s.MonthWeekday = day;
                        else Problem($"weekday \"{wd}\" không hợp lệ.");
                    }
                }
                break;
        }
        r.Schedule = s;
    }

    private static void ParseTriggers(JsonArray list, Result r)
    {
        foreach (var o in list.OfType<JsonObject>())
        {
            void Problem(string message) => r.Problems.Add($"triggers ({Str(o, "type")}): {message}");
            var typeName = Str(o, "type").Trim();
            if (!Enum.TryParse<TriggerType>(typeName, ignoreCase: true, out var type) || !Enum.IsDefined(type) || int.TryParse(typeName, out _))
            {
                r.Problems.Add($"triggers: type \"{typeName}\" không hợp lệ ({string.Join(", ", Enum.GetNames<TriggerType>())}).");
                continue;
            }
            var t = new JobTrigger { Type = type, Value = Str(o, "value").Trim(), Value2 = Str(o, "value2").Trim(), Minutes = Int(o, "minutes", type == TriggerType.EmailReceived ? 5 : 10) };
            switch (type)
            {
                case TriggerType.Hotkey:
                    try { TriggerManager.ParseHotkey(t.Value); }
                    catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException) { Problem($"phím tắt \"{t.Value}\" không hợp lệ — {ex.Message}"); }
                    break;
                case TriggerType.FileCreated or TriggerType.ProcessStarted or TriggerType.ProcessExited when t.Value.Length == 0:
                    Problem(type == TriggerType.FileCreated ? "thiếu value (thư mục cần theo dõi)." : "thiếu value (tên tiến trình).");
                    break;
            }
            if (t.Minutes < 1) t.Minutes = 1;
            r.Triggers.Add(t);
        }
    }

    private static TimeSpan? TimeOf(string text) =>
        TimeSpan.TryParseExact(text.Trim().Replace('h', ':').TrimEnd(':'), [@"h\:mm", @"hh\:mm", "%h", "hh"], CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1)
            ? t : null;

    private static DayOfWeek? DayOf(string text) => text.Trim().ToLowerInvariant() switch
    {
        "mon" or "monday" or "t2" or "thứ 2" or "thu 2" => DayOfWeek.Monday,
        "tue" or "tuesday" or "t3" or "thứ 3" or "thu 3" => DayOfWeek.Tuesday,
        "wed" or "wednesday" or "t4" or "thứ 4" or "thu 4" => DayOfWeek.Wednesday,
        "thu" or "thursday" or "t5" or "thứ 5" or "thu 5" => DayOfWeek.Thursday,
        "fri" or "friday" or "t6" or "thứ 6" or "thu 6" => DayOfWeek.Friday,
        "sat" or "saturday" or "t7" or "thứ 7" or "thu 7" => DayOfWeek.Saturday,
        "sun" or "sunday" or "cn" or "chủ nhật" or "chu nhat" => DayOfWeek.Sunday,
        _ => null
    };

    private static int Int(JsonObject o, string key, int fallback = 0) =>
        int.TryParse(Str(o, key).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : fallback;

    private static string Str(JsonObject o, string key)
    {
        foreach (var (k, v) in o)
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase))
                return v is JsonValue jv && jv.TryGetValue(out string? s) ? s ?? "" : v?.ToString() ?? "";
        return "";
    }

    private static string Short(string s, int max)
    {
        s = s.Replace("\r", "").Replace("\n", " ");
        return s.Length > max ? s[..max] + "…" : s;
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    // ───────────────────────────── Hướng dẫn cho AI ─────────────────────────────

    private const string ToolSchema = """
        {
          "name": "build_flow",
          "description": "Trả về flow ScheduleApp đã thiết kế. Luôn gọi công cụ này đúng một lần cho mỗi yêu cầu.",
          "input_schema": {
            "type": "object",
            "properties": {
              "name": { "type": "string", "description": "Tên công việc ngắn gọn bằng tiếng Việt (≤ 60 ký tự)." },
              "summary": { "type": "string", "description": "Flow làm gì, 1–3 câu tiếng Việt." },
              "variables": {
                "type": "array",
                "description": "Biến cần khai báo giá trị ban đầu (bộ đếm, danh sách rỗng, đường dẫn/URL người dùng có thể muốn đổi).",
                "items": {
                  "type": "object",
                  "properties": { "name": { "type": "string" }, "value": { "type": "string" } },
                  "required": ["name", "value"]
                }
              },
              "steps": {
                "type": "array",
                "description": "Các bước theo thứ tự chạy; mỗi bước là object có trường Type và các trường như mô tả trong hướng dẫn.",
                "items": { "type": "object", "properties": { "Type": { "type": "string" } }, "required": ["Type"] }
              },
              "notes": {
                "type": "array",
                "description": "Việc người dùng cần kiểm tra / điền trước khi chạy (ngắn gọn, tiếng Việt). Bỏ trống nếu không có.",
                "items": { "type": "string" }
              },
              "schedule": {
                "type": "object",
                "description": "Lịch chạy — chỉ điền khi người dùng nói lúc nào chạy (xem mục Lịch chạy trong hướng dẫn).",
                "properties": { "type": { "type": "string", "enum": ["Once", "Daily", "Weekly", "Monthly", "Interval", "Manual"] } },
                "required": ["type"]
              },
              "triggers": {
                "type": "array",
                "description": "Kích hoạt theo sự kiện — chỉ điền khi người dùng nói (phím tắt, khi có email / file mới…).",
                "items": { "type": "object", "properties": { "type": { "type": "string" } }, "required": ["type"] }
              }
            },
            "required": ["name", "summary", "steps"]
          }
        }
        """;

    internal const string SystemPrompt = """
        Bạn thiết kế flow tự động hóa cho ScheduleApp — ứng dụng Windows chạy tuần tự các bước: mở ứng dụng, gõ phím, click phần tử,
        điều khiển Chrome/Edge, đọc/ghi Excel, gọi API, hỏi AI, gửi thông báo… Người dùng mô tả việc cần làm bằng lời (thường là tiếng Việt);
        bạn trả flow qua công cụ build_flow. Mục tiêu: flow chạy được ngay, ít phải sửa tay nhất.

        # Định dạng bước
        Mỗi bước là một object JSON, chỉ ghi các trường cần dùng (trường bỏ qua lấy giá trị mặc định hợp lý). Tên trường và giá trị enum viết đúng như dưới.
        Trường chung: Type (bắt buộc); Target, Text, Arguments, Variable (chuỗi); X, Y, Count (số nguyên);
        DelayMs = timeout / thời gian chờ (ms); DelayAfterMs = nghỉ sau bước (mặc định 500 với thao tác, 0 với bước dữ liệu/điều khiển);
        Retries, RetryDelayMs = thử lại khi lỗi; OnError: Default|Stop|Continue|GotoLabel (+ ErrorLabel = tên nhãn); Enabled (mặc định true).

        ## Ứng dụng & cửa sổ
        - LaunchApp: Target = exe / đường dẫn / file / URL (vd "notepad.exe", "excel.exe", "%USERPROFILE%\\Documents\\a.xlsx", "https://…"); Arguments = tham số dòng lệnh.
        - WaitForWindow: Target = cửa sổ; DelayMs = chờ tối đa (mặc định 15000).
        - FocusWindow: Target = cửa sổ (hoặc {{biến}} đã nhớ bởi MinimizeWindow để mở lại đúng cửa sổ đó).
        - MinimizeWindow: Target = cửa sổ (trống = cửa sổ người dùng đang dùng); Variable = biến nhớ cửa sổ (vd "ungDungTruoc") để FocusWindow mở lại.
        - CloseApp: Target = tên tiến trình ("notepad"); Force=true để buộc đóng.
        - RunCommand: Target = lệnh cmd; Variable = biến nhận output (tùy chọn); DelayMs = timeout (0 = không chờ).
          BẮT BUỘC: mọi biến chứa dữ liệu (tên file, đường dẫn, nội dung do người/email/web/Excel đưa vào — {{trigger.file}}, {{row.…}}, {{clipboard}}, {{ai.answer}}, biến AskUser…)
          khi đặt vào lệnh phải dùng {{biến:cmd}} (tự bọc dấu nháy an toàn) — KHÔNG tự thêm dấu nháy quanh nó: move {{trigger.file:cmd}} D:\dich. Tránh để dữ liệu chạy thành lệnh.
        Cách ghi Target cửa sổ: một phần tiêu đề (không phân biệt hoa thường) hoặc "exe:tên_tiến_trình" ("exe:EXCEL", "exe:notepad", "exe:msedge") — dùng "exe:" khi tiêu đề thay đổi theo tài liệu đang mở.

        ## Bàn phím & chuột
        - TypeText: Text = chữ cần gõ (có {{biến}}, xuống dòng); Target = cửa sổ đích (được kích hoạt trước khi gõ); TypeMode: Auto|Keys|Paste.
        - KeyPress: Text = tổ hợp phím: "Enter", "Tab*3", "Ctrl+S", "Alt+F4", "Ctrl+Shift+Esc", nhiều tổ hợp cách bằng dấu phẩy "Ctrl+A, Delete".
          Tên phím: Ctrl Alt Shift Win Enter Tab Esc Space Backspace Delete Insert Home End PgUp PgDn Up Down Left Right F1–F24 A–Z 0–9 PrintScreen Apps. Target = cửa sổ đích.
        - MouseClick: X, Y (tương đối theo cửa sổ Target; màn hình nếu Target trống); Button: Left|Right|Middle; DoubleClick. Chỉ dùng khi không còn cách nào khác và ghi notes để người dùng sửa tọa độ.
        - MouseScroll: Count = số nấc (âm = xuống); Target, X, Y tùy chọn.
        - KHÔNG dùng ClickImage, WaitForImage (cần hình mẫu người dùng tự chụp).

        ## Phần tử giao diện ứng dụng Windows (UI Automation — ổn định hơn tọa độ)
        - ClickElement: Target = cửa sổ; Text = bộ chọn; Button, DoubleClick; DelayMs = chờ tối đa (mặc định 10000).
        - SetElementText: Target; Text = bộ chọn ô nhập; Arguments = giá trị cần nhập.
        - WaitForElement: Target; Text = bộ chọn; DelayMs.
        Bộ chọn: "Name=Lưu; ControlType=Button", "AutomationId=txtUser", "Name~=Đăng nhập" (tên chứa), thêm "; Index=2" khi trùng.
        ControlType: Button Edit ComboBox CheckBox RadioButton ListItem MenuItem TabItem Hyperlink Text TreeItem DataItem Document.
        Tên phần tử là chữ hiển thị theo ngôn ngữ giao diện của ứng dụng; khi không chắc dùng "Name~=" với đoạn chữ ngắn và ghi notes để người dùng kiểm tra bằng nút "Bắt phần tử".

        ## Chữ trên màn hình (OCR — khi ứng dụng không hỗ trợ UI Automation)
        - ClickText: Text = chữ cần click; Target = cửa sổ giới hạn vùng tìm; MatchIndex = lần xuất hiện thứ mấy; DelayMs.
        - WaitForText: Text; Target; DelayMs.

        ## Trình duyệt Chrome/Edge (DevTools — cách tốt nhất cho web)
        Type "Browser" và BrowserAction:
        - Launch: Target = "Chrome" hoặc "Edge"; Text = URL (tùy chọn); Arguments = tên hồ sơ (trống = mặc định; mỗi hồ sơ giữ đăng nhập riêng); Force=true = chạy ẩn (headless).
          Phải chạy trước các bước Browser / Dynamics khác (trừ khi flow gọi CallJob tới công việc đã mở trình duyệt).
        - Navigate: Text = URL.
        - Click: Text = bộ chọn.
        - SetValue: Text = bộ chọn ô nhập / select; Arguments = giá trị (kích hoạt sự kiện input/change — chạy với React/Angular/Dynamics 365).
        - ReadText: Text = bộ chọn; Variable = biến nhận chữ / giá trị.
        - WaitFor: Text = bộ chọn; DelayMs.
        - RunScript: Text = JavaScript (biểu thức, hoặc IIFE có return); Variable (tùy chọn) nhận kết quả.
        Target (trừ Launch) = tab: một phần URL/tiêu đề; trống = tab web đầu tiên. Click/SetValue/ReadText tự chờ phần tử tới DelayMs (mặc định 15000).
        Bộ chọn: CSS ("#email", "input[name=q]", "button[type=submit]"), "xpath://button[.='Lưu']", hoặc "text:Đăng nhập" (phần tử hiển thị đúng chữ đó).
        Không bịa id/class của trang mà bạn không chắc — ưu tiên "text:…", thuộc tính name/type/placeholder/aria-label ("input[placeholder*='Tìm']", "[aria-label='Search']"), và ghi notes các bộ chọn cần kiểm tra.
        Đăng nhập Microsoft (Dynamics 365, Office 365): ô email "input[type=email]", mật khẩu "input[type=password]", nút Next/Sign in "#idSIButton9".

        ## Dynamics 365 model-driven (Client API Xrm chạy trong tab trình duyệt điều khiển — dùng cho nhập liệu và kiểm thử D365)
        Với Dynamics 365 / Power Apps model-driven LUÔN dùng bước Dynamics thay vì Browser Click/SetValue (không vỡ khi giao diện đổi). Cần trình duyệt điều khiển
        đã mở app (Browser Launch với URL "https://<org>.crm5.dynamics.com/main.aspx?appid=…", hoặc CallJob công việc mở D365 có sẵn).
        Type "Dynamics" và D365Action; Target = tab (trống = tab D365 đầu tiên); DelayMs = chờ tối đa (mặc định 30000):
        - OpenForm: Text = bảng (logical name: account, contact, lead, opportunity, incident…); Arguments = Id bản ghi (trống = form tạo mới); Form = tên/Id form chính (tùy chọn). Tự chờ form tải.
        - OpenView: Text = bảng; Arguments = Id view (tùy chọn). WaitForm: chờ form tải xong (sau thao tác chuyển trang).
        - SetField: Text = field (logical name); Arguments = giá trị — lookup: tên bản ghi hoặc "bảng:guid" (vd "account:{{accountId}}"); option set: nhãn hoặc số;
          nhiều lựa chọn "A; B"; ngày "dd/MM/yyyy [HH:mm]"; Có/Không "true"/"false"; số "1234567.5"; trống = xóa giá trị. Force=true để ghi cả field đang khóa/ẩn.
        - GetField: Text = field (thêm ":raw" để lấy Id / số / ISO); Variable.
        - Save: lưu (lỗi validate / field bắt buộc / plugin → bước lỗi kèm nội dung); Id vào Variable và {{d365.lastId}}; bản ghi mới thêm vào {{d365.created}}.
        - Command: Text = nhãn nút trên thanh lệnh ("Lưu & đóng", "Deactivate") hoặc command id ("Mscrm.Form.account.Deactivate").
        - SelectTab: Text = tên/nhãn tab. BpfNext / BpfPrevious: chuyển giai đoạn quy trình (BPF).
        - ConfirmDialog: Text = nhãn nút trên hộp thoại (trống = nút chính); Variable = nội dung hộp thoại.
        - GetRecordId: Variable. GetNotifications: Variable = thông báo / lỗi đang hiện trên form.
        - WebApi: Method; Arguments = đường dẫn ("accounts?$select=name&$top=5", "contacts(guid)"); Text = body JSON; Variable (POST → Id mới, còn lại → body);
          có {{http.status}}, {{http.body}}; dùng phiên đăng nhập của trình duyệt (không cần Connection); Force=true để không lỗi khi mã ≥ 400. Hợp để chuẩn bị / kiểm tra dữ liệu test.
        - Cleanup: xóa mọi bản ghi trong {{d365.created}} (đặt cuối kịch bản kiểm thử).
        - RunScript: Text = JavaScript, có sẵn formContext, Xrm, dùng được await; giá trị return vào Variable.
        - SubgridOpenRow / SubgridGetValue: Text = tên subgrid (vd "Contacts"); RowRef = số thứ tự dòng hoặc chữ có trong dòng; GetValue: Arguments = cột (logical name, trống = cột tên), Variable.
        - SubgridNew (Text = subgrid; như bấm "+ Mới", field cha điền sẵn) · SubgridRefresh (Text = subgrid).
        - ViewQuery / ViewOpenRecord: Text = bảng; Arguments = tên hoặc Id view (trống = view mặc định); RowRef = lọc theo tên chứa chữ; kết quả {{view.count}}, {{view.ids}}, {{view.names}}
          (ViewQuery: Variable = số bản ghi; ViewOpenRecord mở bản ghi đầu tiên).
        - QuickCreate: Arguments = bảng; Text = mỗi dòng "field=giá trị"; Variable = Id mới.
        - Login: Text = email tài khoản test; Arguments = "{{secret:MatKhauTest}}"; RowRef = "{{secret:TotpTest}}" (khóa TOTP Base32 khi có MFA). Đã đăng nhập thì tự bỏ qua.
        - GetUser: {{d365.user}}, {{d365.userId}}, {{d365.roles}} (mỗi dòng một vai trò); Variable = tên người dùng.
        Tên logic chuẩn: name, accountnumber, telephone1, emailaddress1, firstname, lastname, fullname, parentcustomerid, primarycontactid, ownerid, statecode, statuscode,
        estimatedvalue, revenue, description… Field / bảng tùy biến (tiền tố publisher như new_, cr123_) không đoán được: dùng tên hợp lý và ghi notes để người dùng sửa bằng nút "Chọn field từ form…".
        Kịch bản kiểm thử D365 điển hình: (CallJob mở D365) → OpenForm (tạo mới) → SetField… với giá trị duy nhất "Test {{now:HHmmss}}" → Save → Assert → … → Cleanup.

        ## Kiểm tra (Assert) — kịch bản kiểm thử
        Type "Assert": kiểm tra một điều kiện, ghi ĐẠT / KHÔNG ĐẠT kèm giá trị thực tế vào báo cáo; mặc định sai vẫn chạy tiếp để thấy hết lỗi (OnError "Stop" khi các bước sau phụ thuộc vào nó).
        Message = mô tả hiện trong báo cáo (vd "Khách hàng mới có số điện thoại"). Dùng mọi Condition của If (Compare, FileExists, ElementExists…) và thêm (cũng dùng được trong If / Loop While):
        - BrowserElement: Text = bộ chọn CSS / "xpath:" / "text:"; Target = tab.
        - D365FieldValue: Text = field; CompareOp; Arguments = giá trị mong đợi (lookup so theo tên, option set theo nhãn).
        - D365FieldState: Text = field; Arguments = required | recommended | disabled | visible | dirty | empty.
        - D365Notification: Text = chữ có trong thông báo (trống = có bất kỳ thông báo / lỗi nào); Negate=true = form không báo lỗi.
        - D365RecordCount: Text = truy vấn Web API ("contacts?$filter=emailaddress1 eq '{{email}}'"); CompareOp; Arguments = số.
        - D365SubgridCount: Text = subgrid; CompareOp; Arguments = số. D365SubgridRow: Text = subgrid; Arguments = chữ có trong một dòng.
        - D365Command: Text = command id hoặc nhãn nút; Arguments = visible | enabled | disabled (Negate=true = nút bị ẩn, vd thiếu quyền).
        - D365CurrentForm: CompareOp; Arguments = tên form chính. D365UserRole: Text = tên vai trò bảo mật.
        DelayMs > 0 = kiểm tra lại tới khi đạt (plugin bất đồng bộ, Power Automate chạy chậm).

        ## Biến & dữ liệu
        - SetVariable: Variable = tên biến; VarSource và các trường:
          Value (Text = giá trị, có {{biến}}) · Calc (Text = phép tính "{{dem}} + 1", "{{gia}} * 1.1") · Clipboard ·
          Command (Target = lệnh cmd, Arguments = regex trích, nhóm 1) · ScreenText (Target = cửa sổ, trống = cả màn hình; Arguments = regex trích) ·
          Element (Target = cửa sổ, Text = bộ chọn UI Automation) · AskUser (Text = câu hỏi, Target = tiêu đề hộp thoại, Arguments = giá trị mặc định, Force=true nếu là mật khẩu) ·
          File (Target = đường dẫn file văn bản) · ListAdd (Text = phần tử thêm vào cuối danh sách; danh sách = mỗi phần tử một dòng) ·
          Split (Text = chuỗi, Arguments = dấu phân cách, trống = dấu phẩy, "\\n" = xuống dòng) · JsonPath (Text = JSON vd "{{http.body}}", Arguments = "value[0].name" | "items[*].id" | "data.length").
        - LogMessage: Text = nội dung ghi nhật ký.
        - WriteData: ghi .xlsx / .csv không cần mở Excel (giữ định dạng, công thức). Target = đường dẫn file (chưa có thì tự tạo); Arguments = sheet (trống = sheet đầu);
          DataAction: AppendRow|UpdateRow; RowRef (UpdateRow) = số dòng ("{{row.rowNumber}}") hoặc "Cột=giá trị" để tìm theo cột khóa;
          Text = mỗi dòng "Tên cột=giá trị" (cột chưa có tự thêm). Sau bước có {{lastRow}}.
          Ghi file văn bản tự do (báo cáo, nhật ký, "đã chạy xong"…): DataAction WriteText (thay nội dung cũ) | AppendText (thêm vào cuối);
          Target = file .txt (tự tạo cả thư mục); Text = nội dung nhiều dòng, có {{biến}}. Mở để xem: LaunchApp "notepad.exe" Arguments = "\"<đường dẫn>\"".
        - PlayMedia: phát lần lượt video / nhạc bằng trình phát có sẵn của ScheduleApp, hết file này tự sang file kế, phát xong cả danh sách mới sang bước sau
          (không cần LaunchApp / chờ / đóng trình phát). Text = mỗi dòng một file (dòng là thư mục → mọi video trong đó theo tên; dòng "#" là ghi chú);
          Force = toàn màn hình (mặc định true); Arguments = âm lượng 0–100; Monitor = màn hình phát khi có nhiều màn hình (0 = chính, -1 = màn hình đang có chuột, 2 = màn hình số 2…). Sau bước có {{media.played}} (số file đã phát), {{media.duration}} (tổng thời lượng, vd 0:26), {{media.seconds}}. Dùng cho "mở video 1 rồi video 2…".
        - HttpRequest: Method GET|POST|PATCH|PUT|DELETE; Connection = tên kết nối đã khai báo (khi đó Target là phần sau URL gốc, vd "accounts?$top=5&$select=name"), không có kết nối thì Target = URL đầy đủ;
          Headers = mỗi dòng "Tên: giá trị"; Text = body JSON (đặt chuỗi vào JSON bằng "{{biến:json}}"); Arguments = đường dẫn JSON trích kết quả; Variable = biến nhận.
          Luôn có {{http.status}}, {{http.body}}. Force=true để không báo lỗi khi mã ≥ 400.
        - AskAi: Text = yêu cầu cho Claude (kết quả vào Variable và {{ai.answer}}; trả về đúng giá trị được hỏi, có thể yêu cầu JSON rồi dùng SetVariable JsonPath);
          Force=true gửi kèm ảnh chụp cửa sổ Target / cả màn hình. Dùng để trích, phân loại, tóm tắt dữ liệu không có cấu trúc cố định.
        - Notify: Target = tiêu đề (tùy chọn); Text = nội dung; gửi qua Telegram/email/webhook đã bật; Force=true đính kèm ảnh màn hình.
        - Reminder: Target = tiêu đề; Text = nội dung; WaitForUser=true để flow chờ người dùng bấm "Đã hiểu" (dùng cho việc người phải tự làm: OTP, captcha, kiểm tra trước khi gửi).
        - Wait: DelayMs = thời gian chờ.

        ## Điều khiển luồng (khối phải đóng đúng, lồng nhau được)
        - If … [Else] … EndIf. If dùng Condition:
          Compare: Target = vế trái ("{{tong}}"), CompareOp: Equals|NotEquals|Contains|NotContains|StartsWith|Greater|GreaterOrEqual|Less|LessOrEqual|IsEmpty|IsNotEmpty|Regex, Arguments = vế phải (so số nếu cả hai là số);
          WindowExists (Target) · ProcessRunning (Target = tên tiến trình) · FileExists (Target = đường dẫn) · TextOnScreen (Text, Target) · ElementExists (Target, Text = bộ chọn) ·
          LastStepFailed (bước trước lỗi; bước trước phải có OnError "Continue").
          Negate=true để đảo; DelayMs > 0 = chờ tối đa tới khi điều kiện đúng.
        - Loop … EndLoop. LoopKind:
          Count (Count = số lần) · While (điều kiện như If: Condition, Target, CompareOp, Arguments) ·
          Rows (Target = file Excel/CSV, Arguments = sheet, Variable = tiền tố, mặc định "row" → {{row.Tên cột}}, {{row.rowNumber}} = số dòng thật trong file) ·
          Lines (Target = đường dẫn file hoặc "{{biến danh sách}}", Variable = tên biến từng dòng, mặc định "item") ·
          Files (Target = thư mục, Arguments = "*.pdf;*.xlsx", Variable mặc định "item" → {{item}} đường dẫn đầy đủ, {{item.name}}, {{item.base}}, {{item.ext}}, {{item.dir}}).
          Trong vòng lặp có {{loop.index}} (từ 1), {{loop.count}}.
        - BreakLoop (thoát vòng lặp), ContinueLoop (sang lần lặp kế) — chỉ dùng trong Loop.
        - Label (Target = tên nhãn), Goto (Target = tên nhãn).
        - StopFlow: Text = lý do; Force=true tính là lỗi.
        - CallJob: Target = đúng tên công việc khác (xem bối cảnh) — chạy nó như một bước, dùng chung biến. Nên dùng lại công việc có sẵn (vd đăng nhập) thay vì viết lại.

        # Biến
        {{tên}} được thay khi chạy, dùng trong mọi ô chữ. Có sẵn: {{today}} {{yesterday}} {{tomorrow}} (dd/MM/yyyy), {{now}}, {{time}}, {{today-1}}, {{today+7}},
        {{today-1M:MM/yyyy}} (đơn vị d w h m M y), {{now:HH:mm}} (định dạng ngày giờ .NET), {{clipboard}}, {{env:USERNAME}}, {{secret:Tên}} (bí mật mã hóa),
        {{random:1-100}}, {{guid}}, {{newline}}, {{lastOutput}}, {{lastError}}, {{job.name}}.
        Định dạng: {{x:upper}} lower trim unquote (bỏ dấu nháy bao quanh) len url json cmd (tham số lệnh an toàn cho RunCommand) nodiacritics · số {{x:N0}} {{x:N2}} · ngày {{x:dd/MM/yyyy}} · danh sách {{ds:count}} first last sort unique {{ds:item(2)}} {{ds:join(, )}}.
        Biến chưa có giá trị mà bị dùng sẽ báo lỗi — khai báo trong "variables" (bộ đếm "0", danh sách rỗng "").
        Dữ liệu test ngẫu nhiên, sinh mới ở mỗi lần chạy: giá trị biến trong "variables" viết dạng công thức — "=hoten()" (hoặc "=hoten(nữ)"), "=ho()", "=ten()",
        "=email()" / "=email(cty.vn)", "=sdt()", "=diachi()", "=thanhpho()", "=congty()", "=random(1, 100)" / "=random(1, 100, 2)", "=chuso(6)", "=chuoi(8)",
        "=chon(Mới; Đang xử lý)", "=ngay(-30, 0)" / "=ngay(1, 90, yyyy-MM-dd)", "=cccd()", "=guid()"; hoặc thẳng trong ô chữ "{{=sdt()}}" (mỗi lần thay ra giá trị khác).
        Kịch bản kiểm thử nên khai báo dữ liệu test bằng biến dạng này để cùng một giá trị dùng cho cả bước nhập và bước Kiểm tra.

        # Lịch chạy & kích hoạt (schedule, triggers)
        Chỉ điền khi người dùng nói lúc nào / khi nào chạy; không nói thì bỏ trống cả hai (chạy thủ công hoặc bằng lệnh). Ngày giờ tính theo "Bây giờ" trong bối cảnh.
        - schedule: {"type": "Once|Daily|Weekly|Monthly|Interval|Manual", "time": "HH:mm", "date": "yyyy-MM-dd", "days": ["Mon",…,"Sun"],
          "everyMinutes": 30, "between": "08:00-17:30", "monthly": "DayOfMonth|LastDay|FirstWorkday|LastWorkday|NthWeekday",
          "dayOfMonth": 15, "weekOfMonth": 1–5 (5 = tuần cuối), "weekday": "Mon", "skipHolidays": true (bỏ qua ngày nghỉ lễ)}.
          Ví dụ: "8h sáng mỗi ngày" → {"type":"Daily","time":"08:00"} · "các ngày làm việc lúc 7h45" → {"type":"Weekly","days":["Mon","Tue","Wed","Thu","Fri"],"time":"07:45"}
          · "thứ 2 và thứ 6 lúc 17h" → {"type":"Weekly","days":["Mon","Fri"],"time":"17:00"} · "mỗi 15 phút trong giờ hành chính" →
          {"type":"Interval","everyMinutes":15,"between":"08:00-17:30","days":["Mon","Tue","Wed","Thu","Fri"]} · "ngày cuối tháng 18h" →
          {"type":"Monthly","monthly":"LastDay","time":"18:00"} · "ngày 5 hằng tháng" → {"type":"Monthly","dayOfMonth":5,"time":"08:00"}
          · "3 giờ chiều nay" → {"type":"Once","date":"<ngày hôm nay>","time":"15:00"}.
        - triggers: [{"type": …, "value": …, "value2": …, "minutes": …}] — Hotkey (value "Ctrl+Alt+1"); FileCreated (value = thư mục, value2 = bộ lọc "*.pdf";
          flow có {{trigger.file}} {{trigger.name}} {{trigger.base}} {{trigger.dir}}); ProcessStarted / ProcessExited (value = tên tiến trình, vd "EXCEL");
          Idle (minutes = số phút máy không dùng); SessionUnlock; AppStartup; EmailReceived (value = tiêu đề chứa, value2 = người gửi chứa, minutes = chu kỳ kiểm tra;
          flow có {{email.subject}} {{email.from}} {{email.body}} {{email.attachments}} {{email.attachmentDir}}).

        # Nguyên tắc
        1. Độ ổn định: API / ghi file trực tiếp > Dynamics (với D365) > Browser > ClickElement/SetElementText > phím tắt (KeyPress) > ClickText (OCR) > MouseClick.
           Yêu cầu "kiểm thử / test / kiểm tra rằng…" → kết thúc bằng các bước Assert có Message rõ ràng.
        2. Sau LaunchApp có WaitForWindow (web: Browser WaitFor) trước khi thao tác; đặt Target cho TypeText/KeyPress để gõ đúng cửa sổ; thao tác dễ chập chờn đặt Retries 1–2.
        3. Không bịa đường dẫn, URL, tài khoản cụ thể mà người dùng không cho: đặt thành biến trong "variables" với giá trị hợp lý (vd "%USERPROFILE%\\Documents\\bao-cao.xlsx") và ghi notes để người dùng sửa.
        4. Mật khẩu / token: luôn {{secret:Tên}} + notes "Thêm bí mật Tên trong 🔑 Bí mật". Không bao giờ ghi mật khẩu thật vào flow.
        5. OTP, captcha, đăng nhập lần đầu, xác nhận quan trọng: Reminder với WaitForUser=true thay vì cố tự động.
        6. Chữ hiển thị cho người dùng (nhắc nhở, nhật ký, thông báo, tên công việc) viết tiếng Việt. Thêm vài LogMessage ở các mốc chính để dễ theo dõi.
        7. notes: chỉ những gì người dùng phải kiểm tra / điền / cấu hình trước khi chạy; ngắn gọn, không lặp lại summary.
        8. Yêu cầu mơ hồ: chọn cách hợp lý nhất, ghi giả định vào notes — không hỏi lại.
        """;
}
