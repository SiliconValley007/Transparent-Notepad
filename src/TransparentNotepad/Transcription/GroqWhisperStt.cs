using System.Net.Http.Headers;
using System.Text.Json;

namespace TransparentNotepad.Transcription;

public sealed class AsrHttpException : HttpRequestException
{
    public int Status { get; }
    public AsrHttpException(int status, string msg) : base(msg) => Status = status;
}

/// <summary>Groq Cloud Whisper (whisper-large-v3-turbo) over REST, OpenAI-compatible /audio/transcriptions.</summary>
public sealed class GroqWhisperStt : ISpeechToText
{
    private readonly string _key, _lang, _model, _baseUrl; private readonly HttpClient _http;
    public GroqWhisperStt(string apiKey, string language, string model = "whisper-large-v3-turbo", string baseUrl = "https://api.groq.com/openai/v1", HttpClient? http = null)
    { _key = apiKey; _lang = language; _model = model; _baseUrl = baseUrl.TrimEnd('/'); _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) }; }

    public Task InitializeAsync(CancellationToken ct) =>
        string.IsNullOrWhiteSpace(_key) ? Task.FromException(new InvalidOperationException("Groq API key missing")) : Task.CompletedTask;

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var wav = new ByteArrayContent(ToWav(samples)); wav.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(wav, "file", "audio.wav");
        form.Add(new StringContent(_model), "model");
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("0"), "temperature");
        if (_lang is not ("multi" or "auto" or "")) form.Add(new StringContent(_lang), "language");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/audio/transcriptions") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new AsrHttpException((int)res.StatusCode, $"Groq transcription failed ({(int)res.StatusCode}) {(body.Length > 200 ? body[..200] : body)}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("text", out var t) ? Clean(t.GetString() ?? "") : "";
    }

    /// <summary>Drop Whisper's well-known silence hallucinations and non-speech tags.</summary>
    internal static string Clean(string t)
    {
        t = t.Trim();
        if (t.Length == 0) return "";
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^[\[\(\*♪].*[\]\)\*♪]$")) return "";
        var low = t.ToLowerInvariant().TrimEnd('.', '!', ' ');
        return low is "thank you" or "thanks for watching" or "you" or "bye" ? "" : t;
    }

    /// <summary>16 kHz mono PCM16 WAV.</summary>
    public static byte[] ToWav(float[] s)
    {
        int dataLen = s.Length * 2; var b = new byte[44 + dataLen];
        void W(int o, string x) { for (int i = 0; i < x.Length; i++) b[o + i] = (byte)x[i]; }
        W(0, "RIFF"); BitConverter.GetBytes(36 + dataLen).CopyTo(b, 4); W(8, "WAVEfmt ");
        BitConverter.GetBytes(16).CopyTo(b, 16); BitConverter.GetBytes((short)1).CopyTo(b, 20); BitConverter.GetBytes((short)1).CopyTo(b, 22);
        BitConverter.GetBytes(16000).CopyTo(b, 24); BitConverter.GetBytes(32000).CopyTo(b, 28); BitConverter.GetBytes((short)2).CopyTo(b, 32); BitConverter.GetBytes((short)16).CopyTo(b, 34);
        W(36, "data"); BitConverter.GetBytes(dataLen).CopyTo(b, 40);
        for (int i = 0; i < s.Length; i++) { var v = Math.Clamp(s[i], -1f, 1f); BitConverter.GetBytes((short)(v < 0 ? v * 32768f : v * 32767f)).CopyTo(b, 44 + i * 2); }
        return b;
    }
}

/// <summary>Cloud first; on any failure (rate limit, offline, bad key) transparently uses the local model instead.</summary>
public sealed class FallbackSpeechToText : ISpeechToText, IDisposable
{
    private readonly ISpeechToText _primary, _fallback;
    private bool _fallbackReady; private DateTime _primaryRetryAt = DateTime.MinValue;
    public event Action<string>? Notice;
    public FallbackSpeechToText(ISpeechToText primary, ISpeechToText fallback) { _primary = primary; _fallback = fallback; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        try { await _primary.InitializeAsync(ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { Notice?.Invoke($"Cloud speech unavailable ({e.Message}); using local model."); await EnsureFallback(ct).ConfigureAwait(false); _primaryRetryAt = DateTime.MaxValue; }
    }

    public void Dispose() { (_primary as IDisposable)?.Dispose(); (_fallback as IDisposable)?.Dispose(); }

    private async Task EnsureFallback(CancellationToken ct) { if (!_fallbackReady) { await _fallback.InitializeAsync(ct).ConfigureAwait(false); _fallbackReady = true; } }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        if (DateTime.UtcNow >= _primaryRetryAt)
        {
            try { var r = await _primary.TranscribeAsync(samples, ct).ConfigureAwait(false); if (_primaryRetryAt != DateTime.MinValue) Notice?.Invoke("Cloud speech recovered."); _primaryRetryAt = DateTime.MinValue; return r; }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var wait = e is AsrHttpException { Status: 429 } ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(15);
                _primaryRetryAt = DateTime.UtcNow + wait; Notice?.Invoke("Cloud speech failed; switched to local model temporarily.");
            }
        }
        await EnsureFallback(ct).ConfigureAwait(false);
        return await _fallback.TranscribeAsync(samples, ct).ConfigureAwait(false);
    }
}
