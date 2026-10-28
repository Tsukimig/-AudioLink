namespace AudioLink.Core;

/// <summary>Atomic controls; never access a view model from an audio thread.</summary>
public sealed class RouteControls
{
    private float _volume = 1;
    private int _muted;
    private int _delayMs;
    private float _master = 1;
    public float Volume { get => Volatile.Read(ref _volume); set => Volatile.Write(ref _volume, float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0); }
    public float MasterVolume { get => Volatile.Read(ref _master); set => Volatile.Write(ref _master, float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0); }
    public bool Muted { get => Volatile.Read(ref _muted) != 0; set => Volatile.Write(ref _muted, value ? 1 : 0); }
    public int DelayMilliseconds { get => Volatile.Read(ref _delayMs); set => Volatile.Write(ref _delayMs, Math.Clamp(value, 0, 500)); }
    public float Gain => Muted ? 0 : Volume * MasterVolume;
}
