using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using AudioLink.Audio;
using AudioLink.Network;
using AudioLink.Services;

namespace AudioLink.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly PhoneServer _phones = new();
    private readonly RoutingEngine _engine;
    internal PhoneServer Phones => _phones;
    internal EngineStatistics RoutingStatistics => _engine.GetStatistics();
    private readonly SettingsStore _store;
    private readonly UserSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _meterTimer, _refreshDebounce, _routeDebounce, _saveDebounce, _recoveryTimer;
    private readonly Dictionary<string, OutputDeviceViewModel> _known = new(StringComparer.Ordinal);
    private IReadOnlyList<EndpointInfo> _active = [];
    private DeviceMonitor? _monitor;
    private EndpointInfo? _selectedSource;
    private EndpointInfo? _syncInput;
    private bool _sourceSyncEnabled;
    private DefaultPlaybackLease? _defaultPlayback;
    private LatencyProfile _profile;
    private bool _loading, _refreshing, _applying, _applyAgain, _closing, _requested, _initialized;
    private double _masterVolume;
    private string _status = "正在查找音频设备", _detail = "";
    private string _latency = "—", _elapsed = "00:00", _glitches = "0", _activeCount = "0";
    private bool _running;
    private bool _clearingHistory;
    private string _historyMessage = "";
    private double _peak;
    private readonly Stopwatch _sessionTime = new();
    private DateTime _nextRecovery;
    private int _recoveryFailures;
    public event Action? WaveformTick;
    public event Action? HelpRequested;
    public event Action? PhoneRequested;
    public ObservableCollection<EndpointInfo> Sources { get; } = [];
    public ObservableCollection<EndpointInfo> SyncInputs { get; } = [];
    public ObservableCollection<OutputDeviceViewModel> Outputs { get; } = [];
    public ObservableCollection<HistoryDeviceViewModel> HistoryDevices { get; } = [];
    public LatencyProfile[] Profiles => LatencyProfile.All;
    public RelayCommand ToggleCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ProfileCommand { get; }
    public RelayCommand HelpCommand { get; }
    public RelayCommand PhoneCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand ConfirmHistoryCommand { get; }
    public RelayCommand CancelHistoryCommand { get; }
    public RelayCommand SelectHistoryCommand { get; }

    public MainViewModel(Dispatcher dispatcher) : this(dispatcher, new SettingsStore()) { }
    internal MainViewModel(Dispatcher dispatcher, SettingsStore store)
    {
        _dispatcher = dispatcher;
        _store = store;
        _engine = new(_phones);
        _settings = _store.Load();
        _sourceSyncEnabled = _settings.SourceSyncEnabled;
        _masterVolume = _settings.MasterVolume;
        _profile = Profiles.First(x => x.Id == _settings.ProfileId);
        ToggleCommand = new(_ => Toggle(), () => _initialized && !_closing && (_requested || (SelectedSource?.IsAvailable == true && (SourceSyncEnabled || Outputs.Any(x => x.Selected && x.Available)))));
        RefreshCommand = new(_ => _ = RefreshDevicesAsync(), () => !_refreshing && !_closing);
        ProfileCommand = new(p => { if (p is LatencyProfile profile) Profile = profile; });
        HelpCommand = new(_ => HelpRequested?.Invoke());
        PhoneCommand = new(_ => PhoneRequested?.Invoke());
        ClearHistoryCommand = new(_ => BeginHistorySelection(), () => !_closing && !_refreshing && !IsClearingHistory);
        ConfirmHistoryCommand = new(_ => ConfirmHistoryDeletion(), () => !_closing && !_refreshing && IsClearingHistory && HistoryDevices.Any(x => x.IsSelected && CanForgetHistory(x.Device)));
        CancelHistoryCommand = new(_ => EndHistorySelection(), () => !_closing && IsClearingHistory);
        SelectHistoryCommand = new(_ => SelectHistory(), () => !_closing && !_refreshing && IsClearingHistory && HistoryDevices.Any(x => x.CanDelete));
        _phones.Changed += () => _dispatcher.BeginInvoke(() => { if (!_closing) { RebuildOutputs(); if (_requested) ScheduleRoutes(); } });
        _meterTimer = Timer(33, UpdateMeters, DispatcherPriority.Background);
        _refreshDebounce = Timer(550, () => { _refreshDebounce!.Stop(); _ = RefreshDevicesAsync(); });
        _routeDebounce = Timer(180, () => { _routeDebounce!.Stop(); _ = ApplyRoutingAsync(); });
        _saveDebounce = Timer(650, () => { _saveDebounce!.Stop(); Save(); });
        _recoveryTimer = Timer(3000, Recover, DispatcherPriority.Background);
        _engine.DeviceFaulted += (id, ex) => _dispatcher.BeginInvoke(() => OnDeviceFault(id, ex));
        foreach (DevicePreference saved in _settings.Devices)
            _known[saved.Id] = CreateOutput(new(saved.Id, saved.Name, saved.Format, false, false), saved);
    }

    private DispatcherTimer Timer(int milliseconds, Action tick, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        var timer = new DispatcherTimer(priority, _dispatcher) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { if (!_closing) tick(); };
        return timer;
    }
    public EndpointInfo? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!Set(ref _selectedSource, value)) return;
            if (!_loading) { if (value?.IsVirtual == true) SourceSyncEnabled = false; RebuildSyncInputs(); RebuildOutputs(); SettingsChanged(true); }
            Raise(nameof(CanSynchronizeSource));
        }
    }
    public OutputDeviceViewModel? SourceOutput => SelectedSource is not null && _known.TryGetValue(SelectedSource.Id, out var output) ? output : null;
    public bool CanSynchronizeSource => SelectedSource is { IsAvailable: true, IsVirtual: false };
    public bool SourceSyncEnabled
    {
        get => _sourceSyncEnabled;
        set { if (Set(ref _sourceSyncEnabled, value)) { RebuildOutputs(); SettingsChanged(true); Raise(nameof(SyncInputHint)); } }
    }
    public EndpointInfo? SyncInput
    {
        get => _syncInput;
        set { if (Set(ref _syncInput, value)) { if (!_loading) { RebuildOutputs(); SettingsChanged(true); } Raise(nameof(SyncInputHint)); } }
    }
    public string SyncInputHint => SyncInput is null ? "请选择虚拟中转设备" : !SyncInput.IsAvailable ? "中转设备已断开" : "";
    private string? CaptureDeviceId => SourceSyncEnabled ? SyncInput?.Id : SelectedSource?.Id;
    public LatencyProfile Profile
    {
        get => _profile;
        set { if (Set(ref _profile, value)) { Raise(nameof(IsLow)); Raise(nameof(IsBalanced)); Raise(nameof(IsSafe)); SettingsChanged(true); } }
    }
    public bool IsLow => Profile.Id == "low";
    public bool IsBalanced => Profile.Id == "balanced";
    public bool IsSafe => Profile.Id == "safe";
    public double MasterVolume
    {
        get => _masterVolume;
        set
        {
            if (!Set(ref _masterVolume, Math.Clamp(value, 0, 100))) return;
            foreach (OutputDeviceViewModel output in _known.Values) output.Controls.MasterVolume = (float)(_masterVolume / 100);
            Raise(nameof(MasterVolumeText)); SettingsChanged(false);
        }
    }
    public string MasterVolumeText => $"{MasterVolume:0}%";
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public bool Running { get => _running; private set => Set(ref _running, value); }
    public bool Requested => _requested;
    public bool Busy => _applying;
    internal bool Initialized => _initialized;
    public string ButtonText => _requested ? "停止共享" : "开始共享";
    public string ButtonIcon => _requested ? "\uE71A" : "\uE768";
    public string LatencyText { get => _latency; private set => Set(ref _latency, value); }
    public string ElapsedText { get => _elapsed; private set => Set(ref _elapsed, value); }
    public string GlitchText { get => _glitches; private set => Set(ref _glitches, value); }
    public string ActiveCount { get => _activeCount; private set => Set(ref _activeCount, value); }
    public double SourcePeak { get => _peak; private set => Set(ref _peak, value); }
    public bool HasOutputs => Outputs.Count > 0;
    public string DeviceCountText => $"{Outputs.Count(x => x.Available)} 个可用设备 · {Outputs.Count(x => x.Selected)} 个已选择";
    public bool IsClearingHistory { get => _clearingHistory; private set => Set(ref _clearingHistory, value); }
    public string HistoryMessage { get => _historyMessage; private set => Set(ref _historyMessage, value); }
    public bool HasHistoryDevices => HistoryDevices.Count > 0;
    public string HistorySelectionText => HistoryDevices.Any(x => x.CanDelete) && HistoryDevices.Where(x => x.CanDelete).All(x => x.IsSelected) ? "取消全选" : "全选";

    public async Task InitializeAsync()
    {
        try { DefaultPlaybackLease.RecoverPrevious(); } catch (Exception ex) { AppLog.Write("恢复先前系统音源", ex); }
        await RefreshDevicesAsync();
        try
        {
            _monitor = new();
            _monitor.Changed += () => _dispatcher.BeginInvoke(() => { if (!_closing) { _refreshDebounce.Stop(); _refreshDebounce.Start(); } });
        }
        catch (Exception ex) { AppLog.Write("设备通知不可用，将定期刷新", ex); }
        _initialized = true;
        _meterTimer.Start(); _recoveryTimer.Start();
        ToggleCommand.Notify();
    }

    private async Task RefreshDevicesAsync()
    {
        if (_refreshing || _closing) return;
        _refreshing = true; RefreshCommand.Notify(); NotifyHistoryCommands();
        try
        {
            IReadOnlyList<EndpointInfo> devices = await Task.Run(DeviceCatalog.Enumerate);
            if (_closing) return;
            _active = devices;
            string? desiredId = SelectedSource?.Id ?? _settings.SourceId;
            EndpointInfo? desired = devices.FirstOrDefault(x => x.Id == desiredId);
            if (desired is null && desiredId is not null)
            {
                EndpointInfo? previous = SelectedSource;
                DevicePreference? saved = _settings.Devices.FirstOrDefault(x => x.Id == desiredId);
                desired = new(desiredId, previous?.Name ?? saved?.Name ?? "先前的音源设备", previous?.Format ?? saved?.Format ?? default, false, false);
            }
            desired ??= devices.FirstOrDefault(x => x.IsDefault) ?? devices.FirstOrDefault();
            _loading = true;
            Sources.Clear();
            foreach (EndpointInfo device in devices) Sources.Add(device);
            if (desired?.IsAvailable == false) Sources.Add(desired);
            SelectedSource = desired;
            RebuildSyncInputs();
            if (desired?.IsVirtual == true) _sourceSyncEnabled = false;
            Raise(nameof(SourceSyncEnabled)); Raise(nameof(CanSynchronizeSource));
            _loading = false;
            RebuildOutputs();
            if (!_initialized && !_settings.Devices.Any(x => x.Selected))
            {
                OutputDeviceViewModel? first = Outputs.FirstOrDefault(x => x.Available && !x.Device.IsVirtual) ?? Outputs.FirstOrDefault(x => x.Available);
                if (first is not null) first.Selected = true;
            }
            if (!_requested) SetIdleStatus();
            if (_requested) ScheduleRoutes();
        }
        catch (Exception ex)
        {
            AppLog.Write("刷新音频设备失败", ex);
            Status = "无法读取音频设备"; Detail = Explain(ex);
        }
        finally { _loading = false; _refreshing = false; RefreshCommand.Notify(); ToggleCommand.Notify(); NotifyHistoryCommands(); }
    }

    private OutputDeviceViewModel CreateOutput(EndpointInfo info, DevicePreference? preference) => new(info,
        preference ?? (info.IsPhone ? new DevicePreference { Id = info.Id, Selected = true } : null), MasterVolume, SettingsChanged, _dispatcher);
    private void RebuildSyncInputs()
    {
        bool previousLoading = _loading;_loading = true;
        try
        {
            string? busId = SyncInput?.Id ?? _settings.SyncInputId;
            EndpointInfo? previousBus = SyncInput;
            SyncInputs.Clear();
            foreach (EndpointInfo bus in _active.Where(x => x.IsVirtual && x.Id != SelectedSource?.Id)) SyncInputs.Add(bus);
            EndpointInfo? nextBus = busId is null ? SyncInputs.FirstOrDefault() : SyncInputs.FirstOrDefault(x => x.Id == busId);
            if (nextBus is null && busId is not null)
            {
                nextBus = previousBus is not null ? previousBus with { IsAvailable = false, IsDefault = false } : new(busId, "先前的虚拟中转设备", default, false, false);
                SyncInputs.Add(nextBus);
            }
            SyncInput = nextBus;
        }
        finally { _loading = previousLoading; }
    }
    private void RebuildOutputs()
    {
        var devices = _active.Concat(_phones.Peers.Select(x => new EndpointInfo(x.Id, x.Name, new(48000, 2, 16, false, 3), false, x.IsConnected))).ToArray();
        var available = devices.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (OutputDeviceViewModel output in _known.Values)
            output.UpdateDevice(available.TryGetValue(output.Id, out EndpointInfo? info) ? info : output.Device with { IsAvailable = false, IsDefault = false });
        foreach (EndpointInfo info in devices)
            if (!_known.ContainsKey(info.Id)) _known.Add(info.Id, CreateOutput(info, _settings.Devices.FirstOrDefault(x => x.Id == info.Id)));
        var wanted = _known.Values.Where(x => x.Id != SelectedSource?.Id && (!SourceSyncEnabled || x.Id != SyncInput?.Id) && (x.Available || x.Selected)).OrderByDescending(x => x.Available).ThenBy(x => x.Name, StringComparer.CurrentCulture).ToArray();
        // Preserve row instances and audio controls during device notifications.
        for (int i = Outputs.Count - 1; i >= 0; i--) if (!wanted.Contains(Outputs[i])) Outputs.RemoveAt(i);
        for (int i = 0; i < wanted.Length; i++)
        {
            int existing = Outputs.IndexOf(wanted[i]);
            if (existing < 0) Outputs.Insert(i, wanted[i]);
            else if (existing != i) Outputs.Move(existing, i);
        }
        Raise(nameof(HasOutputs)); Raise(nameof(DeviceCountText)); Raise(nameof(SourceOutput)); ToggleCommand.Notify(); NotifyHistoryCommands();
    }
    private bool CanForgetHistory(OutputDeviceViewModel device) =>
        _known.TryGetValue(device.Id, out OutputDeviceViewModel? known) && ReferenceEquals(known, device) &&
        !device.Available && !device.Connected && device.Id != SelectedSource?.Id && device.Id != SyncInput?.Id &&
        !_phones.Peers.Any(x => x.Id == device.Id && x.IsConnected);
    private void BeginHistorySelection()
    {
        if (_closing || _refreshing || IsClearingHistory) return;
        HistoryMessage = "";
        HistoryDevices.Clear();
        IsClearingHistory = true;
        NotifyHistoryCommands();
    }
    private void EndHistorySelection()
    {
        IsClearingHistory = false;
        HistoryDevices.Clear();
        HistoryMessage = "";
        NotifyHistoryCommands();
    }
    private void RebuildHistoryDevices()
    {
        if (!IsClearingHistory) return;
        var existing = HistoryDevices.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var wanted = _known.Values.Where(x => !x.Available)
            .OrderByDescending(CanForgetHistory).ThenBy(x => x.Name, StringComparer.CurrentCulture)
            .Select(device =>
            {
                if (!existing.TryGetValue(device.Id, out var row)) row = new(device, HistorySelectionChanged);
                row.Update(CanForgetHistory(device), device.Id == SelectedSource?.Id ? "当前音源" : device.Id == SyncInput?.Id ? "中转设备" : device.Connected ? "正在播放" : "已断开");
                return row;
            }).ToArray();
        for (int i = HistoryDevices.Count - 1; i >= 0; i--)
            if (!wanted.Contains(HistoryDevices[i])) HistoryDevices.RemoveAt(i);
        for (int i = 0; i < wanted.Length; i++)
        {
            int index = HistoryDevices.IndexOf(wanted[i]);
            if (index < 0) HistoryDevices.Insert(i, wanted[i]);
            else if (index != i) HistoryDevices.Move(index, i);
        }
        Raise(nameof(HasHistoryDevices));
    }
    private void SelectHistory()
    {
        if (!SelectHistoryCommand.CanExecute(null)) return;
        bool select = !HistoryDevices.Where(x => x.CanDelete).All(x => x.IsSelected);
        foreach (var row in HistoryDevices) row.IsSelected = select;
    }
    private void ConfirmHistoryDeletion()
    {
        if (_closing || _refreshing || !IsClearingHistory) return;
        var selected = HistoryDevices.Where(x => x.IsSelected).Select(x => x.Device).ToArray();
        if (selected.Length == 0) return;
        var removed = new List<OutputDeviceViewModel>();
        foreach (var device in selected)
        {
            // Recheck on confirmation: a phone can reconnect while the list is open.
            if (!CanForgetHistory(device) || (device.Device.IsPhone && !_phones.ForgetDisconnected(device.Id))) continue;
            _known.Remove(device.Id);
            removed.Add(device);
        }
        if (removed.Count == 0)
        {
            RebuildOutputs();
            HistoryMessage = "设备已连接，请重新选择";
            return;
        }
        _saveDebounce.Stop();
        if (!Save())
        {
            // Keep the records available for another attempt if the atomic file write fails.
            foreach (var device in removed) _known.Add(device.Id, device);
            _settings.Devices = _known.Values.Select(x => x.ToPreference()).ToList();
            RebuildOutputs();
            HistoryMessage = "无法保存删除结果，请检查设置目录权限后重试";
            return;
        }
        foreach (var device in removed) device.Dispose();
        EndHistorySelection();
        RebuildOutputs();
        HistoryMessage = $"已删除 {removed.Count} 个设备";
        if (_requested) ScheduleRoutes();
    }
    private void HistorySelectionChanged()
    {
        Raise(nameof(HistorySelectionText));
        ConfirmHistoryCommand.Notify(); SelectHistoryCommand.Notify();
    }
    private void NotifyHistoryCommands()
    {
        RebuildHistoryDevices();
        ClearHistoryCommand.Notify(); CancelHistoryCommand.Notify(); HistorySelectionChanged();
    }
    private void SettingsChanged(bool routing)
    {
        if (_loading || _closing) return;
        _saveDebounce.Stop(); _saveDebounce.Start();
        Raise(nameof(DeviceCountText)); ToggleCommand.Notify();
        if (routing && _requested) ScheduleRoutes();
    }
    private void ScheduleRoutes() { _routeDebounce.Stop(); _routeDebounce.Start(); }
    private void Toggle()
    {
        _requested = !_requested;
        if (_requested) { _sessionTime.Restart(); _recoveryFailures = 0; }
        else _sessionTime.Stop();
        Raise(nameof(Requested)); Raise(nameof(ButtonText)); Raise(nameof(ButtonIcon)); ToggleCommand.Notify();
        _ = ApplyRoutingAsync();
    }

    private async Task ApplyRoutingAsync()
    {
        if (_closing) return;
        if (_applying) { _applyAgain = true; return; }
        _applying = true; Raise(nameof(Busy));
        try
        {
            do
            {
                _applyAgain = false;
                if (!_requested)
                {
                    await _engine.StopAsync(); RestoreDefaultPlayback(); Running = false; SetIdleStatus();
                    continue;
                }
                var routeList = Outputs.Where(x => x.Selected && x.Available).Select(x => new RouteRequest(x.Id, x.Controls)).ToList();
                if (SourceSyncEnabled && CanSynchronizeSource && SourceOutput is { } sourceOutput) routeList.Add(new(sourceOutput.Id, sourceOutput.Controls));
                var routes = routeList.ToArray();
                if (SelectedSource?.IsAvailable != true || routes.Length == 0 || (SourceSyncEnabled && SyncInput?.IsAvailable != true))
                {
                    await _engine.StopAsync(); RestoreDefaultPlayback(); Running = false;
                    Status = SelectedSource?.IsAvailable != true ? "等待音源重新连接" : SourceSyncEnabled && SyncInput?.IsAvailable != true ? "等待虚拟中转设备" : "等待共享设备";
                    Detail = SourceSyncEnabled && SyncInput?.IsAvailable != true ? SyncInputHint : SelectedSource?.IsAvailable != true ? "设备重新连接后将自动恢复，也可以手动选择另一个音源。" : "在右侧启用至少一个设备，声音会自动接入。";
                    continue;
                }
                Status = _engine.SourceHealthy ? "正在更新共享设备" : "正在建立音频连接";
                try
                {
                    if (_defaultPlayback is not null && (!SourceSyncEnabled || _defaultPlayback.BusId != SyncInput?.Id))
                    { await _engine.StopAsync(); RestoreDefaultPlayback(); }
                    // Start capture and all render routes before moving system
                    // audio, so the original endpoint never feeds itself.
                    string captureId = CaptureDeviceId!;
                    await _engine.ApplyAsync(captureId, routes, Profile);
                    if (_closing || !_requested) { await _engine.StopAsync(); RestoreDefaultPlayback(); break; }
                    if (CaptureDeviceId != captureId) { _applyAgain = true; continue; }
                    if (SourceSyncEnabled && _defaultPlayback is null) _defaultPlayback = DefaultPlaybackLease.Acquire(SyncInput!);
                    EngineStatistics stats = _engine.GetStatistics();
                    Running = stats.Running && stats.Outputs.Count > 0;
                    if (Running)
                    {
                        bool complete = stats.Outputs.Count == routes.Length;
                        Status = complete ? "正在共享" : "正在共享 · 部分设备待恢复";
                        Detail = complete ? "" : $"{stats.Outputs.Count} 个输出正在共享，其余设备将自动重试连接。";
                        _recoveryFailures = complete ? 0 : _recoveryFailures + 1;
                    }
                    else { Status = "设备连接失败 · 将自动重试"; Detail = "检查设备连接，或切换到高稳定模式后重试。"; _recoveryFailures++; }
                }
                catch (Exception ex)
                {
                    await _engine.StopAsync(); RestoreDefaultPlayback();
                    Running = false; Status = "连接暂时中断 · 将自动重试"; Detail = Explain(ex); AppLog.Write("共享启动失败", ex);
                    _recoveryFailures++;
                }
                _nextRecovery = DateTime.UtcNow.AddSeconds(Math.Min(30, 3 * Math.Pow(2, Math.Min(4, _recoveryFailures))));
            } while (_applyAgain && !_closing);
        }
        finally { _applying = false; Raise(nameof(Busy)); ToggleCommand.Notify(); }
    }

    private void OnDeviceFault(string id, Exception ex)
    {
        if (_closing) return;
        if (_known.TryGetValue(id, out OutputDeviceViewModel? output)) output.SetError(Explain(ex));
        NotifyHistoryCommands();
        if (CaptureDeviceId == id && _requested) { Running = false; Status = "音源连接中断 · 将自动重试"; Detail = Explain(ex); }
    }
    private int _refreshTicks;
    private void Recover()
    {
        if (++_refreshTicks >= 5) { _refreshTicks = 0; _ = RefreshDevicesAsync(); }
        if (!_requested || _applying || DateTime.UtcNow < _nextRecovery || SelectedSource?.IsAvailable != true) return;
        var active = _engine.GetStatistics().Outputs.Select(x => x.DeviceId).ToHashSet(StringComparer.Ordinal);
        if (!_engine.SourceHealthy || Outputs.Any(x => x.Selected && x.Available && !active.Contains(x.Id)) || (SourceSyncEnabled && SourceOutput is { } source && !active.Contains(source.Id))) ScheduleRoutes();
    }
    private void UpdateMeters()
    {
        EngineStatistics statistics = _engine.GetStatistics();
        SourcePeak = Math.Max(Math.Min(1, statistics.SourcePeak) * 100, SourcePeak * 0.83);
        LatencyText = statistics.Running ? $"{statistics.TargetLatencyMs:0}" : "—";
        ActiveCount = statistics.Outputs.Count.ToString();
        GlitchText = (statistics.CaptureDiscontinuities + statistics.Outputs.Sum(x => x.Audio.Underruns + x.DriverUnderruns)).ToString();
        ElapsedText = _sessionTime.Elapsed.TotalHours >= 1 ? _sessionTime.Elapsed.ToString(@"hh\:mm\:ss") : _sessionTime.Elapsed.ToString(@"mm\:ss");
        bool connectionsChanged = false;
        foreach (OutputDeviceViewModel output in Outputs)
        {
            bool connected = output.Connected;
            output.UpdateStatistics(statistics.Outputs.FirstOrDefault(x => x.DeviceId == output.Id), _requested);
            connectionsChanged |= connected != output.Connected;
        }
        if (connectionsChanged) NotifyHistoryCommands();
        if (SourceSyncEnabled && SourceOutput is { } source) source.UpdateStatistics(statistics.Outputs.FirstOrDefault(x => x.DeviceId == source.Id), _requested);
        WaveformTick?.Invoke();
    }
    private void SetIdleStatus()
    {
        if (_defaultPlayback is not null) { Status = "请恢复 Windows 播放设备"; Detail = "系统输出尚未恢复，请在 Windows 声音设置中选择原播放设备。"; return; }
        Status = SelectedSource is null ? "未找到音频设备" : "准备就绪";
        Detail = SelectedSource is null ? "连接耳机、音箱或音频接口，然后刷新设备。" : "";
    }
    private static string Explain(Exception ex) => ex switch
    {
        TimeoutException => "设备响应超时。请检查连接，尝试高稳定模式。",
        System.Runtime.InteropServices.COMException com when unchecked((uint)com.HResult) == 0x88890004 => "设备已断开或格式已更改，正在等待重新连接。",
        System.Runtime.InteropServices.COMException com when unchecked((uint)com.HResult) == 0x8889000A => "设备被其他程序独占。关闭该程序的独占模式后重试。",
        _ => $"{ex.Message}（0x{ex.HResult:X8}）"
    };
    private bool Save()
    {
        try
        {
            _settings.SourceId = SelectedSource?.Id;
            _settings.SourceSyncEnabled = SourceSyncEnabled;
            _settings.SyncInputId = SyncInput?.Id;
            _settings.ProfileId = Profile.Id;
            _settings.MasterVolume = MasterVolume;
            _settings.Devices = _known.Values.Select(x => x.ToPreference()).ToList();
            _store.Save(_settings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { AppLog.Write("设置保存失败", ex); return false; }
    }
    public async Task ShutdownAsync()
    {
        if (_closing) return;
        _closing = true;
        foreach (DispatcherTimer timer in new[] { _meterTimer, _refreshDebounce, _routeDebounce, _saveDebounce, _recoveryTimer }) timer.Stop();
        _monitor?.Dispose();
        Save();
        try { await _engine.DisposeAsync(); } finally { RestoreDefaultPlayback(); }
        await _phones.DisposeAsync();
        foreach (OutputDeviceViewModel output in _known.Values) output.Dispose();
    }
    private void RestoreDefaultPlayback()
    {
        if (_defaultPlayback is null) return;
        try { _defaultPlayback.Dispose(); _defaultPlayback = null; }
        catch (Exception ex) { AppLog.Write("恢复系统默认输出失败", ex); Detail = "系统输出尚未恢复，请在 Windows 声音设置中选择原播放设备。"; }
    }
}
