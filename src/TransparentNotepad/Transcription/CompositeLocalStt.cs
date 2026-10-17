namespace TransparentNotepad.Transcription;

/// <summary>
/// Zero-config local speech: starts immediately on an instant engine (Windows built-in recognizer) while the better
/// Whisper model downloads/loads in the background, then switches to Whisper as soon as it is ready.
/// </summary>
public sealed class CompositeLocalStt : ISpeechToText, IDisposable
{
    private readonly ISpeechToText _best; private readonly ISpeechToText? _instant;
    private volatile bool _bestReady; private Task? _loading;
    public event Action<string>? Notice;

    public CompositeLocalStt(ISpeechToText best, ISpeechToText? instant) { _best = best; _instant = instant; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (_instant == null) { await _best.InitializeAsync(ct).ConfigureAwait(false); _bestReady = true; return; }
        try { await _instant.InitializeAsync(ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException)
        { await _best.InitializeAsync(ct).ConfigureAwait(false); _bestReady = true; return; }   // no instant engine on this PC: wait for Whisper
        Notice?.Invoke("Using Windows speech recognition while the Whisper model loads…");
        _loading = Task.Run(async () =>
        {
            try { await _best.InitializeAsync(ct).ConfigureAwait(false); _bestReady = true; Notice?.Invoke("Whisper model ready - better accuracy enabled."); }
            catch (Exception e) when (e is not OperationCanceledException) { Notice?.Invoke($"Whisper model unavailable ({e.Message}); staying on Windows speech recognition."); }
        }, ct);
    }

    public Task<string> TranscribeAsync(float[] samples, CancellationToken ct) =>
        _bestReady || _instant == null ? _best.TranscribeAsync(samples, ct) : _instant.TranscribeAsync(samples, ct);

    public void Dispose() { (_best as IDisposable)?.Dispose(); (_instant as IDisposable)?.Dispose(); }
}
