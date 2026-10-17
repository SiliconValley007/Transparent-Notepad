using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using TransparentNotepad.Session;

namespace TransparentNotepad.Audio;

/// <summary>
/// WASAPI microphone + loopback (system audio) -> mono -> 16 kHz -> PCM16 in steady 40 ms frames.
/// - Capture callbacks (single producer per device generation) push into a lock-free SPSC ring; one pump thread drains it.
/// - Device lifecycle: a supervisor re-opens a slot when Windows reports a default-device change / removal / state change,
///   when a capture stops with an error (headset unplugged, Bluetooth profile switch, sample-rate change), and keeps retrying
///   with backoff while no device exists. The channel layout is fixed for the session; a missing device just yields silence.
/// - Loopback failure falls back to standard WaveIn microphone capture; WASAPI mic failure falls back to WaveIn too.
/// </summary>
public sealed class NAudioCaptureSource : IAudioSource, IMMNotificationClient
{
    private const int Rate = 16000, FrameSamples = 640, MaxLatencySamples = 8000; // 40 ms frames; cap backlog at 500 ms
    private enum SlotKind { Mic, System }

    private sealed class Slot
    {
        public SlotKind Kind;
        public volatile SpscFloatRing Ring = new(1 << 16);   // replaced per device generation (never two producers on one ring)
        public IWaveIn? Cap;
        public volatile bool Dirty;
        public int Failures; public long NextAttemptTick; public bool LostNotified; public bool UsingWaveIn;
    }

    private readonly List<Slot> _slots = new();
    private readonly NoiseGate _gate = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private MMDeviceEnumerator? _enumerator;
    private CancellationTokenSource? _cts;
    private Task? _pump, _supervisor;

    public int Channels { get; private set; } = 1;
    public bool NoiseGateEnabled { get; set; }
    public event Action<byte[]>? PcmChunk;
    public event Action<float>? Level;
    public event Action<string>? Warning;

    // ---------------------------------------------------------------- start / stop
    public void Start(AudioSourceKind kind)
    {
        if (_pump != null) return;
        _slots.Clear();
        // Order matters: slot 0 = mic, slot 1 = system when both.
        if (kind is AudioSourceKind.Microphone or AudioSourceKind.Both) _slots.Add(new Slot { Kind = SlotKind.Mic });
        if (kind is AudioSourceKind.SystemAudio or AudioSourceKind.Both) _slots.Add(new Slot { Kind = SlotKind.System });

        foreach (var slot in _slots.ToArray())
        {
            try { Open(slot); }
            catch (Exception e)
            {
                if (_slots.Count == 1 && slot.Kind == SlotKind.System)
                {   // system-only and loopback failed: standard microphone capture instead
                    Warning?.Invoke($"System audio capture failed ({e.Message}). Using the microphone instead.");
                    slot.Kind = SlotKind.Mic;
                    try { Open(slot); } catch (Exception e2) { throw new InvalidOperationException($"No audio input available: {e2.Message}", e2); }
                }
                else if (_slots.Count == 1) throw new InvalidOperationException($"No audio input available: {e.Message}", e);
                else
                {   // both requested, one source missing: continue with the other (single channel)
                    Warning?.Invoke($"{(slot.Kind == SlotKind.Mic ? "Microphone" : "System audio")} unavailable ({e.Message}); continuing with the other source.");
                    _slots.Remove(slot);
                }
            }
        }
        if (_slots.Count == 0) throw new InvalidOperationException("No audio input available.");
        Channels = _slots.Count;       // 1 or 2, fixed for the whole session
        _gate.Reset();

        try { _enumerator = new MMDeviceEnumerator(); _enumerator.RegisterEndpointNotificationCallback(this); }
        catch (Exception e) { Warning?.Invoke($"Device change notifications unavailable ({e.Message}); recovery uses polling."); }

        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => Pump(_cts.Token));
        _supervisor = Task.Run(() => Supervise(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _supervisor?.Wait(1000); } catch { }
        try { _pump?.Wait(500); } catch { }
        try { _enumerator?.UnregisterEndpointNotificationCallback(this); } catch { }
        _enumerator?.Dispose(); _enumerator = null;
        foreach (var s in _slots) CloseCapture(s);
        _slots.Clear(); _cts?.Dispose(); _cts = null; _pump = null; _supervisor = null;
    }

    public void Dispose() => Stop();

    // ---------------------------------------------------------------- device open / close
    private void Open(Slot slot)
    {
        IWaveIn cap = slot.Kind == SlotKind.System ? OpenLoopback() : OpenMic(slot);
        ISampleProvider chain;
        BufferedWaveProvider buf = new(cap.WaveFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(1), ReadFully = false };
        chain = new MonoDownmix(buf.ToSampleProvider());
        if (chain.WaveFormat.SampleRate != Rate) chain = new WdlResamplingSampleProvider(chain, Rate);   // fresh format each generation (sample-rate changes)
        var ring = new SpscFloatRing(1 << 16); var tmp = new float[4096];
        cap.DataAvailable += (_, e) =>
        {
            buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
            int n; while ((n = chain.Read(tmp, 0, tmp.Length)) > 0) ring.Write(tmp.AsSpan(0, n));
        };
        cap.RecordingStopped += (_, e) =>
        {
            if (!ReferenceEquals(slot.Cap, cap)) return;       // we closed it ourselves
            slot.Dirty = true; _signal.Release();               // device vanished / format changed: recover
        };
        cap.StartRecording();                                    // throws if the device can't start -> caller handles
        var old = slot.Cap;
        slot.Ring = ring; slot.Cap = cap; slot.Dirty = false;
        if (old != null) DisposeCapture(old);
    }

    private IWaveIn OpenLoopback()
    {
        using var en = new MMDeviceEnumerator();
        return new WasapiLoopbackCapture(en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
    }

    private IWaveIn OpenMic(Slot slot)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var c = new WasapiCapture(en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications));
            slot.UsingWaveIn = false; return c;
        }
        catch (Exception e)
        {   // standard WaveIn microphone capture (16 kHz mono, no WASAPI)
            if (!slot.UsingWaveIn) Warning?.Invoke($"WASAPI microphone failed ({e.Message}); using standard WaveIn.");
            slot.UsingWaveIn = true;
            return new WaveInEvent { WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = 40 };
        }
    }

    private void CloseCapture(Slot s) { var c = s.Cap; s.Cap = null; if (c != null) DisposeCapture(c); }
    private static void DisposeCapture(IWaveIn c) { try { c.StopRecording(); } catch { } try { c.Dispose(); } catch { } }

    // ---------------------------------------------------------------- recovery supervisor
    private async Task Supervise(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(TimeSpan.FromSeconds(2), ct); await Task.Delay(400, ct); }   // wake on event, or poll every 2 s; debounce bursts
            catch (OperationCanceledException) { return; }
            while (_signal.CurrentCount > 0) _signal.Wait(0);
            foreach (var slot in _slots.ToArray())   // snapshot: Stop() may clear the list
            {
                if (ct.IsCancellationRequested) return;
                if (!slot.Dirty && slot.Cap != null) continue;
                if (Environment.TickCount64 < slot.NextAttemptTick) continue;
                try
                {
                    CloseCapture(slot);
                    Open(slot);
                    if (slot.LostNotified) Warning?.Invoke($"{(slot.Kind == SlotKind.Mic ? "Microphone" : "System audio")} recovered.");
                    slot.Failures = 0; slot.LostNotified = false; slot.NextAttemptTick = 0;
                }
                catch (Exception e)
                {
                    slot.Failures++;
                    slot.NextAttemptTick = Environment.TickCount64 + (long)RecoveryBackoff.Delay(slot.Failures).TotalMilliseconds;
                    if (!slot.LostNotified) { slot.LostNotified = true; Warning?.Invoke($"{(slot.Kind == SlotKind.Mic ? "Microphone" : "System audio")} lost ({e.Message}); waiting for a device…"); }
                    // loopback keeps failing while no mic is in use and it is the only source: fall back to the microphone
                    if (slot.Kind == SlotKind.System && _slots.Count == 1 && slot.Failures >= 3)
                    { slot.Kind = SlotKind.Mic; slot.Failures = 0; slot.NextAttemptTick = 0; Warning?.Invoke("System audio unavailable; switching to the microphone."); }
                }
            }
        }
    }

    private void MarkDirty(SlotKind? only = null)
    {
        foreach (var s in _slots) if (only == null || s.Kind == only) s.Dirty = true;
        try { _signal.Release(); } catch (SemaphoreFullException) { }
    }

    // IMMNotificationClient: runs on a COM thread, so only flag + signal (never touch devices here)
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => MarkDirty(flow == DataFlow.Capture ? SlotKind.Mic : flow == DataFlow.Render ? SlotKind.System : null);
    public void OnDeviceRemoved(string deviceId) => MarkDirty();
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => MarkDirty();
    public void OnDeviceAdded(string pwstrDeviceId) { if (_slots.Any(s => s.Cap == null)) MarkDirty(); }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    // ---------------------------------------------------------------- pump
    private async Task Pump(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long produced = 0;
        var slots = _slots.ToArray();          // snapshot: the layout is fixed for the session
        var assembler = new FrameAssembler(slots.Length, FrameSamples, MaxLatencySamples);
        var pcm = new short[FrameSamples * slots.Length];
        var rings = new SpscFloatRing[slots.Length];
        while (!ct.IsCancellationRequested)
        {
            long due = sw.ElapsedMilliseconds * Rate / 1000 - produced * FrameSamples;
            if (due < FrameSamples) { try { await Task.Delay(5, ct); } catch (OperationCanceledException) { return; } continue; }
            for (int c = 0; c < slots.Length; c++) rings[c] = slots[c].Ring;      // current device generation
            // Wall-clock cadence + zero-fill: a silent WASAPI loopback (no DataAvailable) still yields silence frames,
            // so the tail of the last sentence is flushed by endpointing instead of being stranded in the ring.
            float rms = assembler.Assemble(rings, pcm);
            produced++;
            Level?.Invoke(rms);
            if (NoiseGateEnabled && !_gate.Process(rms)) continue;
            var bytes = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
            PcmChunk?.Invoke(bytes);
        }
    }
}
