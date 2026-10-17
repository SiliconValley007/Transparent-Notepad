using System.Text.RegularExpressions;
using TransparentNotepad.Config;
using TransparentNotepad.Logging;
using TransparentNotepad.Transcription;
using Whisper.net;

namespace TransparentNotepad.Audio;

/// <summary>Local whisper.cpp (Whisper.net, CPU). Free, offline, no key. Model auto-downloads once (~150 MB for "base").</summary>
public sealed class WhisperNetStt : ISpeechToText, IDisposable
{
    private const long MinModelBytes = 30_000_000;                       // smallest ggml model (tiny) is ~75 MB
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _lang, _modelName;
    private WhisperFactory? _factory; private WhisperProcessor? _proc;
    public event Action<string>? Progress;

    public WhisperNetStt(string language, string modelName = "base") { _lang = string.IsNullOrWhiteSpace(language) ? "en" : language; _modelName = modelName; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        bool english = _lang == "en";
        var file = $"ggml-{_modelName}{(english ? ".en" : "")}.bin";
        var path = Path.Combine(ConfigStore.Dir, "models", file);
        if (!File.Exists(path) || new FileInfo(path).Length < MinModelBytes)   // missing or truncated by an earlier failure
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Progress?.Invoke($"Downloading speech model {file} (first run only)…");
            await DownloadAsync(file, path, ct).ConfigureAwait(false);
        }
        Progress?.Invoke("Loading speech model…");
        _factory = WhisperFactory.FromPath(path);
        var b = _factory.CreateBuilder();
        _proc = (_lang == "multi" || _lang == "auto" ? b.WithLanguageDetection() : b.WithLanguage(_lang)).Build();
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        if (_proc == null) throw new InvalidOperationException("Model not loaded");
        var sb = new System.Text.StringBuilder();
        await foreach (var seg in _proc.ProcessAsync(samples, ct)) sb.Append(seg.Text);
        var t = sb.ToString().Trim();
        // drop Whisper's non-speech artifacts: [BLANK_AUDIO], (music), ♪ …
        return Regex.IsMatch(t, @"^[\[\(\*♪].*[\]\)\*♪]$") ? "" : t;
    }

    private async Task DownloadAsync(string file, string path, CancellationToken ct)
    {
        var tmp = $"{path}.{Environment.ProcessId}.part";
        try
        {
            using var res = await Http.GetAsync($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{file}", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            long? total = res.Content.Headers.ContentLength; long done = 0; int lastPct = -10, n;
            var buf = new byte[81920];
            await using (var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, buf.Length, useAsync: true))
            {
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    if (total is > 0 && (int)(done * 100 / total.Value) >= lastPct + 10) { lastPct = (int)(done * 100 / total.Value); Progress?.Invoke($"Downloading speech model {file}… {lastPct}%"); }
                }
            }
            if ((total.HasValue && done != total.Value) || done < MinModelBytes) throw new IOException($"Model download incomplete ({done} bytes)");
            File.Move(tmp, path, overwrite: true);
            Log.Info("asr.model_downloaded", ("file", file), ("bytes", done));
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            Log.Error("asr.model_download_failed", ex, ("file", file));
            throw;
        }
    }

    public void Dispose()
    {
        var p = _proc; var f = _factory; _proc = null; _factory = null;
        p?.Dispose(); f?.Dispose();
    }
}
