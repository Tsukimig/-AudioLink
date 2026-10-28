using AudioLink.Core;
using AudioLink.Network;

namespace AudioLink.Audio;

public sealed record LatencyProfile(string Id, string Name, string Hint, int OutputBufferMs, int SafetyMs)
{
    public static readonly LatencyProfile[] All =
    [new("low", "低延迟", "自动最小缓冲", 0, 8), new("balanced", "均衡", "20 ms 输出缓冲", 20, 38), new("safe", "高稳定", "40 ms 输出缓冲", 40, 75)];
}
public sealed record RouteRequest(string DeviceId, RouteControls Controls);
public sealed record EndpointStatistics(string DeviceId, NativeFormat Format, double OutputBufferMs, double DriverLatencyMs, int PaddingFrames, long RenderedFrames, long DriverUnderruns, RenderStatistics Audio, double PeriodMs, string Backend, double OutputQueueMs);
public sealed record EngineStatistics(bool Running, float SourcePeak, long CapturedFrames, long CaptureDiscontinuities, double TargetLatencyMs, IReadOnlyList<EndpointStatistics> Outputs, double CapturePeriodMs, double CaptureDeliveryLagMs);

public sealed class RoutingEngine : IAsyncDisposable
{
    private sealed record OutputRegistration(AudioWorker Worker, CancellationTokenSource Stop);
    private readonly PhoneServer? _phones;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _outputsGate = new();
    private readonly Dictionary<string, OutputRegistration> _outputs = new(StringComparer.Ordinal);
    private CancellationTokenSource? _session;
    private CaptureWorker? _capture;
    private RenderWorker? _keepAlive;
    private string? _sourceId, _profileId;
    private double _commonLatencyMs;
    private int _safetyMs;
    private double _capturePeriodMs = 10;
    private double _captureLagMs;
    public event Action<string, Exception>? DeviceFaulted;
    public bool SourceHealthy => _capture?.IsAlive == true && _keepAlive?.IsAlive == true;
    public RoutingEngine(PhoneServer? phones = null) => _phones = phones;

    public async Task ApplyAsync(string sourceId, IReadOnlyList<RouteRequest> routes, LatencyProfile profile)
    {
        if (routes.Any(x => x.DeviceId == sourceId)) throw new InvalidOperationException("音源设备不能同时作为共享目标，以免产生音频回路。");
        if (routes.Select(x => x.DeviceId).Distinct(StringComparer.Ordinal).Count() != routes.Count) throw new InvalidOperationException("共享目标不能重复。");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sourceId != sourceId || _profileId != profile.Id || !SourceHealthy)
            {
                await StopInternalAsync().ConfigureAwait(false);
                _session = new();
                _sourceId = sourceId; _profileId = profile.Id; _safetyMs = profile.SafetyMs;
                bool lowLatency = profile.Id == "low";
                // Start the silent render client first so the capture endpoint can
                // follow the smaller shared engine period negotiated by WASAPI 3.
                _keepAlive = new(sourceId, null, new(), profile.OutputBufferMs, 0, () => 0, _session.Token, lowLatency: lowLatency);
                _keepAlive.Faulted += OnFault;
                _keepAlive.Start();
                await _keepAlive.Ready.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                _capture = new(sourceId, _session.Token, lowLatency);
                _capture.Faulted += OnFault;
                _capture.Start();
                await _capture.Ready.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                await Task.Delay(100, _session.Token).ConfigureAwait(false);
                _capturePeriodMs = Math.Max(_capture.QuantumMilliseconds, _keepAlive.PeriodMilliseconds);
                _captureLagMs = _capture.DeliveryLagMilliseconds;
            }
            List<OutputRegistration> removals;
            lock (_outputsGate)
            {
                string[] ids = _outputs.Where(x => !routes.Any(r => r.DeviceId == x.Key) || !x.Value.Worker.IsAlive).Select(x => x.Key).ToArray();
                removals = ids.Select(x => _outputs[x]).ToList();
                foreach (string id in ids) _outputs.Remove(id);
            }
            foreach (OutputRegistration registration in removals) await StopOutputAsync(registration).ConfigureAwait(false);
            foreach (RouteRequest route in routes)
            {
                lock (_outputsGate) { if (_outputs.ContainsKey(route.DeviceId)) continue; }
                var stop = CancellationTokenSource.CreateLinkedTokenSource(_session!.Token);
                AudioWorker worker;
                if (route.DeviceId.StartsWith("android:", StringComparison.Ordinal))
                {
                    PhonePeer? phone = _phones?.Find(route.DeviceId);
                    if (phone is null) { stop.Dispose(); continue; }
                    worker = new PhoneRenderWorker(phone, _capture!.Timeline, route.Controls, _capture.Format.ChannelMask, () => Volatile.Read(ref _commonLatencyMs), stop.Token);
                }
                else worker = new RenderWorker(route.DeviceId, _capture!.Timeline, route.Controls, profile.OutputBufferMs, _capture.Format.ChannelMask, () => Volatile.Read(ref _commonLatencyMs), stop.Token, lowLatency: profile.Id == "low");
                worker.Faulted += OnFault;
                var registration = new OutputRegistration(worker, stop);
                lock (_outputsGate) _outputs.Add(route.DeviceId, registration);
                try
                {
                    worker.Start();
                    await worker.Ready.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    lock (_outputsGate) _outputs.Remove(route.DeviceId);
                    await StopOutputAsync(registration).ConfigureAwait(false);
                    OnFault(route.DeviceId, ex);
                }
                UpdateLatency();
            }
            UpdateLatency();
        }
        catch
        {
            await StopInternalAsync().ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    public EngineStatistics GetStatistics()
    {
        List<EndpointStatistics> outputs;
        lock (_outputsGate)
            outputs = _outputs.Values.Where(x => x.Worker.IsAlive).Select(x => x.Worker.OutputStatistics).OfType<EndpointStatistics>().ToList();
        return new(SourceHealthy, _capture?.Timeline?.Peak ?? 0, _capture?.Frames ?? 0, _capture?.Discontinuities ?? 0, Volatile.Read(ref _commonLatencyMs), outputs, _capturePeriodMs, _captureLagMs);
    }
    private void UpdateLatency()
    {
        double maximum;
        lock (_outputsGate) maximum = _outputs.Values.Where(x => x.Worker.IsAlive).Select(x => x.Worker.OutputStatistics).OfType<EndpointStatistics>().Select(x => x.OutputQueueMs + x.DriverLatencyMs).DefaultIfEmpty(0).Max();
        Volatile.Write(ref _commonLatencyMs, LatencyBudget.Calculate(maximum, 0, _capturePeriodMs, _safetyMs, _capture?.Format.SampleRate ?? 48000, _captureLagMs));
    }
    private void OnFault(string id, Exception ex) => DeviceFaulted?.Invoke(id, ex);
    private static async Task StopOutputAsync(OutputRegistration registration)
    {
        registration.Stop.Cancel();
        await registration.Worker.Completion.ConfigureAwait(false);
        registration.Stop.Dispose();
    }
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopInternalAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private async Task StopInternalAsync()
    {
        _session?.Cancel();
        List<OutputRegistration> outputs;
        lock (_outputsGate) { outputs = _outputs.Values.ToList(); _outputs.Clear(); }
        foreach (OutputRegistration output in outputs) await StopOutputAsync(output).ConfigureAwait(false);
        if (_keepAlive is not null) await _keepAlive.Completion.ConfigureAwait(false);
        if (_capture is not null) await _capture.Completion.ConfigureAwait(false);
        _capture = null; _keepAlive = null; _sourceId = null; _profileId = null;
        _session?.Dispose(); _session = null;
        Volatile.Write(ref _commonLatencyMs, 0);
    }
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); GC.SuppressFinalize(this); }
}
