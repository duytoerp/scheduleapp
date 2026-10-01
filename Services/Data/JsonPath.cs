using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ScheduleApp.Services.Data;

/// <summary>
/// Trích giá trị từ JSON bằng đường dẫn đơn giản:
/// <c>value[0].name</c> · <c>$.data.items[*].id</c> (mọi phần tử, mỗi giá trị một dòng) ·
/// <c>value[0]["@odata.etag"]</c> (khóa có dấu chấm) · <c>value.length</c> (số phần tử).
/// Kết quả là chữ/số thì trả về nguyên giá trị; đối tượng/mảng trả về chuỗi JSON.
/// </summary>
public static class JsonPath
{
    public static string Select(string json, string path)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex) { throw new InvalidOperationException($"Nội dung không phải JSON hợp lệ: {ex.Message} — \"{Short(json)}\""); }

        using (doc)
        {
            var segments = Parse(path);
            var current = new List<JsonElement> { doc.RootElement };
            bool multi = false;
            foreach (var seg in segments)
            {
                var next = new List<JsonElement>();
                foreach (var e in current)
                {
                    switch (seg)
                    {
                        case { Wildcard: true }:
                            multi = true;
                            if (e.ValueKind == JsonValueKind.Array) next.AddRange(e.EnumerateArray());
                            else if (e.ValueKind == JsonValueKind.Object) next.AddRange(e.EnumerateObject().Select(p => p.Value));
                            break;
                        case { Index: int i }:
                            if (e.ValueKind == JsonValueKind.Array)
                            {
                                int len = e.GetArrayLength();
                                int idx = i < 0 ? len + i : i;
                                if (idx >= 0 && idx < len) next.Add(e[idx]);
                                else if (!multi) throw new InvalidOperationException($"Phần tử [{i}] không có — mảng chỉ có {len} phần tử.");
                            }
                            else if (!multi) throw new InvalidOperationException($"[{i}]: giá trị không phải mảng.");
                            break;
                        case { Name: "length" } when e.ValueKind == JsonValueKind.Array:
                            next.Add(JsonDocument.Parse(e.GetArrayLength().ToString(CultureInfo.InvariantCulture)).RootElement);
                            break;
                        default:
                            if (e.ValueKind == JsonValueKind.Object && TryGet(e, seg.Name!, out var child)) next.Add(child);
                            else if (!multi)
                                throw new InvalidOperationException(e.ValueKind == JsonValueKind.Object
                                    ? $"Không có khóa \"{seg.Name}\". Các khóa: {string.Join(", ", e.EnumerateObject().Take(15).Select(p => p.Name))}."
                                    : $"\"{seg.Name}\": giá trị không phải đối tượng JSON.");
                            break;
                    }
                }
                current = next;
            }
            return multi ? string.Join("\n", current.Select(ToText)) : current.Count > 0 ? ToText(current[0]) : "";
        }
    }

    /// <summary>Khóa khớp chính xác, nếu không có thì không phân biệt hoa thường.</summary>
    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value)) return true;
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        return false;
    }

    public static string ToText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => e.GetRawText(),
        _ => JsonSerializer.Serialize(e, Compact)
    };

    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record Segment(string? Name, int? Index, bool Wildcard);

    private static List<Segment> Parse(string path)
    {
        var p = path.Trim();
        if (p.StartsWith('$')) p = p[1..];
        var result = new List<Segment>();
        var name = new StringBuilder();
        void Flush()
        {
            if (name.Length > 0) result.Add(new Segment(name.ToString().Trim(), null, false));
            name.Clear();
        }
        for (int i = 0; i < p.Length; i++)
        {
            char c = p[i];
            if (c == '.') { Flush(); continue; }
            if (c != '[') { name.Append(c); continue; }

            Flush();
            int end = p.IndexOf(']', i + 1);
            // Khóa trong ngoặc có thể chứa "]" — tìm dấu nháy đóng trước.
            if (i + 1 < p.Length && p[i + 1] is '"' or '\'')
            {
                char q = p[i + 1];
                int close = p.IndexOf(q, i + 2);
                if (close < 0) throw new FormatException($"Đường dẫn JSON \"{path}\" thiếu dấu {q} đóng.");
                result.Add(new Segment(p[(i + 2)..close], null, false));
                end = p.IndexOf(']', close);
                if (end < 0) throw new FormatException($"Đường dẫn JSON \"{path}\" thiếu dấu ].");
                i = end;
                continue;
            }
            if (end < 0) throw new FormatException($"Đường dẫn JSON \"{path}\" thiếu dấu ].");
            var inner = p[(i + 1)..end].Trim();
            if (inner == "*") result.Add(new Segment(null, null, true));
            else if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)) result.Add(new Segment(null, idx, false));
            else result.Add(new Segment(inner, null, false));
            i = end;
        }
        Flush();
        return result;
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
