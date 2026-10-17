using NAudio.Wave;

namespace TransparentNotepad.Audio;

/// <summary>Averages all channels of the source into one mono channel.</summary>
internal sealed class MonoDownmix : ISampleProvider
{
    private readonly ISampleProvider _src; private float[] _buf = Array.Empty<float>();
    public MonoDownmix(ISampleProvider src) { _src = src; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 1); }
    public WaveFormat WaveFormat { get; }
    public int Read(float[] buffer, int offset, int count)
    {
        int ch = _src.WaveFormat.Channels;
        if (ch == 1) return _src.Read(buffer, offset, count);
        if (_buf.Length < count * ch) _buf = new float[count * ch];
        int frames = _src.Read(_buf, 0, count * ch) / ch;
        for (int i = 0; i < frames; i++) { float s = 0; for (int c = 0; c < ch; c++) s += _buf[i * ch + c]; buffer[offset + i] = s / ch; }
        return frames;
    }
}
