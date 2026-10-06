using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScheduleApp.Services;

/// <summary>
/// Gọi Telegram Bot API có thử lại: mạng chập chờn / máy chủ lỗi (5xx) → thử lại sau 2 rồi 5 giây; bị giới hạn tốc độ (429) → chờ đúng
/// số giây Telegram yêu cầu (retry_after, tối đa 60 giây). Lỗi khác (token sai, chat id sai…) báo ngay, không thử lại.
/// </summary>
internal static class TelegramApi
{
    /// <summary>Thời gian chờ giữa các lần thử (kiểm thử rút ngắn).</summary>
    internal static TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    /// <summary>Chờ tối đa khi bị giới hạn tốc độ.</summary>
    internal static TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Gửi một yêu cầu (nội dung dựng lại mỗi lần thử). Trả về phần "result" của phản hồi; ném HttpRequestException khi hết lượt thử.</summary>
    public static async Task<JsonElement> CallAsync(HttpClient http, string api, string method, Func<HttpContent> content, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            TimeSpan? wait;
            string problem;
            try
            {
                using var body = content();
                using var resp = await http.PostAsync($"{api}/{method}", body, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(json);
                    return doc.RootElement.TryGetProperty("result", out var result) ? result.Clone() : default;
                }
                problem = $"Telegram trả về {(int)resp.StatusCode}: {Describe(json)}";
                wait = resp.StatusCode == HttpStatusCode.TooManyRequests ? RetryAfter(json)
                     : (int)resp.StatusCode >= 500 ? Delay(attempt)
                     : null;   // 400 / 401 / 403…: thử lại cũng vậy
            }
            catch (HttpRequestException ex)
            {
                problem = ex.Message;
                wait = Delay(attempt);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                problem = "hết thời gian chờ Telegram (" + ex.Message + ")";
                wait = Delay(attempt);
            }
            if (wait is not { } w || attempt >= RetryDelays.Length) throw new HttpRequestException(problem);
            await Task.Delay(w, ct);
        }
    }

    public static Task<JsonElement> SendMessageAsync(HttpClient http, string api, string chatId, string text, JsonObject? keyboard, CancellationToken ct = default) =>
        CallAsync(http, api, "sendMessage", () =>
        {
            var o = new JsonObject { ["chat_id"] = long.TryParse(chatId, out long id) ? id : chatId, ["text"] = text };   // số, hoặc @tên_kênh
            if (keyboard != null) o["reply_markup"] = keyboard.DeepClone();
            return JsonContent(o);
        }, ct);

    public static Task<JsonElement> SendPhotoAsync(HttpClient http, string api, string chatId, byte[] photo, string fileName, string mime, string caption,
        JsonObject? keyboard, CancellationToken ct = default) =>
        CallAsync(http, api, "sendPhoto", () =>
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent(chatId), "chat_id" },
                { new StringContent(caption.Length > 1000 ? caption[..1000] + "…" : caption), "caption" }
            };
            if (keyboard != null) form.Add(new StringContent(keyboard.ToJsonString()), "reply_markup");
            var file = new ByteArrayContent(photo);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
            form.Add(file, "photo", fileName);
            return form;
        }, ct);

    public static HttpContent JsonContent(JsonNode node) =>
        new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    /// <summary>Bàn phím nút bấm dưới tin nhắn: mỗi mảng con là một hàng (chữ trên nút, dữ liệu gửi lại khi bấm — tối đa 64 byte).</summary>
    public static JsonObject Keyboard(params (string Text, string Data)[][] rows) => new()
    {
        ["inline_keyboard"] = new JsonArray([.. rows.Select(r => (JsonNode)new JsonArray([.. r.Select(b => (JsonNode)new JsonObject { ["text"] = b.Text, ["callback_data"] = b.Data })]))])
    };

    private static TimeSpan? Delay(int attempt) => attempt < RetryDelays.Length ? RetryDelays[attempt] : null;

    /// <summary>429: {"parameters":{"retry_after":N}} — chờ N giây (tối thiểu 1, tối đa <see cref="MaxRetryAfter"/>).</summary>
    private static TimeSpan RetryAfter(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("parameters", out var p) && p.TryGetProperty("retry_after", out var r) && r.TryGetInt32(out int s))
                return TimeSpan.FromSeconds(Math.Clamp(s, 1, MaxRetryAfter.TotalSeconds));
        }
        catch (JsonException) { }
        return RetryDelays.Length > 0 ? RetryDelays[^1] : TimeSpan.FromSeconds(5);
    }

    /// <summary>"description" trong phản hồi lỗi (ngắn gọn), không thì 200 ký tự đầu.</summary>
    private static string Describe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) return d.GetString()!;
        }
        catch (JsonException) { }
        return json.Length > 200 ? json[..200] + "…" : json;
    }
}
