namespace AudioLink.Core;

public readonly record struct TimelineSnapshot(long WriteFrame, long EarliestFrame, double EndTime100ns, int Epoch);

/// <summary>A bounded, interleaved PCM timeline. Copy under a short lock; DSP runs outside it.</summary>
public sealed class AudioTimelineBuffer
{
    private readonly object _gate = new();
    private readonly float[] _samples;
    private long _writeFrame;
    private double _endTime100ns;
    private int _epoch;
    private float _peak;

    public int SampleRate { get; }
    public int Channels { get; }
    public int CapacityFrames { get; }
    public float Peak => Volatile.Read(ref _peak);

    public AudioTimelineBuffer(int sampleRate, int channels, int capacityMilliseconds = 2000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        if (capacityMilliseconds < 500) throw new ArgumentOutOfRangeException(nameof(capacityMilliseconds));
        SampleRate = sampleRate;
        Channels = channels;
        CapacityFrames = checked(sampleRate * capacityMilliseconds / 1000);
        _samples = new float[checked(CapacityFrames * channels)];
    }

    public void Write(ReadOnlySpan<float> data, double firstFrameTime100ns, bool discontinuity = false)
    {
        if (data.Length % Channels != 0) throw new ArgumentException("PCM must contain complete frames.", nameof(data));
        int frames = data.Length / Channels;
        if (frames == 0) return;
        float peak = 0;
        foreach (float sample in data) peak = Math.Max(peak, Math.Abs(sample));
        lock (_gate)
        {
            if (discontinuity)
            {
                _writeFrame = 0;
                _epoch++;
            }
            int skip = Math.Max(0, frames - CapacityFrames);
            long startFrame = _writeFrame + skip;
            ReadOnlySpan<float> input = data[(skip * Channels)..];
            int index = (int)(startFrame % CapacityFrames) * Channels;
            int first = Math.Min(input.Length, _samples.Length - index);
            input[..first].CopyTo(_samples.AsSpan(index));
            input[first..].CopyTo(_samples);
            _writeFrame += frames;
            _endTime100ns = firstFrameTime100ns + frames * 10_000_000d / SampleRate;
        }
        Volatile.Write(ref _peak, peak);
    }

    public TimelineSnapshot Snapshot()
    {
        lock (_gate) return new(_writeFrame, Math.Max(0, _writeFrame - CapacityFrames), _endTime100ns, _epoch);
    }

    public bool CopyFrames(long startFrame, Span<float> destination, int expectedEpoch)
    {
        if (destination.Length % Channels != 0) throw new ArgumentException("PCM must contain complete frames.", nameof(destination));
        lock (_gate)
        {
            int frames = destination.Length / Channels;
            if (_epoch != expectedEpoch || startFrame < Math.Max(0, _writeFrame - CapacityFrames) || startFrame + frames > _writeFrame)
                return false;
            int index = (int)(startFrame % CapacityFrames) * Channels;
            int first = Math.Min(destination.Length, _samples.Length - index);
            _samples.AsSpan(index, first).CopyTo(destination);
            _samples.AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
            return true;
        }
    }
}
