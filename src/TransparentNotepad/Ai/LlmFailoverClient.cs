using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using TransparentNotepad.Logging;

namespace TransparentNotepad.Ai;

/// <summary>One configured LLM endpoint (OpenAI-compatible, or Anthropic).</summary>
public sealed record LlmEndpoint(string Name, LlmProvider Kind, string BaseUrl, string Model, string ApiKey, bool Enabled = true, bool KeyOptional = false)
{
    public bool IsLocal => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var u) && u.IsLoopback;
    public bool Usable => Enabled && (IsLocal || KeyOptional || !string.IsNullOrWhiteSpace(ApiKey));
}

/// <summary>
/// Provider-agnostic engine: tries endpoints in priority order and fails over (rate limit, auth, 5xx, timeout, network)
/// as long as no token has been streamed yet. A failing endpoint is put on a short cooldown so the next request skips it.
/// </summary>
public sealed class LlmFailoverClient
{
    private readonly LlmClient _inner;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<string, DateTime> _cooldownUntil = new();
    public TimeSpan FirstTokenTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Same-provider retries (exponential backoff + jitter) for HTTP 429/5xx before failing over.</summary>
    public int MaxRetries { get; set; } = 2;
    /// <summary>Hard cap for one answer (first token to last), so a stalled stream can never hang the UI.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromMinutes(2);
    private readonly AppLogger _log;
    public TimeSpan RetryBase { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Raised when a provider starts answering (name) or is skipped/failed ("Groq failed: …").</summary>
    public event Action<string>? ProviderUsed;
    public event Action<string>? ProviderFailed;

    public LlmFailoverClient(LlmClient? inner = null, Func<DateTime>? now = null, AppLogger? log = null)
    { _inner = inner ?? new LlmClient(); _now = now ?? (() => DateTime.UtcNow); _log = log ?? AppLogger.Default; }

    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<LlmEndpoint> endpoints, string system, string user, int maxTokens,
        [EnumeratorCancellation] CancellationToken ct = default, byte[]? image = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var usable = endpoints.Where(e => e.Usable).ToList();
        if (usable.Count == 0) throw new InvalidOperationException("No AI provider is configured. Open Settings and add a free API key (or enable Ollama).");
        var ready = usable.Where(e => !_cooldownUntil.TryGetValue(e.Name, out var t) || t <= _now()).ToList();
        if (ready.Count == 0) ready = usable; // everything cooling down: just try them all again
        Exception? last = null;

        foreach (var ep in ready)
        {
            bool moveOn = false;
            for (int attempt = 0; attempt <= MaxRetries && !moveOn; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(FirstTokenTimeout);
                await using var it = _inner.StreamAsync(new LlmRequest(ep.Kind, ep.ApiKey, ep.Model, ep.BaseUrl, system, user, maxTokens, image, ep.KeyOptional), cts.Token).GetAsyncEnumerator(cts.Token);
                bool started = false, retry = false;
                while (true)
                {
                    bool has = false; string? cur = null; Exception? err = null;
                    try { has = await it.MoveNextAsync(); if (has) cur = it.Current; }
                    catch (Exception ex) { err = ex; }

                    if (err != null)
                    {
                        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        if (started)                                                 // mid-answer failure: never splice two providers
                        {
                            _log.Warn("llm.stream_aborted", ("provider", ep.Name), ("error", err.Message));
                            if (err is OperationCanceledException) throw new TimeoutException($"{ep.Name} stopped responding mid-answer");
                            ExceptionDispatchInfo.Capture(err).Throw();
                        }
                        last = err is OperationCanceledException ? new TimeoutException($"{ep.Name} did not answer within {FirstTokenTimeout.TotalSeconds:0}s") : err;
                        if (attempt < MaxRetries && err is LlmHttpException { Status: 429 or >= 500 }) { retry = true; break; }
                        _cooldownUntil[ep.Name] = _now() + Cooldown(err);
                        _log.Warn("llm.provider_failed", ("provider", ep.Name), ("attempt", attempt + 1), ("error", Short(last)));
                        ProviderFailed?.Invoke($"{ep.Name} failed: {Short(last)}");
                        moveOn = true; break;                                      // next endpoint
                    }
                    if (!has) yield break;
                    if (!started) { started = true; cts.CancelAfter(TotalTimeout); _log.Info("llm.provider_used", ("provider", ep.Name), ("model", ep.Model), ("vision", image != null)); ProviderUsed?.Invoke(ep.Name); }
                    yield return cur!;
                }
                if (retry) await Task.Delay(TimeSpan.FromMilliseconds(RetryBase.TotalMilliseconds * Math.Pow(2, attempt) * (0.5 + Random.Shared.NextDouble() * 0.5)), ct);
            }
        }
        throw new InvalidOperationException($"All AI providers failed. Last error: {Short(last)}");
    }

    private static TimeSpan Cooldown(Exception e) => e is LlmHttpException h
        ? h.Status == 429 ? TimeSpan.FromSeconds(60) : h.Status is 401 or 403 or 404 ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(15)
        : TimeSpan.FromSeconds(15);

    private static string Short(Exception? e) { var m = e?.Message ?? "unknown"; return m.Length > 160 ? m[..160] : m; }
}
