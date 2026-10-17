using System.Net;
using System.Text;
using TransparentNotepad.Ai;

namespace TransparentNotepad.Tests;

/// <summary>Routes HttpClient traffic to a deterministic in-memory script (no network, no keys).</summary>
internal sealed class ScriptedHandler : HttpMessageHandler
{
    public readonly List<(string Method, Uri Url, string Body, string? Auth)> Calls = new();
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _route;
    public ScriptedHandler(Func<HttpRequestMessage, string, HttpResponseMessage> route) => _route = route;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        var body = r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct);
        lock (Calls) Calls.Add((r.Method.Method, r.RequestUri!, body, r.Headers.Authorization?.ToString()));
        return _route(r, body);       // may throw HttpRequestException to simulate "connection refused"
    }

    public static HttpResponseMessage Sse(params string[] deltas)
    {
        var sb = new StringBuilder();
        foreach (var d in deltas) sb.Append("data: {\"choices\":[{\"delta\":{\"content\":\"").Append(d).Append("\"}}]}\n\n");
        sb.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream") };
    }
    public static HttpResponseMessage Status(HttpStatusCode c) => new(c) { Content = new StringContent("{\"error\":\"x\"}") };
    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public static class KeylessLlmFailoverTests
{
    private static LlmEndpoint Groq() => new("Groq", LlmProvider.OpenAi, "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", "gsk_test");
    private static LlmEndpoint Ollama() => new("Ollama", LlmProvider.OpenAi, "http://localhost:11434/v1", "llama3.2", "");
    private static async Task<string> Collect(IAsyncEnumerable<string> e) { var sb = new StringBuilder(); await foreach (var d in e) sb.Append(d); return sb.ToString(); }

    private static (AiEngine Engine, ScriptedHandler H) Build(Func<HttpRequestMessage, string, HttpResponseMessage> route, bool allowKeyless)
    {
        var h = new ScriptedHandler(route); var http = new HttpClient(h);
        var failover = new LlmFailoverClient(new LlmClient(http)) { RetryBase = TimeSpan.FromMilliseconds(1) };
        return (new AiEngine(failover, new OllamaProbe(http), () => allowKeyless), h);
    }

    public static void Register(Action<string, Func<Task>> T)
    {
        T("Keyless chain: Groq 401 -> Ollama refused -> keyless cloud streams SSE; no auth header; vision payload is base64 image_url", async () =>
        {
            var (engine, h) = Build((r, b) =>
            {
                var host = r.RequestUri!.Host;
                if (host == "api.groq.com") return ScriptedHandler.Status(HttpStatusCode.Unauthorized);
                if (host == "localhost") throw new HttpRequestException("connection refused");
                if (host == "text.pollinations.ai") return ScriptedHandler.Sse("Hel", "lo");
                throw new InvalidOperationException("unexpected host " + host);
            }, allowKeyless: true);
            var img = new byte[] { 0xFF, 0xD8, 9, 8, 7 };
            var text = await Collect(engine.StreamAsync(new[] { Groq(), Ollama() }, AssistMode.Screen, "SYS", "what is on screen", 100, "You: hi", "", img));
            Assert.Eq("Hello", text, "keyless answer");
            Assert.Eq(true, h.Calls.Any(c => c.Url.Host == "api.groq.com"), "primary tried first");
            Assert.Eq(true, h.Calls.Any(c => c.Url.Host == "localhost" && c.Url.AbsolutePath == "/api/tags"), "ollama probed");
            Assert.Eq(false, h.Calls.Any(c => c.Url.Host == "localhost" && c.Url.AbsolutePath.Contains("chat")), "dead ollama never used for chat");
            var pol = h.Calls.Last(c => c.Url.Host == "text.pollinations.ai");
            Assert.Eq("/openai/chat/completions", pol.Url.AbsolutePath, "OpenAI-compatible path"); Assert.Eq(null, pol.Auth, "no auth header on keyless");
            Assert.Eq(true, pol.Body.Contains("image_url") && pol.Body.Contains("data:image/jpeg;base64," + Convert.ToBase64String(img)) && pol.Body.Contains("\"model\":\"openai\""), "vision payload");
            await Task.CompletedTask;
        });

        T("Keyless chain: 429 retried then failover; running Ollama (installed model auto-selected) wins before keyless cloud", async () =>
        {
            int groq = 0;
            var (engine, h) = Build((r, b) =>
            {
                var host = r.RequestUri!.Host;
                if (host == "api.groq.com") { groq++; return ScriptedHandler.Status(HttpStatusCode.TooManyRequests); }
                if (host == "localhost" && r.RequestUri.AbsolutePath == "/api/tags") return ScriptedHandler.Json("{\"models\":[{\"name\":\"qwen2.5:7b\"},{\"name\":\"llama3.2:latest\"}]}");
                if (host == "localhost") return ScriptedHandler.Sse("local");
                throw new InvalidOperationException("keyless must not be reached: " + host);
            }, allowKeyless: true);
            var text = await Collect(engine.StreamAsync(new[] { Groq(), Ollama() }, AssistMode.Assist, "S", "U", 50, "", ""));
            Assert.Eq("local", text, "ollama answered"); Assert.Eq(3, groq, "1 try + 2 backoff retries on 429");
            Assert.Eq(true, h.Calls.Last().Body.Contains("\"model\":\"llama3.2:latest\""), "installed model name used");
            await Task.CompletedTask;
        });

        T("Offline fallback: no key, Ollama down, keyless not allowed -> heuristic answer, zero cloud calls; vision explains why", async () =>
        {
            var (engine, h) = Build((r, b) => throw new HttpRequestException("offline"), allowKeyless: false);
            var notices = new List<string>(); engine.Notice += notices.Add;
            var t1 = await Collect(engine.StreamAsync(new[] { Ollama() }, AssistMode.Assist, "S", "U", 50, "Interviewer: Tell me about yourself.\nInterviewer: What is your biggest weakness?\nYou: Hmm", ""));
            Assert.Eq(true, t1.Contains(OfflineAssistant.Notice) && t1.Contains("Latest question: What is your biggest weakness?"), t1);
            Assert.Eq(false, h.Calls.Any(c => c.Url.Host.Contains("pollinations")), "no cloud call without consent"); Assert.Eq(1, notices.Count, "notice raised");
            var t2 = await Collect(engine.StreamAsync(new[] { Ollama() }, AssistMode.Screen, "S", "U", 50, "", "", new byte[] { 1 }));
            Assert.Eq(true, t2.Contains("vision-capable"), t2);
            var t3 = await Collect(engine.StreamAsync(Array.Empty<LlmEndpoint>(), AssistMode.ActionItems, "S", "U", 50, "You: I will send the report by Friday.\nThem: Great, we should review it.", ""));
            Assert.Eq(true, t3.Contains("send the report by Friday") && t3.Contains("review it"), t3); await Task.CompletedTask;
        });

        T("Offline mid-answer failure is surfaced, never replaced by offline text (no splicing)", async () =>
        {
            var (engine, _) = Build((r, b) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailAfterOneChunk()) }, allowKeyless: false);
            var got = new StringBuilder(); bool threw = false;
            try { await foreach (var d in engine.StreamAsync(new[] { Groq() }, AssistMode.Assist, "S", "U", 50, "", "")) got.Append(d); } catch { threw = true; }
            Assert.Eq(true, threw, "error propagates"); Assert.Eq("part", got.ToString(), "only the real tokens were emitted"); await Task.CompletedTask;
        });

        T("Ollama probe: prefers installed model, caches for 20 s, unreachable -> null", async () =>
        {
            int hits = 0; var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); bool up = true;
            var h = new ScriptedHandler((r, b) => { hits++; if (!up) throw new HttpRequestException("refused"); return ScriptedHandler.Json("{\"models\":[{\"name\":\"phi3:mini\"},{\"name\":\"llama3.2:3b\"}]}"); });
            var probe = new OllamaProbe(new HttpClient(h), () => now);
            Assert.Eq("llama3.2:3b", await probe.FindModelAsync("http://localhost:11434/v1", "llama3.2"), "preferred match");
            await probe.FindModelAsync("http://localhost:11434/v1", "llama3.2"); Assert.Eq(1, hits, "cached");
            now = now.AddSeconds(25); up = false; Assert.Eq(null, await probe.FindModelAsync("http://localhost:11434/v1", "llama3.2"), "unreachable"); Assert.Eq(2, hits, "re-probed after expiry");
            await Task.CompletedTask;
        });
    }
}

internal sealed class FailAfterOneChunk : Stream
{
    private bool _sent; private readonly byte[] _d = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"part\"}}]}\n\n");
    public override int Read(byte[] b, int o, int c) { if (_sent) throw new IOException("connection lost"); _sent = true; _d.CopyTo(b, o); return _d.Length; }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => 0;
    public override long Position { get => 0; set { } } public override void Flush() { } public override long Seek(long o, SeekOrigin s) => 0; public override void SetLength(long v) { } public override void Write(byte[] b, int o, int c) { }
}

/// <summary>Tiny assertion helper shared by the shared-case files.</summary>
internal static class Assert
{
    public static void Eq<T>(T expected, T actual, string what = "")
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what} expected [{expected}] got [{actual}]"); }
}
