using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace TransparentNotepad.Ai;

public enum LlmProvider { Anthropic, OpenAi }

public sealed record LlmRequest(LlmProvider Provider, string ApiKey, string Model, string BaseUrl, string System, string User, int MaxTokens = 1024, byte[]? Image = null, bool KeyOptional = false);

/// <summary>HTTP failure from an LLM endpoint (carries the status code so the failover engine can classify it).</summary>
public sealed class LlmHttpException : HttpRequestException
{
    public int Status { get; }
    public LlmHttpException(int status, string message) : base(message) => Status = status;
}

/// <summary>Streams text deltas from Anthropic Messages or any OpenAI-compatible /chat/completions endpoint (SSE).</summary>
public sealed class LlmClient
{
    private readonly HttpClient _http;
    public LlmClient(HttpClient? http = null) => _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    public async IAsyncEnumerable<string> StreamAsync(LlmRequest r, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (!Uri.TryCreate(r.BaseUrl, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException($"Invalid base URL for {r.Provider}: '{r.BaseUrl}'");
        if (string.IsNullOrWhiteSpace(r.Model)) throw new InvalidOperationException($"No model set for {r.Provider}.");
        bool local = u.IsLoopback;
        if (string.IsNullOrWhiteSpace(r.ApiKey) && !local && !r.KeyOptional) throw new InvalidOperationException($"Add your {r.Provider} API key in Settings.");

        using var req = r.Provider == LlmProvider.Anthropic ? BuildAnthropic(r) : BuildOpenAi(r);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new LlmHttpException((int)res.StatusCode, $"LLM request failed ({(int)res.StatusCode}) {(body.Length > 300 ? body[..300] : body)}".Trim());
        }

        using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string evt = "message"; var data = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var (delta, stop) = Parse(r.Provider, evt, data.ToString());
                    if (!string.IsNullOrEmpty(delta)) yield return delta;
                    if (stop) yield break;
                }
                evt = "message"; data.Clear();
            }
            else if (line.StartsWith("event:")) evt = line[6..].Trim();
            else if (line.StartsWith("data:")) { if (data.Length > 0) data.Append('\n'); data.Append(line[5..].TrimStart(' ')); }
        }
        if (data.Length > 0)
        {
            var (delta, _) = Parse(r.Provider, evt, data.ToString());
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

    private static HttpRequestMessage BuildAnthropic(LlmRequest r)
    {
        object content = r.Image == null ? r.User : new object[]
        {
            new { type = "image", source = new { type = "base64", media_type = "image/jpeg", data = Convert.ToBase64String(r.Image) } },
            new { type = "text", text = r.User }
        };
        var body = JsonSerializer.Serialize(new { model = r.Model, max_tokens = r.MaxTokens, stream = true, system = r.System, messages = new[] { new { role = "user", content } } });
        var m = new HttpRequestMessage(HttpMethod.Post, $"{r.BaseUrl.TrimEnd('/')}/v1/messages") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        m.Headers.Add("x-api-key", r.ApiKey);
        m.Headers.Add("anthropic-version", "2023-06-01");
        return m;
    }

    private static HttpRequestMessage BuildOpenAi(LlmRequest r)
    {
        // Vision: OpenAI-compatible multimodal content (works for Gemini's, OpenRouter's and Groq's OpenAI endpoints)
        object userContent = r.Image == null ? r.User : new object[]
        {
            new { type = "text", text = r.User },
            new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + Convert.ToBase64String(r.Image) } }
        };
        var body = JsonSerializer.Serialize(new { model = r.Model, stream = true, messages = new object[] { new { role = "system", content = r.System }, new { role = "user", content = userContent } } });
        var m = new HttpRequestMessage(HttpMethod.Post, $"{r.BaseUrl.TrimEnd('/')}/chat/completions") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (!string.IsNullOrWhiteSpace(r.ApiKey)) m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", r.ApiKey);
        return m;
    }

    private static (string? Delta, bool Stop) Parse(LlmProvider p, string evt, string data)
    {
        if (p == LlmProvider.OpenAi && data == "[DONE]") return (null, true);
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (p == LlmProvider.Anthropic)
            {
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (evt == "error" || type == "error")
                    throw new HttpRequestException(root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : "Anthropic stream error");
                if (type == "message_stop") return (null, true);
                if (type == "content_block_delta" && root.GetProperty("delta").TryGetProperty("text", out var tx)) return (tx.GetString(), false);
                return (null, false);
            }
            if (root.TryGetProperty("error", out var err))
                throw new HttpRequestException(err.TryGetProperty("message", out var em) ? em.GetString() : "LLM stream error");
            if (root.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0 &&
                ch[0].TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                return (c.GetString(), false);
            return (null, false);
        }
        catch (JsonException) { return (null, false); }
    }
}
