using System.Threading.Channels;

namespace TransparentNotepad.Transcription;

/// <summary>Speech-to-text engine abstraction (16 kHz mono float samples in, text out). Real impl: Audio/WhisperNetStt.cs.</summary>
public interface ISpeechToText
{
    /// <summary>Load (and, first run, download) the model.</summary>
    Task InitializeAsync(CancellationToken ct);
    Task<string> TranscribeAsync(float[] samples16k, CancellationToken ct);
}

public sealed class LocalAsrOptions
{
    public float EnergyThreshold { get; init; } = 0.012f;   // RMS gate
    public int SilenceMs { get; init; } = 450;               // end-of-utterance pause
    public int MaxUtteranceMs { get; init; } = 10000;
    public int MinUtteranceMs { get; init; } = 300;
    public int PartialEveryMs { get; init; } = 500;          // minimum gap between live interim refreshes while speaking (0 = off)
    public int PartialWindowMs { get; init; } = 8000;        // sliding window: only the last N ms are re-transcribed for interims
}

/// <summary>Energy-VAD utterance splitter for one channel.</summary>
public sealed class UtteranceSegmenter
{
    private const int Rate = 16000;
    private readonly LocalAsrOptions _o;
    private readonly List<float> _buf = new();
    private readonly Queue<float[]> _pre = new();
    private int _silence;
    public bool InSpeech { get; private set; }
    public UtteranceSegmenter(LocalAsrOptions o) => _o = o;

    public float[] Snapshot(int maxMs = 0)
    {
        int max = maxMs > 0 ? maxMs * Rate / 1000 : int.MaxValue;
        return _buf.Count <= max ? _buf.ToArray() : _buf.GetRange(_buf.Count - max, max).ToArray();
    }

    /// <summary>Feed one frame; returns a finished utterance when a pause (or the max length) ends it.</summary>
    public float[]? Feed(float[] frame)
    {
        double s = 0; foreach (var v in frame) s += v * v;
        bool speech = frame.Length > 0 && Math.Sqrt(s / frame.Length) >= _o.EnergyThreshold;
        if (!InSpeech)
        {
            if (!speech) { _pre.Enqueue(frame); if (_pre.Count > 5) _pre.Dequeue(); return null; } // ~200 ms pre-roll
            InSpeech = true; _silence = 0;
            foreach (var p in _pre) _buf.AddRange(p);
            _pre.Clear();
        }
        _buf.AddRange(frame);
        _silence = speech ? 0 : _silence + frame.Length;
        if (_silence >= _o.SilenceMs * Rate / 1000 || _buf.Count >= _o.MaxUtteranceMs * Rate / 1000) return Finish();
        return null;
    }

    private float[]? Finish()
    {
        var arr = _buf.ToArray(); int spoken = arr.Length - _silence;
        _buf.Clear(); _silence = 0; InSpeech = false;
        return spoken >= _o.MinUtteranceMs * Rate / 1000 ? arr : null;
    }
}

/// <summary>
/// Free, offline "streaming" ASR: VAD-segments each channel and transcribes utterances with a local model.
/// Stereo input ([mic, system]) keeps You/Them separation. Same contract as DeepgramClient.
/// </summary>
public sealed class WhisperLocalClient : ITranscriptionClient
{
    private readonly ISpeechToText _stt; private readonly int _channels; private readonly LocalAsrOptions _o;
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly SemaphoreSlim _sttLock = new(1, 1);
    private CancellationTokenSource? _cts; private Task? _run;
    private volatile int _lastCostMs; // adaptive: interim cadence never outruns the engine (cloud rate limits / slow CPUs)

    public event Action<TranscriptEvent>? Transcript;
    public event Action<ConnectionState>? StateChanged;
    public event Action<string>? Error;
    public ConnectionState State { get; private set; } = ConnectionState.Idle;

    public WhisperLocalClient(ISpeechToText stt, int channels, LocalAsrOptions? o = null) { _stt = stt; _channels = Math.Max(1, channels); _o = o ?? new(); }

    private void SetState(ConnectionState s) { if (State != s) { State = s; StateChanged?.Invoke(s); } }
    public void Send(byte[] pcm) => _queue.Writer.TryWrite(pcm);
    public void Start() { if (_run != null) return; _cts = new(); _run = Task.Run(() => RunAsync(_cts.Token)); }

    private async Task RunAsync(CancellationToken ct)
    {
        SetState(ConnectionState.Connecting);
        try { await _stt.InitializeAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { SetState(ConnectionState.Closed); return; }
        catch (Exception e) { Error?.Invoke($"Speech model failed to load: {e.Message}"); SetState(ConnectionState.Closed); return; }
        SetState(ConnectionState.Open);

        var segs = Enumerable.Range(0, _channels).Select(_ => new UtteranceSegmenter(_o)).ToArray();
        var consumed = new long[_channels]; var lastPartial = new DateTime[_channels];
        try
        {
            await foreach (var pcm in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                int n = pcm.Length / 2 / _channels;
                for (int c = 0; c < _channels; c++)
                {
                    var frame = new float[n];
                    for (int i = 0; i < n; i++) frame[i] = BitConverter.ToInt16(pcm, (i * _channels + c) * 2) / 32768f;
                    consumed[c] += n;
                    var utt = segs[c].Feed(frame);
                    if (utt != null) await EmitAsync(c, utt, true, (consumed[c] - utt.Length) / 16000.0, ct).ConfigureAwait(false);
                    else if (_o.PartialEveryMs > 0 && segs[c].InSpeech && (DateTime.UtcNow - lastPartial[c]).TotalMilliseconds >= Math.Max(_o.PartialEveryMs, 2 * _lastCostMs) && _sttLock.CurrentCount > 0)
                    {
                        lastPartial[c] = DateTime.UtcNow; var snap = segs[c].Snapshot(_o.PartialWindowMs); int ch = c;
                        _ = EmitAsync(ch, snap, false, 0, ct, tryOnly: true);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        SetState(ConnectionState.Closed);
    }

    private async Task EmitAsync(int channel, float[] samples, bool final, double start, CancellationToken ct, bool tryOnly = false)
    {
        if (tryOnly) { if (!await _sttLock.WaitAsync(0).ConfigureAwait(false)) return; }
        else await _sttLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var text = (await _stt.TranscribeAsync(samples, ct).ConfigureAwait(false)).Trim();
            _lastCostMs = (int)sw.ElapsedMilliseconds;
            if (text.Length > 0) Transcript?.Invoke(new TranscriptEvent(text, final, final, channel, null, start));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Error?.Invoke($"Transcription failed: {e.Message}"); }
        finally { _sttLock.Release(); }
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { if (_run != null) await Task.WhenAny(_run, Task.Delay(3000)).ConfigureAwait(false); } catch { }
        _cts.Dispose(); _cts = null; _run = null;
        while (_queue.Reader.TryRead(out _)) { }
        SetState(ConnectionState.Closed);
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        (_stt as IDisposable)?.Dispose();   // release the native model (StopAsync has already ended the worker)
    }
}
