namespace TransparentNotepad.Session;

/// <summary>
/// Lock-free single-producer / single-consumer ring buffer of samples.
/// Producer = audio capture callback, consumer = pump thread; neither ever blocks the other.
/// </summary>
public sealed class SpscFloatRing
{
    private readonly float[] _buf; private readonly int _mask;
    private long _head, _tail;      // monotonically increasing; head written by producer only, tail by consumer only
    private long _dropped;

    public SpscFloatRing(int capacityPow2)
    {
        if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0) throw new ArgumentException("capacity must be a power of two");
        _buf = new float[capacityPow2]; _mask = capacityPow2 - 1;
    }

    public int Capacity => _buf.Length;
    public int Available => (int)(Volatile.Read(ref _head) - Volatile.Read(ref _tail));
    /// <summary>Samples the producer had to discard because the consumer fell behind.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Producer only. Returns samples written (drops the excess if full).</summary>
    public int Write(ReadOnlySpan<float> src)
    {
        long h = Volatile.Read(ref _head), t = Volatile.Read(ref _tail);
        int n = Math.Min(_buf.Length - (int)(h - t), src.Length);
        for (int i = 0; i < n; i++) _buf[(h + i) & _mask] = src[i];
        Volatile.Write(ref _head, h + n);
        if (n < src.Length) Interlocked.Add(ref _dropped, src.Length - n);
        return n;
    }

    /// <summary>Consumer only. Returns samples read.</summary>
    public int Read(Span<float> dst)
    {
        long t = Volatile.Read(ref _tail), h = Volatile.Read(ref _head);
        int n = Math.Min((int)(h - t), dst.Length);
        for (int i = 0; i < n; i++) dst[i] = _buf[(t + i) & _mask];
        Volatile.Write(ref _tail, t + n);
        return n;
    }

    /// <summary>Consumer only. Discard up to n samples (latency catch-up).</summary>
    public int Skip(int n)
    {
        long t = Volatile.Read(ref _tail), h = Volatile.Read(ref _head);
        int k = Math.Min((int)(h - t), n);
        Volatile.Write(ref _tail, t + k);
        return k;
    }
}
