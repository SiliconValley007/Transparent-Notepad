namespace TransparentNotepad.Session;

/// <summary>
/// Frame-level voice-activity gate: open above max(-45 dBFS, adaptive noise floor + 8 dB), stays open for a hangover
/// so end-of-speech silence still reaches the ASR (its endpointing needs it), then closes so pure silence is not sent.
/// </summary>
public sealed class NoiseGate
{
    private readonly double _minThresholdDb; private readonly int _hangoverFrames;
    private double _floorDb = -80; private int _hang;
    public NoiseGate(double thresholdDbfs = -45, int hangoverFrames = 15) { _minThresholdDb = thresholdDbfs; _hangoverFrames = hangoverFrames; }

    public double FloorDb => _floorDb;
    public double ThresholdDb => Math.Max(_minThresholdDb, Math.Min(-35, _floorDb + 8));

    /// <summary>rms is linear amplitude (0..1). Returns true if this frame should be transmitted.</summary>
    public bool Process(float rms)
    {
        double db = 20 * Math.Log10(Math.Max(rms, 1e-9));
        // adaptive floor: follows quiet frames down quickly, creeps up slowly, never above -50 dBFS
        _floorDb = db < _floorDb ? db : Math.Min(-50, _floorDb + (db - _floorDb) * 0.001);
        if (db >= ThresholdDb) { _hang = _hangoverFrames; return true; }
        if (_hang > 0) { _hang--; return true; }
        return false;
    }

    public void Reset() { _hang = 0; _floorDb = -80; }
}
