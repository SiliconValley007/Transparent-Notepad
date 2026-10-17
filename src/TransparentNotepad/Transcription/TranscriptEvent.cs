namespace TransparentNotepad.Transcription;

/// <summary>One recognition result from the streaming ASR service.</summary>
/// <param name="Channel">Audio channel index (0 = mic, 1 = system audio when multichannel).</param>
/// <param name="Speaker">Diarization speaker id when available.</param>
public sealed record TranscriptEvent(string Text, bool IsFinal, bool SpeechFinal, int Channel, int? Speaker, double Start);

public enum ConnectionState { Idle, Connecting, Open, Reconnecting, Closed }

public interface ITranscriptionClient : IAsyncDisposable
{
    event Action<TranscriptEvent>? Transcript;
    event Action<ConnectionState>? StateChanged;
    event Action<string>? Error;
    void Start();
    /// <summary>Queue a raw PCM16 little-endian chunk. Never blocks; oldest audio is dropped if offline too long.</summary>
    void Send(byte[] pcm);
    Task StopAsync();
}
