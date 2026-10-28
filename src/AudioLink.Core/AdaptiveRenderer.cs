namespace AudioLink.Core;

public readonly record struct RenderStatistics(double BufferMilliseconds, double CorrectionPpm, long Underruns, long Resyncs, float Peak, bool Primed);

/// <summary>
/// Each endpoint follows a common QPC timeline. A slow PI controller compensates independent
/// device oscillators; bounded buffers and gentle fades recover from discontinuities.
/// </summary>
public sealed class AdaptiveRenderer
{
    private readonly AudioTimelineBuffer _timeline;
    private readonly SincKernel _kernel;
    private readonly ChannelMatrix _matrix;
    private readonly float[] _window;
    private readonly float[] _lastFrame;
    private readonly int _outputRate;
    private readonly int _outputChannels;
    private readonly double _nominalRatio;
    private double _cursor, _ratio, _filteredError, _integral;
    private float _gain;
    private int _epoch = -1;
    private int _lastDelay = -1;
    private int _fadeRemaining;
    private bool _primed, _starving;
    private long _underruns, _resyncs;
    private readonly object _statisticsGate = new();
    private RenderStatistics _statistics;

    public RouteControls Controls { get; }
    public RenderStatistics Statistics { get { lock (_statisticsGate) return _statistics; } }

    public AdaptiveRenderer(AudioTimelineBuffer timeline, int outputRate, int outputChannels, int maxOutputFrames, RouteControls controls, uint sourceMask = 0, uint targetMask = 0)
    {
        if (outputRate <= 0 || outputChannels is < 1 or > 32 || maxOutputFrames <= 0) throw new ArgumentOutOfRangeException(nameof(outputRate));
        _timeline = timeline;
        _outputRate = outputRate;
        _outputChannels = outputChannels;
        _nominalRatio = _ratio = (double)timeline.SampleRate / outputRate;
        _kernel = SincKernel.ForRates(timeline.SampleRate, outputRate);
        _matrix = new(timeline.Channels, outputChannels, sourceMask, targetMask);
        _window = new float[(int)Math.Ceiling(maxOutputFrames * _nominalRatio * 1.01 + SincKernel.Taps + 16) * timeline.Channels];
        _lastFrame = new float[outputChannels];
        Controls = controls;
    }

    public void Render(Span<float> output, double now100ns, int paddingFrames, double driverLatencyMs, double commonLatencyMs)
    {
        if (output.Length % _outputChannels != 0) throw new ArgumentException("PCM must contain complete frames.", nameof(output));
        int frames = output.Length / _outputChannels;
        if (frames == 0) return;
        TimelineSnapshot snapshot = _timeline.Snapshot();
        double elapsed = Math.Clamp((now100ns - snapshot.EndTime100ns) / 10_000_000, -0.02, 0.1);
        double sourceNow = snapshot.WriteFrame + elapsed * _timeline.SampleRate;
        int delay = Controls.DelayMilliseconds;
        double desired = sourceNow + (paddingFrames / (double)_outputRate + (driverLatencyMs - commonLatencyMs - delay) / 1000) * _timeline.SampleRate;
        bool reset = _epoch != snapshot.Epoch || _lastDelay != delay;

        if (reset || !_primed || Math.Abs(desired - _cursor) > _timeline.SampleRate * 0.1)
        {
            if (_primed && !reset) _resyncs++;
            _cursor = desired;
            _ratio = _nominalRatio;
            _filteredError = _integral = 0;
            _fadeRemaining = Math.Max(1, _outputRate / 200);
            _epoch = snapshot.Epoch;
            _lastDelay = delay;
            _primed = false;
        }

        double dt = frames / (double)_outputRate;
        double error = desired - _cursor;
        _filteredError += (error - _filteredError) * (1 - Math.Exp(-dt / 0.8));
        _integral = Math.Clamp(_integral + _filteredError / _timeline.SampleRate * dt * 0.08, -0.002, 0.002);
        double correction = Math.Clamp(_filteredError / _timeline.SampleRate * 0.5 + _integral, -0.003, 0.003);
        double requestedRatio = _nominalRatio * (1 + correction);
        _ratio += (requestedRatio - _ratio) * (1 - Math.Exp(-dt / 0.15));

        long firstFrame = (long)Math.Floor(_cursor) - SincKernel.Half + 1;
        long endFrame = (long)Math.Floor(_cursor + (frames - 1) * _ratio) + SincKernel.Half + 1;
        int sampleCount = checked((int)(endFrame - firstFrame) * _timeline.Channels);
        if (sampleCount > _window.Length) throw new ArgumentException("Render request exceeds configured maximum buffer size.", nameof(output));
        if (firstFrame < 0 || !_timeline.CopyFrames(firstFrame, _window.AsSpan(0, sampleCount), snapshot.Epoch))
        {
            if (_primed && !_starving) _underruns++;
            _starving = true;
            _primed = false;
            FadeToSilence(output, frames);
            SetStatistics(snapshot.WriteFrame, 0, false);
            return;
        }

        _primed = true;
        _starving = false;
        float targetGain = Controls.Gain;
        float gainCoefficient = 1 - MathF.Exp(-1f / (_outputRate * 0.006f));
        float peak = 0;
        Span<float> sourceFrame = stackalloc float[_timeline.Channels];
        Span<float> mixedFrame = stackalloc float[_outputChannels];
        int fadeFrames = Math.Max(1, _outputRate / 200);
        for (int frame = 0; frame < frames; frame++)
        {
            double position = _cursor - firstFrame;
            for (int channel = 0; channel < _timeline.Channels; channel++)
                sourceFrame[channel] = _kernel.Sample(_window.AsSpan(0, sampleCount), _timeline.Channels, channel, position);
            _matrix.Mix(sourceFrame, mixedFrame);
            _gain += (targetGain - _gain) * gainCoefficient;
            float fade = _fadeRemaining > 0 ? 1 - (float)_fadeRemaining-- / fadeFrames : 1;
            for (int channel = 0; channel < _outputChannels; channel++)
            {
                float value = Math.Clamp(mixedFrame[channel] * _gain, -1, 1);
                if (fade < 1) value = _lastFrame[channel] * (1 - fade) + value * fade;
                output[frame * _outputChannels + channel] = value;
                peak = Math.Max(peak, Math.Abs(value));
            }
            _cursor += _ratio;
        }
        output[^_outputChannels..].CopyTo(_lastFrame);
        SetStatistics(snapshot.WriteFrame, peak, true);
    }

    private void FadeToSilence(Span<float> output, int frames)
    {
        int ramp = Math.Min(frames, Math.Max(1, _outputRate / 200));
        output.Clear();
        for (int frame = 0; frame < ramp; frame++)
            for (int channel = 0; channel < _outputChannels; channel++)
                output[frame * _outputChannels + channel] = _lastFrame[channel] * (1 - (frame + 1f) / ramp);
        Array.Clear(_lastFrame);
        _gain = 0;
    }

    private void SetStatistics(long writeFrame, float peak, bool primed)
    {
        var statistics = new RenderStatistics(Math.Max(0, writeFrame - _cursor) * 1000 / _timeline.SampleRate, (_ratio / _nominalRatio - 1) * 1_000_000, _underruns, _resyncs, peak, primed);
        lock (_statisticsGate) _statistics = statistics;
    }
}
