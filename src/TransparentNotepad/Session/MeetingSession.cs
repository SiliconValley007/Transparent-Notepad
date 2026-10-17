using TransparentNotepad.Transcription;

namespace TransparentNotepad.Session;

/// <summary>Glue: audio source -> transcription client -> transcript buffer. UI-agnostic; events fire on worker threads.</summary>
public sealed class MeetingSession : IAsyncDisposable
{
    private readonly IAudioSource _audio;
    private readonly Func<int, ITranscriptionClient> _clientFactory; // arg = channel count
    private ITranscriptionClient? _client;
    private AudioSourceKind _kind;
    public TranscriptBuffer Buffer { get; } = new();

    public event Action<Commit>? FinalCommitted;
    public event Action? InterimChanged;
    public event Action<ConnectionState>? StateChanged;
    public event Action<string>? Error;
    public event Action<string>? Warning;
    public event Action<float>? Level;
    public bool Running { get; private set; }

    private string _systemLabel;
    /// <summary>Label for system-audio speech (changeable at runtime from Settings).</summary>
    public string SystemLabel { get => _systemLabel; set => _systemLabel = string.IsNullOrWhiteSpace(value) ? "Interviewer" : value.Trim(); }

    /// <param name="systemLabel">Speaker label for system (loopback) audio when mic + system are captured as separate channels.</param>
    public MeetingSession(IAudioSource audio, Func<int, ITranscriptionClient> clientFactory, string systemLabel = "Interviewer")
    {
        _audio = audio; _clientFactory = clientFactory; _systemLabel = string.IsNullOrWhiteSpace(systemLabel) ? "Interviewer" : systemLabel.Trim();
        _audio.Warning += w => Warning?.Invoke(w);               // subscribed once: Start/Stop can repeat safely
        _audio.Level += l => Level?.Invoke(l);
        _audio.PcmChunk += pcm => _client?.Send(pcm);
    }

    public void Start(AudioSourceKind kind)
    {
        if (Running) return;
        _kind = kind;
        _audio.Start(kind); // may throw if no device; nothing else started yet
        try
        {
            _client = _clientFactory(_audio.Channels);
            _client.Transcript += OnTranscript;
            _client.StateChanged += s => StateChanged?.Invoke(s);
            _client.Error += e => { Error?.Invoke(e); };
            _client.Start();
            Running = true;
        }
        catch { _audio.Stop(); throw; }
    }

    internal string Label(TranscriptEvent e) => _audio.Channels == 2 ? (e.Channel == 0 ? "You" : _systemLabel)
        : _kind == AudioSourceKind.Microphone ? "You"
        : e.Speaker is int s ? $"Speaker {s + 1}" : "Speaker";

    private void OnTranscript(TranscriptEvent e)
    {
        var label = Label(e);
        if (e.IsFinal) { var commit = Buffer.CommitFinal(label, e.Text); FinalCommitted?.Invoke(commit); }   // commit even when nobody is subscribed
        else { Buffer.SetInterim(label, e.Text); }
        InterimChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        if (!Running && _client == null) return;
        Running = false;
        _audio.Stop();
        if (_client != null) { await _client.StopAsync().ConfigureAwait(false); await _client.DisposeAsync().ConfigureAwait(false); _client = null; }
    }

    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _audio.Dispose(); }
}
