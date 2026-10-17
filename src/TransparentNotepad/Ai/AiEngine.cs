using System.Net.Http;
using System.Text.Json;
using TransparentNotepad.Logging;

namespace TransparentNotepad.Ai;

/// <summary>Detects a running local Ollama and which models it has installed (short timeout, cached).</summary>
public sealed class OllamaProbe
{
    private readonly HttpClient _http; private readonly Func<DateTime> _now;
    private (DateTime At, string? Model)? _cache;
    public OllamaProbe(HttpClient? http = null, Func<DateTime>? now = null) { _http = http ?? new HttpClient { Timeout = TimeSpan.FromMilliseconds(600) }; _now = now ?? (() => DateTime.UtcNow); }

    /// <summary>Returns the model to use (the preferred one if installed, else the first installed), or null if Ollama isn't reachable / has no models.</summary>
    public async Task<string?> FindModelAsync(string baseUrl, string preferred, CancellationToken ct = default)
    {
        if (_cache is { } c && (_now() - c.At).TotalSeconds < 20) return c.Model;
        string? result = null;
        try
        {
            var root = baseUrl.TrimEnd('/'); if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root = root[..^3];
            using var res = await _http.GetAsync(root + "/api/tags", ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                var names = doc.RootElement.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array
                    ? m.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : null).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList() : new List<string>();
                result = names.FirstOrDefault(n => n.Equals(preferred, StringComparison.OrdinalIgnoreCase) || n.StartsWith(preferred + ":", StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException) { }
        _cache = (_now(), result);
        return result;
    }
}

/// <summary>Last-resort, fully offline helper: surfaces the most recent question and recent lines (no AI).</summary>
public static class OfflineAssistant
{
    public const string Notice = "[Offline mode - no AI provider reachable]";

    public static string Answer(AssistMode mode, string transcript, string notes, bool hadImage)
    {
        if (hadImage) return $"{Notice}\nScreen questions need a vision-capable AI. Enable the free keyless cloud in Settings, add a free Gemini key, or install Ollama with a vision model.";
        var lines = transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (lines.Count == 0) return $"{Notice}\nNothing has been transcribed yet.";
        static string Body(string l) { int i = l.IndexOf(':'); return i >= 0 ? l[(i + 1)..].Trim() : l; }
        var sentences = lines.SelectMany(l => System.Text.RegularExpressions.Regex.Split(Body(l), @"(?<=[.?!])\s+")).Where(s => s.Length > 3).ToList();
        switch (mode)
        {
            case AssistMode.ActionItems or AssistMode.MarkdownRecap or AssistMode.Summary:
                var cues = new[] { "will ", "need to", "should ", "must ", "deadline", " by ", "action", "follow up", "next step", "let's ", "i'll " };
                var items = sentences.Where(s => cues.Any(c => s.Contains(c, StringComparison.OrdinalIgnoreCase))).TakeLast(8).ToList();
                return $"{Notice}\nPossible action items (keyword match):\n" + (items.Count == 0 ? "- none detected" : string.Join("\n", items.Select(i => "- " + i)));
            case AssistMode.Clean or AssistMode.Diarize:
                return $"{Notice}\n" + string.Join("\n", lines);
            default:
                var q = sentences.LastOrDefault(s => s.EndsWith('?'));
                return $"{Notice}\n" + (q != null ? $"Latest question: {q}\n" : "") + "Recent:\n" + string.Join("\n", lines.TakeLast(3));
        }
    }
}

/// <summary>
/// Zero-configuration chain: configured providers (in priority order) -> auto-probed local Ollama -> keyless free cloud
/// (opt-in: it is a third-party anonymous service) -> built-in offline heuristic. Never throws "no provider" to the UI.
/// </summary>
public sealed class AiEngine
{
    public const string KeylessName = "Pollinations (free, no key)";
    private readonly LlmFailoverClient _failover; private readonly OllamaProbe _probe; private readonly Func<bool> _allowKeyless;
    public event Action<string>? Notice;

    public AiEngine(LlmFailoverClient failover, OllamaProbe? probe = null, Func<bool>? allowKeylessCloud = null)
    { _failover = failover; _probe = probe ?? new OllamaProbe(); _allowKeyless = allowKeylessCloud ?? (() => false); }

    public static LlmEndpoint Keyless() => new(KeylessName, LlmProvider.OpenAi, "https://text.pollinations.ai/openai", "openai", "", true, true);

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<LlmEndpoint> configured, AssistMode mode, string system, string user, int maxTokens,
        string transcript, string notes, byte[]? image = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var chain = new List<LlmEndpoint>();
        foreach (var ep in configured.Where(e => e.Enabled))
        {
            if (ep.IsLocal)   // local Ollama: only keep it if it is really running; pick an installed model
            {
                var model = await _probe.FindModelAsync(ep.BaseUrl, ep.Model, ct).ConfigureAwait(false);
                if (model != null) chain.Add(ep with { Model = image != null ? ep.Model : model });
            }
            else if (ep.Usable) chain.Add(ep);
        }
        if (_allowKeyless()) chain.Add(Keyless());

        bool started = false;
        if (chain.Count > 0)
        {
            await using var it = _failover.StreamAsync(chain, system, user, maxTokens, ct, image).GetAsyncEnumerator(ct);
            while (true)
            {
                bool has = false; string? cur = null; Exception? err = null;
                try { has = await it.MoveNextAsync(); if (has) cur = it.Current; } catch (Exception ex) { err = ex; }
                if (err != null)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    if (started) throw err;                                             // mid-answer: never splice
                    Log.Warn("ai.chain_exhausted", ("error", err.Message)); break;      // fall through to offline mode
                }
                if (!has) yield break;
                started = true; yield return cur!;
            }
        }
        Notice?.Invoke("No AI provider reachable - using offline mode");
        yield return OfflineAssistant.Answer(mode, transcript, notes, image != null);
    }
}
