namespace AudioLink.Network;

/// <summary>Slow feedback follows the phone's DAC clock without packet-sized drops or duplicates.</summary>
public sealed class ReceiverClockController
{
    private double _filtered, _integral, _correction;
    public double Correction => _correction;
    public void Reset() => _filtered = _integral = _correction = 0;
    public double Update(int queuedFrames, int targetFrames, double elapsedSeconds)
    {
        if (queuedFrames is < 0 or > 48000 || targetFrames is < 240 or > 9600 || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(queuedFrames));
        double dt = Math.Clamp(elapsedSeconds, 0.05, 2);
        double error = (targetFrames - queuedFrames) / 48000d;
        _filtered += (error - _filtered) * (1 - Math.Exp(-dt / 1.5));
        _integral = Math.Clamp(_integral + _filtered * dt * 0.025, -0.0025, 0.0025);
        double requested = Math.Clamp(_filtered * 0.18 + _integral, -0.003, 0.003);
        _correction += (requested - _correction) * (1 - Math.Exp(-dt / 1));
        return _correction;
    }
}
