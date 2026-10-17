using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TransparentNotepad.Transcription;

public sealed class DeepgramOptions
{
    public string ApiKey { get; init; } = "";
    public Uri BaseUri { get; init; } = new("wss://api.deepgram.com/v1/listen");
    public string Model { get; init; } = "nova-3";
    public string Language { get; init; } = "en";
    public int SampleRate { get; init; } = 16000;
    public int Channels { get; init; } = 1;
    public bool Diarize { get; init; } = true;
    /// <summary>Optional extra diarization model name (sent as diarize_model=...). Unset by default: plain diarize=true is the documented streaming parameter.</summary>
    public string? DiarizeModel { get; init; }
    public int EndpointingMs { get; init; } = 200;
    /// <summary>Audio kept while offline (40 ms per queued chunk).</summary>
    public int MaxBufferedChunks { get; init; } = 200;
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan MinBackoff { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxFailedAttempts { get; init; } = 6;

    public Uri BuildUri()
    {
        var q = $"model={Uri.EscapeDataString(Model)}&language={Uri.EscapeDataString(Language)}&encoding=linear16" +
                $"&sample_rate={SampleRate}&channels={Channels}&interim_results=true&smart_format=true&punctuate=true" +
                $"&endpointing={EndpointingMs}&utterance_end_ms=1000" +
                (Channels > 1 ? "&multichannel=true" : $"&diarize={(Diarize ? "true" : "false")}") +
                (Channels == 1 && Diarize && !string.IsNullOrWhiteSpace(DiarizeModel) ? $"&diarize_model={Uri.EscapeDataString(DiarizeModel)}" : "");
        return new UriBuilder(BaseUri) { Query = q }.Uri;
    }
}

/// <summary>
/// Deepgram live-streaming client over ClientWebSocket: bounded audio queue (survives reconnects),
/// exponential backoff with jitter, keep-alive, graceful CloseStream.
/// </summary>
public sealed class DeepgramClient : ITranscriptionClient
{
    private readonly DeepgramOptions _o;
    private readonly Channel<byte[]> _queue;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _run;
    private byte[]? _unsent;

    public event Action<TranscriptEvent>? Transcript;
    public event Action<ConnectionState>? StateChanged;
    public event Action<string>? Error;
    public ConnectionState State { get; private set; } = ConnectionState.Idle;

    public DeepgramClient(DeepgramOptions options)
    {
        _o = options;
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.MaxBufferedChunks)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    }

    public void Start()
    {
        if (_run != null) return;
        if (string.IsNullOrWhiteSpace(_o.ApiKey)) { SetState(ConnectionState.Closed); Error?.Invoke("Cannot reach Deepgram: no API key configured."); return; }
        _cts = new CancellationTokenSource();
        _run = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Send(byte[] pcm) => _queue.Writer.TryWrite(pcm);

    private void SetState(ConnectionState s) { if (State != s) { State = s; StateChanged?.Invoke(s); } }

    private async Task RunAsync(CancellationToken ct)
    {
        int attempts = 0;
        while (!ct.IsCancellationRequested)
        {
            SetState(attempts == 0 ? ConnectionState.Connecting : ConnectionState.Reconnecting);
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Authorization", $"Token {_o.ApiKey}");
            var openedAt = DateTime.MinValue;
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                await ws.ConnectAsync(_o.BuildUri(), connectCts.Token).ConfigureAwait(false);
                openedAt = DateTime.UtcNow;
                SetState(ConnectionState.Open);

                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var tasks = new[] { SendLoopAsync(ws, sessionCts.Token), ReceiveLoopAsync(ws, sessionCts.Token), KeepAliveLoopAsync(ws, sessionCts.Token) };
                await Task.WhenAny(tasks).ConfigureAwait(false);
                sessionCts.Cancel();
                try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* individual loop failures already handled */ }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception) { /* connect failure / dropped socket: fall through to backoff */ }

            if (ct.IsCancellationRequested) break;
            if (openedAt != DateTime.MinValue && DateTime.UtcNow - openedAt > TimeSpan.FromSeconds(3)) attempts = 0;
            attempts++;
            if (attempts > _o.MaxFailedAttempts)
            {
                SetState(ConnectionState.Closed);
                Error?.Invoke("Cannot reach the transcription service. Check the Deepgram API key and your network.");
                return;
            }
            SetState(ConnectionState.Reconnecting);
            var baseDelay = Math.Min(_o.MaxBackoff.TotalMilliseconds, _o.MinBackoff.TotalMilliseconds * Math.Pow(2, attempts - 1));
            var delay = TimeSpan.FromMilliseconds(baseDelay * (0.5 + Random.Shared.NextDouble() * 0.5));
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
        SetState(ConnectionState.Closed);
    }

    private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var chunk = _unsent ??= await _queue.Reader.ReadAsync(ct).ConfigureAwait(false); // keep until acknowledged by a successful send
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try { await ws.SendAsync(chunk, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false); }
            finally { _sendLock.Release(); }
            _unsent = null;
        }
    }

    private async Task KeepAliveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var msg = Encoding.UTF8.GetBytes("{\"type\":\"KeepAlive\"}");
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_o.KeepAlive, ct).ConfigureAwait(false);
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try { await ws.SendAsync(msg, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            finally { _sendLock.Release(); }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (!ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            if (r.MessageType == WebSocketMessageType.Text) HandleMessage(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }
    }

    internal void HandleMessage(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "Results")
            {
                var alt = root.GetProperty("channel").GetProperty("alternatives")[0];
                var text = alt.GetProperty("transcript").GetString()?.Trim();
                if (string.IsNullOrEmpty(text)) return;
                int channel = root.TryGetProperty("channel_index", out var ci) && ci.GetArrayLength() > 0 ? ci[0].GetInt32() : 0;
                bool isFinal = root.TryGetProperty("is_final", out var f) && f.GetBoolean();
                bool speechFinal = root.TryGetProperty("speech_final", out var sf) && sf.GetBoolean();
                double start = root.TryGetProperty("start", out var st) ? st.GetDouble() : 0;
                var runs = isFinal && _o.Diarize && _o.Channels == 1 ? SpeakerRuns(alt) : null;
                if (runs is { Count: > 1 })
                {   // a final result that contains several speakers becomes one event per speaker turn
                    for (int i = 0; i < runs.Count; i++)
                        Transcript?.Invoke(new TranscriptEvent(runs[i].Text, true, speechFinal && i == runs.Count - 1, channel, runs[i].Speaker, runs[i].Start));
                }
                else Transcript?.Invoke(new TranscriptEvent(text, isFinal, speechFinal, channel, DominantSpeaker(alt), start));
            }
            else if (type == "Error" || root.TryGetProperty("err_code", out _))
            {
                Error?.Invoke(root.TryGetProperty("description", out var d) ? d.GetString() ?? "Deepgram error" : "Deepgram error");
            }
        }
        catch (JsonException) { /* ignore malformed frame */ }
        catch (KeyNotFoundException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Groups consecutive words by speaker. Returns null when the words carry no speaker ids.</summary>
    internal static List<(int? Speaker, string Text, double Start)>? SpeakerRuns(JsonElement alt)
    {
        if (!alt.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array || words.GetArrayLength() == 0) return null;
        var runs = new List<(int? Speaker, System.Text.StringBuilder Text, double Start)>();
        foreach (var w in words.EnumerateArray())
        {
            int? sp = w.TryGetProperty("speaker", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null;
            var tok = (w.TryGetProperty("punctuated_word", out var pw) ? pw.GetString() : null) ?? (w.TryGetProperty("word", out var ww) ? ww.GetString() : null);
            if (string.IsNullOrEmpty(tok)) continue;
            if (runs.Count == 0 || runs[^1].Speaker != sp) runs.Add((sp, new System.Text.StringBuilder(), w.TryGetProperty("start", out var st) ? st.GetDouble() : 0));
            if (runs[^1].Text.Length > 0) runs[^1].Text.Append(' ');
            runs[^1].Text.Append(tok);
        }
        return runs.Count == 0 ? null : runs.Select(r => (r.Speaker, r.Text.ToString(), r.Start)).ToList();
    }

    private static int? DominantSpeaker(JsonElement alt)
    {
        if (!alt.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array) return null;
        var counts = new Dictionary<int, int>();
        foreach (var w in words.EnumerateArray())
            if (w.TryGetProperty("speaker", out var s) && s.ValueKind == JsonValueKind.Number)
                counts[s.GetInt32()] = counts.GetValueOrDefault(s.GetInt32()) + 1;
        return counts.Count == 0 ? null : counts.MaxBy(kv => kv.Value).Key;
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { if (_run != null) await Task.WhenAny(_run, Task.Delay(3000)).ConfigureAwait(false); } catch { }
        _cts.Dispose(); _cts = null; _run = null;
        while (_queue.Reader.TryRead(out _)) { }
        _unsent = null;
        SetState(ConnectionState.Closed);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
