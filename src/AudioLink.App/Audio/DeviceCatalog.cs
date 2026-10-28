using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioLink.Audio;

public sealed record EndpointInfo(string Id, string Name, NativeFormat Format, bool IsDefault, bool IsAvailable = true)
{
    public bool IsPhone => Id.StartsWith("android:", StringComparison.Ordinal);
    public bool IsBluetooth => Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) || Name.Contains("蓝牙", StringComparison.OrdinalIgnoreCase);
    public bool IsHeadphones => Name.Contains("耳", StringComparison.OrdinalIgnoreCase) || Name.Contains("Head", StringComparison.OrdinalIgnoreCase);
    public bool IsVirtual => Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || Name.Contains("虚拟", StringComparison.OrdinalIgnoreCase) || Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase);
    public string Kind => IsPhone ? "安卓手机" : IsVirtual ? "虚拟设备" : IsBluetooth ? "蓝牙音频" : IsHeadphones ? "耳机" : Name.Contains("HDMI", StringComparison.OrdinalIgnoreCase) || Name.Contains("Display", StringComparison.OrdinalIgnoreCase) ? "显示器音频" : "扬声器 / 音频接口";
    public string DisplayName => Name + (IsDefault ? "  · 默认" : "") + (IsAvailable ? "" : "  · 已断开");
    public string FormatText => IsAvailable ? Format.Description : "等待设备重新连接";
}

public static class DeviceCatalog
{
    public static IReadOnlyList<EndpointInfo> Enumerate()
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
        {
            using MMDevice defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        var result = new List<EndpointInfo>();
        foreach (MMDevice endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (endpoint)
            {
                try
                {
                    using AudioClient client = endpoint.AudioClient;
                    result.Add(new(endpoint.ID, endpoint.FriendlyName, NativeFormat.From(client.MixFormat), endpoint.ID == defaultId));
                }
                catch (Exception ex) { Services.AppLog.Write($"读取设备 {endpoint.ID}", ex); }
            }
        }
        return result.OrderByDescending(x => x.IsDefault).ThenBy(x => x.Name, StringComparer.CurrentCulture).ToArray();
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class DeviceMonitor : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _disposed;
    public event Action? Changed;
    public DeviceMonitor() => Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(this));
    private void Notify() { if (!_disposed) Changed?.Invoke(); }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Notify();
    public void OnDeviceAdded(string pwstrDeviceId) => Notify();
    public void OnDeviceRemoved(string deviceId) => Notify();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { if (flow == DataFlow.Render) Notify(); }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) => Notify();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enumerator.UnregisterEndpointNotificationCallback(this);
        _enumerator.Dispose();
        GC.SuppressFinalize(this);
    }
}
