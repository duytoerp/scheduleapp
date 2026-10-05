using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScheduleApp.Services;

/// <summary>Gọi Claude (Anthropic Messages API) cho bước "Hỏi AI" và cho trình tạo flow bằng AI.</summary>
public static class AiClient
{
    public static readonly string[] Models = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5-20251001"];

    /// <summary>Địa chỉ API (kiểm thử trỏ sang máy chủ giả lập).</summary>
    internal static string Endpoint { get; set; } = "https://api.anthropic.com/v1/messages";
    /// <summary>Hạn chót cứng cho mỗi lần gọi khi người gọi không đặt thời gian chờ.</summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(10);

    // Không tự theo chuyển hướng: .NET chỉ bỏ Authorization, vẫn gửi header x-api-key sang máy chủ mới.
    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = MaxWait,
        MaxResponseContentBufferSize = 16 * 1024 * 1024
    };

    /// <summary>Chỉ dẫn mặc định: trả lời đúng phần được hỏi để dùng làm giá trị biến.</summary>
    private const string SystemPrompt =
        "Bạn là bước xử lý dữ liệu trong một quy trình tự động hóa trên máy tính. Câu trả lời của bạn được gán thẳng vào một biến " +
        "và dùng cho bước tiếp theo, nên chỉ trả về đúng kết quả được yêu cầu — không chào hỏi, không giải thích, không dùng markdown " +
        "trừ khi được yêu cầu rõ. Nếu được yêu cầu JSON thì trả về JSON hợp lệ thuần. Nếu không tìm thấy thông tin, trả về chuỗi rỗng.";

    public static bool IsConfigured => Protector.Unprotect(SettingsStore.Current.Ai.ApiKey).Length > 0;

    public static string Model => string.IsNullOrWhiteSpace(SettingsStore.Current.Ai.Model) ? Models[0] : SettingsStore.Current.Ai.Model.Trim();

    /// <param name="imagePng">Ảnh PNG đính kèm (ảnh chụp màn hình/cửa sổ), có thể null.</param>
    public static async Task<string> AskAsync(string prompt, byte[]? imagePng, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("Chưa nhập câu hỏi / yêu cầu cho AI.");

        var content = new JsonArray();
        if (imagePng != null)
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = Convert.ToBase64String(imagePng) }
            });
        content.Add(new JsonObject { ["type"] = "text", ["text"] = prompt });

        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = Math.Clamp(SettingsStore.Current.Ai.MaxTokens, 64, 32_000),
            ["system"] = SystemPrompt,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } }
        };

        var root = await PostAsync(body, timeoutMs, ct);
        var answer = new StringBuilder();
        if (root.TryGetProperty("content", out var parts))
            foreach (var p in parts.EnumerateArray())
                if (p.TryGetProperty("type", out var t) && t.GetString() == "text" && p.TryGetProperty("text", out var tx))
                    answer.Append(tx.GetString());
        if (root.TryGetProperty("stop_reason", out var stop) && stop.GetString() == "max_tokens")
            Log.Warn("      Câu trả lời của AI bị cắt do vượt giới hạn độ dài (tăng \"Số token tối đa\" trong Cài đặt).");
        return StripFence(answer.ToString().Trim());
    }

    /// <summary>Gửi một yêu cầu Messages API, trả về JSON phản hồi (báo lỗi rõ ràng khi API trả lỗi / quá thời gian).</summary>
    internal static async Task<JsonElement> PostAsync(JsonObject body, int timeoutMs, CancellationToken ct)
    {
        var key = Credentials.Reveal(SettingsStore.Current.Ai.ApiKey, "khóa API Claude");
        if (key.Length == 0) throw new InvalidOperationException("Chưa nhập khóa API Claude (⚙ Cài đặt → Tích hợp).");

        if (timeoutMs <= 0 || timeoutMs > MaxWait.TotalMilliseconds) timeoutMs = (int)MaxWait.TotalMilliseconds;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"AI chưa trả lời sau {timeoutMs / 1000} giây.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Không kết nối được tới Claude: " + Log.Redact(ex.Message), ex);
        }
        using (resp)
        {
            string text;
            try
            {
                text = await resp.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"AI chưa trả lời sau {timeoutMs / 1000} giây.");
            }
            using var doc = JsonDocument.Parse(text.Length > 0 ? text : "{}");
            if (!resp.IsSuccessStatusCode)
            {
                var msg = doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : text;
                throw new InvalidOperationException($"Claude trả về lỗi {(int)resp.StatusCode}: {ApiClient.Short(msg)}");
            }
            return doc.RootElement.Clone();
        }
    }

    /// <summary>Bỏ khung ```json … ``` nếu mô hình vẫn bọc kết quả trong markdown.</summary>
    private static string StripFence(string s)
    {
        if (!s.StartsWith("```")) return s;
        int firstLine = s.IndexOf('\n');
        int end = s.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine > 0 && end > firstLine ? s[(firstLine + 1)..end].Trim() : s;
    }
}
