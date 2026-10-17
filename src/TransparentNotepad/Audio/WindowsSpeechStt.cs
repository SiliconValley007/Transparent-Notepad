using System.Speech.AudioFormat;
using System.Speech.Recognition;
using TransparentNotepad.Transcription;

namespace TransparentNotepad.Audio;

/// <summary>Instant, offline, zero-download recognizer built into Windows (lower accuracy than Whisper). Used while the Whisper model loads.</summary>
public sealed class WindowsSpeechStt : ISpeechToText, IDisposable
{
    private SpeechRecognitionEngine? _engine;
    private readonly object _gate = new();

    public Task InitializeAsync(CancellationToken ct)
    {
        _engine = new SpeechRecognitionEngine();            // throws when no recognizer is installed -> caller falls back
        _engine.LoadGrammar(new DictationGrammar());
        return Task.CompletedTask;
    }

    public Task<string> TranscribeAsync(float[] samples, CancellationToken ct) => Task.Run(() =>
    {
        var engine = _engine ?? throw new InvalidOperationException("Windows speech recognizer not initialised");
        var pcm = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++) BitConverter.GetBytes((short)(Math.Clamp(samples[i], -1f, 1f) * 32767f)).CopyTo(pcm, i * 2);
        lock (_gate)
        {
            using var ms = new MemoryStream(pcm);
            engine.SetInputToAudioStream(ms, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            var result = engine.Recognize(TimeSpan.FromSeconds(15));
            return result?.Text?.Trim() ?? "";
        }
    }, ct);

    public void Dispose() { _engine?.Dispose(); _engine = null; }
}
