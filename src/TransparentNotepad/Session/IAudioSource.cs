namespace TransparentNotepad.Session;

public enum AudioSourceKind { Microphone, SystemAudio, Both }

/// <summary>16 kHz PCM16 little-endian audio producer. Channels = 2 means interleaved [mic, system].</summary>
public interface IAudioSource : IDisposable
{
    int Channels { get; }
    /// <summary>When true, frames below the voice gate (-45 dBFS, adaptive) are not emitted (saves bandwidth for streaming ASR).</summary>
    bool NoiseGateEnabled { get; set; }
    event Action<byte[]>? PcmChunk;
    event Action<float>? Level;
    event Action<string>? Warning;
    void Start(AudioSourceKind kind);
    void Stop();
}
