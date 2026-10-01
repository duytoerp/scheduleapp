using System.Diagnostics;
using System.Reflection;
using System.Windows.Automation;
using ScheduleApp.Models;
using ScheduleApp.Native;
using ScheduleApp.Services;

namespace ScheduleApp.Automation;

/// <summary>
/// Tìm và thao tác phần tử giao diện (nút, ô nhập…) qua Windows UI Automation — ổn định hơn tọa độ / hình ảnh,
/// không phụ thuộc DPI, zoom hay vị trí cửa sổ.
/// Bộ chọn: "AutomationId=txtUser; ControlType=Edit", "Name=Lưu; ControlType=Button; Index=2", "Name~=Đăng nhập".
/// </summary>
internal static class UiElementFinder
{
    private const int PollMs = 300;

    private static readonly Lazy<Dictionary<string, ControlType>> ControlTypes = new(() =>
        typeof(ControlType).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(ControlType))
            .ToDictionary(f => f.Name, f => (ControlType)f.GetValue(null)!, StringComparer.OrdinalIgnoreCase));

    private sealed record Selector(
        string? Name, bool NameContains, string? AutomationId, ControlType? ControlType, string? ClassName, int Index);

    private static Selector Parse(string text)
    {
        string? name = null, id = null, cls = null;
        bool contains = false;
        ControlType? type = null;
        int index = 1;

        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
            {
                // Không có "khóa=" → coi là tên phần tử.
                name = part;
                continue;
            }
            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            bool like = key.EndsWith('~');
            if (like) key = key[..^1].Trim();

            switch (key.ToLowerInvariant())
            {
                case "name":
                    name = value;
                    contains = like;
                    break;
                case "automationid" or "id":
                    id = value;
                    break;
                case "controltype" or "type":
                    var typeName = value.StartsWith("ControlType.", StringComparison.OrdinalIgnoreCase) ? value[12..] : value;
                    type = ControlTypes.Value.TryGetValue(typeName, out var ct)
                        ? ct
                        : throw new FormatException($"Không nhận ra ControlType \"{value}\" (vd Button, Edit, ComboBox, CheckBox, ListItem, MenuItem, Hyperlink).");
                    break;
                case "classname" or "class":
                    cls = value;
                    break;
                case "index":
                    index = int.TryParse(value, out var n) && n > 0 ? n : throw new FormatException("Index phải là số ≥ 1.");
                    break;
                default:
                    throw new FormatException($"Không nhận ra thuộc tính \"{key}\" — dùng Name, AutomationId, ControlType, ClassName, Index.");
            }
        }
        if (name == null && id == null && type == null && cls == null)
            throw new FormatException("Bộ chọn phần tử trống — vd: Name=Lưu; ControlType=Button.");
        return new Selector(name, contains, id, type, cls, index);
    }

    /// <summary>Kiểm tra cú pháp bộ chọn, ném FormatException nếu sai.</summary>
    public static void Validate(string selector) => Parse(selector);

    /// <summary>Tìm phần tử trong cửa sổ (một lần).</summary>
    public static AutomationElement? Find(IntPtr window, string selectorText)
    {
        var sel = Parse(selectorText);
        var root = AutomationElement.FromHandle(window);

        var conditions = new List<Condition>();
        if (sel.Name != null && !sel.NameContains)
            conditions.Add(new PropertyCondition(AutomationElement.NameProperty, sel.Name, PropertyConditionFlags.IgnoreCase));
        if (sel.AutomationId != null)
            conditions.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, sel.AutomationId));
        if (sel.ControlType != null)
            conditions.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, sel.ControlType));
        if (sel.ClassName != null)
            conditions.Add(new PropertyCondition(AutomationElement.ClassNameProperty, sel.ClassName, PropertyConditionFlags.IgnoreCase));

        Condition condition = conditions.Count switch
        {
            0 => Condition.TrueCondition,
            1 => conditions[0],
            _ => new AndCondition([.. conditions])
        };

        // Chỉ cần phần tử đầu tiên → FindFirst nhanh hơn nhiều.
        if (sel.Index == 1 && !sel.NameContains) return root.FindFirst(TreeScope.Descendants, condition);

        int seen = 0;
        foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, condition))
        {
            if (sel.NameContains && !SafeName(e).Contains(sel.Name!, StringComparison.OrdinalIgnoreCase)) continue;
            if (++seen == sel.Index) return e;
        }
        return null;
    }

    /// <summary>
    /// Tìm trong các popup đang mở của cùng ứng dụng (menu, danh sách gợi ý, ô thả xuống — cây nhỏ, tìm nhanh) rồi mới tới cửa sổ chính.
    /// </summary>
    internal static AutomationElement? FindWithPopups(IntPtr window, string selector)
    {
        foreach (var popup in WindowHelper.ProcessPopups(window))
        {
            try
            {
                var e = Find(popup, selector);
                if (e != null) return e;
            }
            catch (ElementNotAvailableException) { /* popup vừa đóng */ }
        }
        return Find(window, selector);
    }

    /// <summary>Chờ tới khi phần tử xuất hiện trong cửa sổ (trống = cửa sổ đang được chọn).</summary>
    public static async Task<AutomationElement> WaitAsync(string windowQuery, string selector, int timeoutMs, CancellationToken ct)
    {
        Parse(selector); // báo lỗi cú pháp ngay
        var sw = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var window = string.IsNullOrWhiteSpace(windowQuery) ? Win32.GetForegroundWindow() : WindowHelper.Find(windowQuery);
            if (window != IntPtr.Zero)
            {
                try
                {
                    var found = await Task.Run(() => FindWithPopups(window, selector), ct);
                    if (found != null) return found;
                }
                catch (ElementNotAvailableException) { /* cửa sổ đang đóng/đổi — thử lại */ }
            }
            if (sw.ElapsedMilliseconds >= timeoutMs)
                throw new TimeoutException(window == IntPtr.Zero
                    ? $"Không tìm thấy cửa sổ \"{windowQuery}\"."
                    : $"Không tìm thấy phần tử [{selector}] sau {ActionStep.FormatMs(timeoutMs)}.");
            await Task.Delay(PollMs, ct);
        }
    }

    public static async Task ClickAsync(AutomationElement e, MouseButtonKind button, bool doubleClick, CancellationToken ct)
    {
        // Click trái đơn: ưu tiên InvokePattern (không cần thấy phần tử, không di chuột).
        if (button == MouseButtonKind.Left && !doubleClick)
        {
            if (e.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
            {
                // Invoke có thể bị chặn tới khi hộp thoại modal mà nút mở ra được đóng — không chờ quá 3 giây.
                var invoke = Task.Run(() => ((InvokePattern)p).Invoke());
                await Task.WhenAny(invoke, Task.Delay(3000, ct));
                if (invoke.IsFaulted) throw invoke.Exception!.InnerException!;
                return;
            }
            if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var t))
            {
                ((TogglePattern)t).Toggle();
                return;
            }
            if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var s))
            {
                ((SelectionItemPattern)s).Select();
                return;
            }
        }
        var point = ClickPoint(e);
        InputSimulator.Click(point.X, point.Y, button, doubleClick);
    }

    public static async Task SetTextAsync(AutomationElement e, string text, CancellationToken ct)
    {
        if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var p) && !((ValuePattern)p).Current.IsReadOnly)
        {
            try
            {
                e.SetFocus();
            }
            catch (InvalidOperationException) { /* một số phần tử không nhận focus — vẫn đặt giá trị được */ }
            ((ValuePattern)p).SetValue(text);
            return;
        }

        // Không hỗ trợ ValuePattern: click vào phần tử, chọn hết rồi dán.
        var point = ClickPoint(e);
        InputSimulator.Click(point.X, point.Y, MouseButtonKind.Left, false);
        await Task.Delay(100, ct);
        InputSimulator.SendKeys("Ctrl+A", ct);
        var previous = ClipboardHelper.TryGetText();
        ClipboardHelper.SetText(text);
        InputSimulator.SendKeys("Ctrl+V", ct);
        await Task.Delay(300, ct);
        if (previous != null) ClipboardHelper.SetText(previous);
    }

    /// <summary>Giá trị / chữ của phần tử: Value → nội dung văn bản → Name.</summary>
    public static string GetValue(AutomationElement e)
    {
        if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var v)) return ((ValuePattern)v).Current.Value ?? "";
        if (e.TryGetCurrentPattern(TextPattern.Pattern, out var t)) return ((TextPattern)t).DocumentRange.GetText(-1) ?? "";
        if (e.TryGetCurrentPattern(TogglePattern.Pattern, out var g))
            return ((TogglePattern)g).Current.ToggleState == ToggleState.On ? "TRUE" : "FALSE";
        if (e.TryGetCurrentPattern(SelectionPattern.Pattern, out var s))
            return string.Join(", ", ((SelectionPattern)s).Current.GetSelection().Select(SafeName));
        return SafeName(e);
    }

    public static Rectangle Bounds(AutomationElement e)
    {
        var r = e.Current.BoundingRectangle;
        return r.IsEmpty ? Rectangle.Empty : new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
    }

    private static Point ClickPoint(AutomationElement e)
    {
        if (e.TryGetClickablePoint(out var p)) return new Point((int)p.X, (int)p.Y);
        var b = Bounds(e);
        if (b.IsEmpty) throw new InvalidOperationException("Phần tử không hiển thị trên màn hình nên không click được.");
        return new Point(b.X + b.Width / 2, b.Y + b.Height / 2);
    }

    private static string SafeName(AutomationElement e)
    {
        try { return e.Current.Name ?? ""; }
        catch (ElementNotAvailableException) { return ""; }
    }

    // ───────────────────────────── Bắt phần tử (trình soạn) ─────────────────────────────

    public sealed record CapturedElement(IntPtr Window, string Selector, string Description, Rectangle Bounds);

    /// <summary>Phần tử dưới điểm <paramref name="p"/> và bộ chọn đủ để tìm lại nó trong cửa sổ chứa.</summary>
    public static CapturedElement? Capture(Point p)
    {
        var window = WindowHelper.RootWindowAt(p);
        if (window == IntPtr.Zero) return null;
        var element = AutomationElement.FromPoint(new System.Windows.Point(p.X, p.Y));
        if (element == null) return null;

        var info = element.Current;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.AutomationId) && !int.TryParse(info.AutomationId, out _))
            parts.Add($"AutomationId={info.AutomationId}");
        else if (!string.IsNullOrWhiteSpace(info.Name))
            parts.Add($"Name={info.Name.Replace(";", " ").Trim()}");
        else if (!string.IsNullOrWhiteSpace(info.AutomationId))
            parts.Add($"AutomationId={info.AutomationId}");
        var typeName = info.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "";
        if (typeName.Length > 0) parts.Add($"ControlType={typeName}");
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(info.ClassName)) parts.Add($"ClassName={info.ClassName}");
        if (parts.Count == 0) return null;

        var selector = string.Join("; ", parts);

        // Bộ chọn khớp nhiều phần tử → thêm Index để trỏ đúng phần tử này.
        try
        {
            var matches = new List<AutomationElement>();
            var sel = Parse(selector);
            var root = AutomationElement.FromHandle(window);
            var conds = new List<Condition>();
            if (sel.Name != null) conds.Add(new PropertyCondition(AutomationElement.NameProperty, sel.Name, PropertyConditionFlags.IgnoreCase));
            if (sel.AutomationId != null) conds.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, sel.AutomationId));
            if (sel.ControlType != null) conds.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, sel.ControlType));
            if (sel.ClassName != null) conds.Add(new PropertyCondition(AutomationElement.ClassNameProperty, sel.ClassName));
            Condition c = conds.Count == 1 ? conds[0] : new AndCondition([.. conds]);
            int i = 0, position = 0;
            foreach (AutomationElement m in root.FindAll(TreeScope.Descendants, c))
            {
                i++;
                if (System.Windows.Automation.Automation.Compare(m, element)) position = i;
            }
            if (i > 1 && position > 0) selector += $"; Index={position}";
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException)
        {
            // Bỏ qua — vẫn dùng bộ chọn chưa có Index.
        }

        var desc = $"{typeName} \"{info.Name}\"" + (string.IsNullOrEmpty(info.AutomationId) ? "" : $" (id: {info.AutomationId})");
        return new CapturedElement(window, selector, desc, Bounds(element));
    }

    public static void LogFound(AutomationElement e)
    {
        try
        {
            var c = e.Current;
            Log.Info($"      Phần tử: {c.ControlType?.ProgrammaticName?.Replace("ControlType.", "")} \"{c.Name}\"");
        }
        catch (ElementNotAvailableException) { }
    }
}
