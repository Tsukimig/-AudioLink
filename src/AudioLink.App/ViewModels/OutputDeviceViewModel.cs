using AudioLink.Audio;
using AudioLink.Core;
using AudioLink.Services;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace AudioLink.ViewModels;

public sealed class OutputDeviceViewModel : ObservableObject, IDisposable
{
    private EndpointInfo _device;
    private readonly Action<bool> _changed;
    private bool _selected, _muted;
    private double _volume;
    private int _delay;
    private double _level;
    private string _state = "已就绪", _buffer = "尚未连接";
    private bool _connected, _hasError;
    private readonly Dispatcher? _dispatcher;
    private EndpointVolumeController? _endpointVolume;
    private bool _disposed;
    public OutputDeviceViewModel(EndpointInfo device, DevicePreference? preference, double masterVolume, Action<bool> changed, Dispatcher? dispatcher = null)
    {
        _device = device;
        _changed = changed;
        _dispatcher = dispatcher;
        _selected = preference?.Selected ?? false;
        _volume = preference?.Volume ?? 100;
        _muted = preference?.Muted ?? false;
        _delay = preference?.DelayMs ?? 0;
        Controls = new() { Volume = (float)(_volume / 100), Muted = _muted, DelayMilliseconds = _delay, MasterVolume = (float)(masterVolume / 100) };
        MuteCommand = new(_ => Muted = !Muted);
        ResetDelayCommand = new(_ => DelayMs = 0);
        AttachEndpointVolume();
    }
    public RouteControls Controls { get; }
    public RelayCommand MuteCommand { get; }
    public RelayCommand ResetDelayCommand { get; }
    public string Id => _device.Id;
    public string Name => _device.Name;
    public string Kind => _device.Kind;
    public string Format => _device.FormatText;
    public bool Available => _device.IsAvailable;
    public bool Bluetooth => _device.IsBluetooth;
    public string Icon => _device.IsPhone ? "\uE8EA" : _device.IsHeadphones ? "\uE7F6" : _device.IsVirtual ? "\uE968" : "\uE767";
    public EndpointInfo Device => _device;
    public bool UsesDeviceVolume => _endpointVolume is not null;
    public string VolumeHint => _device.IsPhone ? "调整发送到这部手机的音量。" : UsesDeviceVolume ? "调整此设备的 Windows 音量，包括播放器直接输出的声音。" : "调整 AudioLink 共享流的音量；设备音量接口暂不可用。";
    public string SourceVolumeHint => UsesDeviceVolume ? VolumeHint : "音源设备音量接口暂不可用，请检查连接或在 Windows 中调整。";
    public bool Selected { get => _selected; set { if (Set(ref _selected, value)) _changed(true); } }
    public double Volume
    {
        get => _volume;
        set
        {
            double volume = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100);
            if (Math.Abs(volume - _volume) < 0.00001) return;
            try
            {
                _endpointVolume?.SetVolume((float)(volume / 100));
                if (Set(ref _volume, volume)) { UpdateRouteGain(); Raise(nameof(VolumeText)); _changed(false); }
            }
            catch (COMException ex) { AppLog.Write($"调整设备音量：{Name}", ex); Raise(nameof(Volume)); }
        }
    }
    public string VolumeText => $"{Volume:0}%";
    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value) return;
            try
            {
                _endpointVolume?.SetMute(value);
                if (Set(ref _muted, value)) { UpdateRouteGain(); Raise(nameof(MuteText)); _changed(false); }
            }
            catch (COMException ex) { AppLog.Write($"调整设备静音：{Name}", ex); Raise(nameof(Muted)); }
        }
    }
    public string MuteText => Muted ? "已静音" : "静音";
    public int DelayMs
    {
        get => _delay;
        set { if (Set(ref _delay, Math.Clamp(value, 0, 500))) { Controls.DelayMilliseconds = _delay; _changed(false); } }
    }
    public double Level { get => _level; private set => Set(ref _level, value); }
    public string State { get => _state; private set => Set(ref _state, value); }
    public string BufferText { get => _buffer; private set => Set(ref _buffer, value); }
    public bool Connected { get => _connected; private set => Set(ref _connected, value); }
    public bool HasError { get => _hasError; private set => Set(ref _hasError, value); }
    public void UpdateDevice(EndpointInfo device)
    {
        _device = device;
        AttachEndpointVolume();
        foreach (string property in new[] { nameof(Name), nameof(Kind), nameof(Format), nameof(Available), nameof(Bluetooth), nameof(Icon) }) Raise(property);
    }
    public void SetError(string message)
    {
        HasError = true; Connected = false; State = "连接失败 · 将重试"; BufferText = message; Level = 0;
    }
    public void UpdateStatistics(EndpointStatistics? stats, bool requested)
    {
        Connected = stats is not null;
        if (stats is not null)
        {
            HasError = false;
            State = stats.Audio.Primed ? (Muted ? "静音中" : "共享中") : "缓冲中";
            double audiblePeak = UsesDeviceVolume ? (Muted ? 0 : stats.Audio.Peak * Volume / 100) : stats.Audio.Peak;
            Level = Math.Max(Math.Min(1, audiblePeak) * 100, Level * 0.8);
            BufferText = $"{stats.Backend} · 排队 {stats.OutputQueueMs:0.##} ms / 容量 {stats.OutputBufferMs:0.##} ms · 周期 {stats.PeriodMs:0.##} ms · 校正 {stats.Audio.CorrectionPpm:+0;-0;0} ppm";
        }
        else
        {
            Level *= 0.7;
            if (!Available) { State = "已断开"; BufferText = Selected ? "重连后自动恢复" : "设备不可用"; }
            else if (!HasError || !requested || !Selected) { State = Selected ? "已就绪" : "未启用"; BufferText = "独立音量 · 自动时钟同步"; HasError = false; }
        }
    }
    public DevicePreference ToPreference() => new() { Id = Id, Name = Name, Format = _device.Format, Selected = Selected, Volume = Volume, Muted = Muted, DelayMs = DelayMs };

    private void UpdateRouteGain()
    {
        // Apply volume once. The endpoint owns device gain; master gain still
        // belongs to AudioLink's shared stream and remains independent.
        Controls.Volume = UsesDeviceVolume ? 1 : (float)(_volume / 100);
        Controls.Muted = !UsesDeviceVolume && _muted;
    }
    private void AttachEndpointVolume()
    {
        if (_disposed) return;
        if (!Available)
        {
            _endpointVolume?.Dispose(); _endpointVolume = null;
            UpdateRouteGain(); Raise(nameof(UsesDeviceVolume)); Raise(nameof(VolumeHint)); Raise(nameof(SourceVolumeHint));
            return;
        }
        if (_endpointVolume is not null) return;
        // Layout fixtures and non-UI consumers deliberately use stream controls.
        if (_dispatcher is null || _device.IsPhone) return;
        try
        {
            var controller = new EndpointVolumeController(Id);
            EndpointVolumeState initial;
            try { initial = controller.Read(); } catch { controller.Dispose(); throw; }
            _endpointVolume = controller;
            controller.Changed += state =>
            {
                if (_disposed || _dispatcher.HasShutdownStarted) return;
                _dispatcher.BeginInvoke(() => { if (!_disposed && ReferenceEquals(_endpointVolume, controller)) ApplyEndpointState(state); });
            };
            ApplyEndpointState(initial);
            Raise(nameof(UsesDeviceVolume)); Raise(nameof(VolumeHint)); Raise(nameof(SourceVolumeHint));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        { AppLog.Write($"设备音量接口暂不可用：{Name}", ex); UpdateRouteGain(); }
    }
    private void ApplyEndpointState(EndpointVolumeState state)
    {
        if (Set(ref _volume, (double)state.Volume * 100)) Raise(nameof(VolumeText));
        if (Set(ref _muted, state.Muted)) Raise(nameof(MuteText));
        UpdateRouteGain();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _endpointVolume?.Dispose(); _endpointVolume = null;
        GC.SuppressFinalize(this);
    }
}
