using System.Runtime.InteropServices;
using AudioLink.Core;
using NAudio.CoreAudioApi;

namespace AudioLink.Audio;

internal abstract class AudioWorker
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected readonly CancellationToken Token;
    public string DeviceId { get; }
    public Task Ready => _ready.Task;
    public Task Completion => _completed.Task;
    public Exception? Error { get; private set; }
    public bool IsAlive => !Completion.IsCompleted;
    public event Action<string, Exception>? Faulted;
    public virtual EndpointStatistics? OutputStatistics => null;
    protected AudioWorker(string deviceId, CancellationToken token) { DeviceId = deviceId; Token = token; }
    public void Start()
    {
        var thread = new Thread(ThreadMain) { IsBackground = true, Name = $"AudioLink {GetType().Name}", Priority = ThreadPriority.AboveNormal };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }
    protected void SetReady() => _ready.TrySetResult();
    protected abstract void Run();
    private void ThreadMain()
    {
        try
        {
            using var priority = new AudioThreadPriority();
            Token.ThrowIfCancellationRequested();
            Run();
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { _ready.TrySetCanceled(Token); }
        catch (Exception ex)
        {
            Error = ex;
            _ready.TrySetException(ex);
            if (!Token.IsCancellationRequested)
            {
                Services.AppLog.Write($"音频线程 {DeviceId}", ex);
                Faulted?.Invoke(DeviceId, ex);
            }
        }
        finally
        {
            _ready.TrySetCanceled();
            _completed.TrySetResult();
        }
    }
    protected static void StopSafely(AudioClient client)
    {
        try { client.Stop(); } catch (COMException) { }
    }
}

internal sealed class CaptureWorker : AudioWorker
{
    private long _frames, _discontinuities;
    private readonly bool _lowLatency;
    private double _quantumMilliseconds = 10;
    private double _deliveryLagMs;
    public AudioTimelineBuffer Timeline { get; private set; } = null!;
    public NativeFormat Format { get; private set; }
    public long Frames => Interlocked.Read(ref _frames);
    public long Discontinuities => Interlocked.Read(ref _discontinuities);
    public double QuantumMilliseconds => Volatile.Read(ref _quantumMilliseconds);
    public double DeliveryLagMilliseconds => Volatile.Read(ref _deliveryLagMs);
    public CaptureWorker(string deviceId, CancellationToken token, bool lowLatency = false) : base(deviceId, token) => _lowLatency = lowLatency;
    protected override void Run()
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = enumerator.GetDevice(DeviceId);
        using AudioClient client = device.AudioClient;
        var waveFormat = client.MixFormat;
        Format = NativeFormat.From(waveFormat);
        Timeline = new(Format.SampleRate, Format.Channels);
        using var dataEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback,
            _lowLatency ? 0 : 200_000, 0, waveFormat, Guid.Empty);
        client.SetEventHandle(dataEvent.SafeWaitHandle.DangerousGetHandle());
        AudioCaptureClient capture = client.AudioCaptureClient;
        byte[] bytes = new byte[client.BufferSize * Format.BlockAlign];
        float[] samples = new float[client.BufferSize * Format.Channels];
        WaitHandle[] handles = [Token.WaitHandle, dataEvent];
        client.Start();
        SetReady();
        try
        {
            while (!Token.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(handles, 100) == 0) break;
                while (capture.GetNextPacketSize() > 0)
                {
                    IntPtr pointer = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags, out _, out long qpc);
                    try
                    {
                        int sampleCount = frames * Format.Channels;
                        if (frames > 0) Volatile.Write(ref _quantumMilliseconds, frames * 1000d / Format.SampleRate);
                        int byteCount = frames * Format.BlockAlign;
                        // A driver may coalesce packets after a scheduling stall.
                        if (bytes.Length < byteCount) Array.Resize(ref bytes, byteCount);
                        if (samples.Length < sampleCount) Array.Resize(ref samples, sampleCount);
                        if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(samples, 0, sampleCount);
                        else
                        {
                            Marshal.Copy(pointer, bytes, 0, byteCount);
                            PcmConverter.Decode(bytes.AsSpan(0, byteCount), samples.AsSpan(0, sampleCount), Format.Bits, Format.IsFloat);
                        }
                        bool discontinuity = (flags & AudioClientBufferFlags.DataDiscontinuity) != 0 && Frames > 0;
                        if (discontinuity) Interlocked.Increment(ref _discontinuities);
                        double timestamp = (flags & AudioClientBufferFlags.TimestampError) != 0 || qpc <= 0
                            ? AudioClock.Now100ns - frames * 10_000_000d / Format.SampleRate : qpc;
                        double lag = Math.Clamp((AudioClock.Now100ns - timestamp) / 10_000 - frames * 1000d / Format.SampleRate, 0, 100);
                        // Ignore the first packets while the native clients are starting.
                        if (Frames > Format.SampleRate / 50) Volatile.Write(ref _deliveryLagMs, Math.Max(lag, DeliveryLagMilliseconds));
                        Timeline.Write(samples.AsSpan(0, sampleCount), timestamp, discontinuity);
                        Interlocked.Add(ref _frames, frames);
                    }
                    finally { capture.ReleaseBuffer(frames); }
                }
            }
        }
        finally { StopSafely(client); }
    }
}

internal sealed class RenderWorker : AudioWorker
{
    private readonly AudioTimelineBuffer? _timeline;
    private readonly RouteControls _controls;
    private readonly int _bufferMs;
    private readonly uint _sourceMask;
    private readonly Func<double> _commonLatency;
    private readonly bool _testTone;
    private readonly bool _lowLatency;
    private double _phase;
    private long _renderedFrames;
    private long _driverUnderruns;
    private int _lastPadding;
    public NativeFormat Format { get; private set; }
    public double BufferMilliseconds { get; private set; }
    public double QueueMilliseconds { get; private set; }
    public double DriverLatencyMs { get; private set; }
    public double PeriodMilliseconds { get; private set; }
    public string Backend { get; private set; } = "WASAPI";
    public AdaptiveRenderer? Renderer { get; private set; }
    public int LastPadding => Volatile.Read(ref _lastPadding);
    public long RenderedFrames => Interlocked.Read(ref _renderedFrames);
    public long DriverUnderruns => Interlocked.Read(ref _driverUnderruns);
    public override EndpointStatistics? OutputStatistics => Renderer is null ? null : new(DeviceId, Format, BufferMilliseconds, DriverLatencyMs, LastPadding, RenderedFrames, DriverUnderruns, Renderer.Statistics, PeriodMilliseconds, Backend, QueueMilliseconds);

    public RenderWorker(string deviceId, AudioTimelineBuffer? timeline, RouteControls controls, int bufferMs, uint sourceMask, Func<double> commonLatency, CancellationToken token, bool testTone = false, bool lowLatency = false)
        : base(deviceId, token)
    {
        _timeline = timeline; _controls = controls; _bufferMs = bufferMs; _sourceMask = sourceMask; _commonLatency = commonLatency; _testTone = testTone;
        _lowLatency = lowLatency;
    }

    protected override void Run()
    {
        using var enumerator = new MMDeviceEnumerator();
        using MMDevice device = enumerator.GetDevice(DeviceId);
        using SharedAudioStream stream = SharedAudioStream.OpenRender(device, _bufferMs, _lowLatency);
        AudioClient client = stream.Client;
        var waveFormat = stream.WaveFormat;
        Format = NativeFormat.From(waveFormat);
        Backend = stream.Backend;
        PeriodMilliseconds = stream.PeriodMilliseconds;
        using var dataEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        client.SetEventHandle(dataEvent.SafeWaitHandle.DangerousGetHandle());
        int capacity = client.BufferSize;
        BufferMilliseconds = capacity * 1000d / Format.SampleRate;
        // The driver may allocate 22 ms even for its minimum period. Fill only
        // one engine period plus 2 ms scheduling slack in low latency mode.
        int queueFrames = _lowLatency ? Math.Min(capacity, Math.Max(1, (int)Math.Ceiling((PeriodMilliseconds + 2) * Format.SampleRate / 1000))) : capacity;
        QueueMilliseconds = queueFrames * 1000d / Format.SampleRate;
        try { DriverLatencyMs = client.StreamLatency / 10_000d; } catch (COMException) { DriverLatencyMs = 0; }
        if (_timeline is not null) Renderer = new(_timeline, Format.SampleRate, Format.Channels, capacity, _controls, _sourceMask, Format.ChannelMask);
        AudioRenderClient render = client.AudioRenderClient;
        byte[] bytes = new byte[capacity * Format.BlockAlign];
        float[] samples = new float[capacity * Format.Channels];
        WaitHandle[] handles = [Token.WaitHandle, dataEvent];

        // Prime with silence. Playback remains live even when the source is idle.
        render.GetBuffer(queueFrames);
        render.ReleaseBuffer(queueFrames, AudioClientBufferFlags.Silent);
        client.Start();
        SetReady();
        bool haveRendered = false;
        double previousSubmit = AudioClock.Now100ns;
        double lateThresholdMs = QueueMilliseconds + PeriodMilliseconds / 2;
        try
        {
            while (!Token.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(handles, 100) == 0) break;
                int padding = client.CurrentPadding;
                int frames = Math.Min(capacity - padding, queueFrames - padding);
                double now = AudioClock.Now100ns;
                Volatile.Write(ref _lastPadding, padding);
                if (frames <= 0) continue;
                if (padding == 0 && haveRendered && (now - previousSubmit) / 10_000 > lateThresholdMs) Interlocked.Increment(ref _driverUnderruns);
                Span<float> block = samples.AsSpan(0, frames * Format.Channels);
                if (Renderer is not null) Renderer.Render(block, now, padding, DriverLatencyMs, _commonLatency());
                else if (_testTone)
                {
                    // Diagnostic-only: -44 dBFS, with a short envelope, never changes system volume.
                    for (int f = 0; f < frames; f++)
                    {
                        float value = (float)(Math.Sin(_phase) * 0.006 * Math.Min(1, RenderedFrames / (Format.SampleRate * 0.03)));
                        for (int c = 0; c < Format.Channels; c++) block[f * Format.Channels + c] = value;
                        _phase += 2 * Math.PI * 997 / Format.SampleRate;
                    }
                }
                else block.Clear();
                IntPtr pointer = render.GetBuffer(frames);
                if (Renderer is null && !_testTone) render.ReleaseBuffer(frames, AudioClientBufferFlags.Silent);
                else
                {
                    PcmConverter.Encode(block, bytes.AsSpan(0, frames * Format.BlockAlign), Format.Bits, Format.IsFloat);
                    Marshal.Copy(bytes, 0, pointer, frames * Format.BlockAlign);
                    render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
                }
                Interlocked.Add(ref _renderedFrames, frames);
                previousSubmit = now;
                haveRendered = true;
            }
        }
        finally { StopSafely(client); }
    }
}
