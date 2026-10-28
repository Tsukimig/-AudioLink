using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;
using AudioLink.Services;

namespace AudioLink.Audio;

public readonly record struct EndpointVolumeState(float Volume, bool Muted);

/// <summary>Device volume, including audio sent directly by other players.</summary>
public sealed class EndpointVolumeController : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private readonly AudioEndpointVolume _volume;
    private volatile bool _disposed;
    public string DeviceId { get; }
    public event Action<EndpointVolumeState>? Changed;

    public EndpointVolumeController(string deviceId)
    {
        DeviceId = deviceId;
        _enumerator = new();
        try
        {
            _device = _enumerator.GetDevice(deviceId);
            try { _volume = _device.AudioEndpointVolume; }
            catch { _device.Dispose(); throw; }
            _volume.OnVolumeNotification += OnNotification;
        }
        catch { _enumerator.Dispose(); throw; }
    }
    public EndpointVolumeState Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new(_volume.MasterVolumeLevelScalar, _volume.Mute);
    }
    public void SetVolume(float value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _volume.MasterVolumeLevelScalar = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    }
    public void SetMute(bool value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _volume.Mute = value;
    }
    public void Restore(EndpointVolumeState state)
    {
        SetVolume(state.Volume);
        SetMute(state.Muted);
    }
    private void OnNotification(AudioVolumeNotificationData notification)
    {
        if (!_disposed) Changed?.Invoke(new(notification.MasterVolume, notification.Muted));
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _volume.OnVolumeNotification -= OnNotification;
        try { _device.Dispose(); }
        catch (COMException ex) { AppLog.Write("释放设备音量接口", ex); }
        finally { _enumerator.Dispose(); }
        GC.SuppressFinalize(this);
    }
}
