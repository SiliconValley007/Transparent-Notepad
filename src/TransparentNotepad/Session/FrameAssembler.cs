namespace TransparentNotepad.Session;

/// <summary>
/// Pulls exactly one fixed-size frame per channel from the capture rings on a wall-clock cadence.
/// An idle ring (WASAPI loopback raises no data while system audio is silent) yields zero-amplitude samples,
/// so downstream VAD/endpointing always sees the silence that ends an utterance. Also caps backlog latency.
/// </summary>
public sealed class FrameAssembler
{
    private readonly int _frame, _maxBacklog; private readonly float[][] _per;
    public FrameAssembler(int channels, int frameSamples = 640, int maxBacklogSamples = 8000)
    {
        _frame = frameSamples; _maxBacklog = maxBacklogSamples;
        _per = new float[channels][]; for (int i = 0; i < channels; i++) _per[i] = new float[frameSamples];
    }

    /// <summary>Fills pcm (interleaved PCM16, length = frame * channels) and returns the frame RMS (0..1).</summary>
    public float Assemble(IReadOnlyList<SpscFloatRing> rings, short[] pcm)
    {
        int ch = _per.Length; double sumSq = 0;
        for (int c = 0; c < ch; c++)
        {
            var ring = rings[c]; int backlog = ring.Available;
            if (backlog > _maxBacklog) ring.Skip(backlog - _frame);        // fell behind: jump to live audio
            int n = ring.Read(_per[c]);
            if (n < _frame) Array.Clear(_per[c], n, _frame - n);            // idle/missing device => silence
        }
        for (int n = 0; n < _frame; n++)
            for (int c = 0; c < ch; c++)
            {
                float v = Math.Clamp(_per[c][n], -1f, 1f);
                pcm[n * ch + c] = (short)(v < 0 ? v * 32768f : v * 32767f);
                sumSq += v * v;
            }
        return (float)Math.Sqrt(sumSq / pcm.Length);
    }
}
