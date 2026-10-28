using AudioLink.Core;
using AudioLink.Network;

var tests = new (string Name, Action Test)[]
{
    ("Bounded timeline wraps without corrupting channels", TimelineWrap),
    ("Discontinuities invalidate old consumer snapshots", Discontinuity),
    ("PCM 16/24/32-bit and float preserve sign and full scale", PcmRoundtrip),
    ("Channel mapping preserves stereo and keeps surround headroom", Channels),
    ("44.1/48/96 kHz conversion keeps a 1 kHz signal clean", Resampling),
    ("Downsampling suppresses out-of-band aliases", AliasRejection),
    ("Fast endpoint clock converges without buffer growth", () => Drift(900)),
    ("Slow endpoint clock converges without buffer growth", () => Drift(-900)),
    ("Independent volume, mute and silence recovery", Controls),
    ("Source sync delay shifts audio without shifting another route", SourceDelay),
    ("Eight outputs share one timeline with independent rates and gain", FanOut),
    ("Concurrent producer and consumers preserve complete frames", ConcurrentTimeline),
    ("Latency budget follows the source period and preserves sinc lookahead", Budget),
    ("Phone packet headers reject truncated and oversized audio", PhonePackets),
    ("Phone feedback follows fast and slow DAC clocks", PhoneClocks),
    ("Phone pairing, malformed feedback and reconnect preserve isolation", NetworkTests.Pairing)
};
int failures = 0;
var watch = System.Diagnostics.Stopwatch.StartNew();
foreach (var test in tests)
{
    try { test.Test(); Console.WriteLine($"PASS  {test.Name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL  {test.Name}\n      {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed in {watch.Elapsed.TotalSeconds:0.00}s");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Near(double actual, double expected, double tolerance, string label) => Check(Math.Abs(actual - expected) <= tolerance, $"{label}: got {actual}, expected {expected} ± {tolerance}");

static void TimelineWrap()
{
    var timeline = new AudioTimelineBuffer(1000, 2, 500);
    for (int block = 0; block < 10; block++)
    {
        var input = new float[160];
        for (int frame = 0; frame < 80; frame++) { input[frame * 2] = block * 80 + frame; input[frame * 2 + 1] = -input[frame * 2]; }
        timeline.Write(input, block * 800_000d);
    }
    TimelineSnapshot snapshot = timeline.Snapshot();
    Check(snapshot.WriteFrame == 800 && snapshot.EarliestFrame == 300, "Timeline grew beyond capacity.");
    var data = new float[1000];
    Check(timeline.CopyFrames(300, data, snapshot.Epoch), "Wrapped range is missing.");
    for (int frame = 0; frame < 500; frame++) { Near(data[frame * 2], 300 + frame, 0, "Left"); Near(data[frame * 2 + 1], -300 - frame, 0, "Right"); }
    Check(!timeline.CopyFrames(299, new float[2], snapshot.Epoch), "Overwritten frame was exposed.");
    Near(snapshot.EndTime100ns, 8_000_000, 0.1, "QPC end timestamp");
}

static void Discontinuity()
{
    var buffer = new AudioTimelineBuffer(48000, 1);
    buffer.Write(new float[480], 0);
    TimelineSnapshot old = buffer.Snapshot();
    buffer.Write(new float[480], 100_000, true);
    Check(!buffer.CopyFrames(0, new float[100], old.Epoch), "Old epoch survived a discontinuity.");
    Check(buffer.Snapshot().WriteFrame == 480, "Discontinuous data retained a stale cursor.");
}

static void PcmRoundtrip()
{
    float[] input = [-1, -0.75f, -0.12345f, 0, 0.12345f, 0.75f, 1];
    foreach (int bits in new[] { 16, 24, 32 })
    {
        var bytes = new byte[input.Length * bits / 8];
        var output = new float[input.Length];
        PcmConverter.Encode(input, bytes, bits, false); PcmConverter.Decode(bytes, output, bits, false);
        for (int i = 0; i < input.Length; i++) Near(output[i], input[i], bits == 16 ? 0.00004 : 0.000001, $"PCM {bits}");
    }
    var floating = new byte[input.Length * 4];
    var floats = new float[input.Length];
    PcmConverter.Encode(input, floating, 32, true); PcmConverter.Decode(floating, floats, 32, true);
    Check(floats.SequenceEqual(input), "Float path changed values.");
}

static void SourceDelay()
{
    var timeline = new AudioTimelineBuffer(48000, 1);
    var input = new float[96000];input[48000] = 0.8f;timeline.Write(input, 0);
    var direct = new AdaptiveRenderer(timeline, 48000, 1, 20000, new());
    var delayed = new AdaptiveRenderer(timeline, 48000, 1, 20000, new() { DelayMilliseconds = 75 });
    var before = new float[20000];var after = new float[20000];
    direct.Render(before, 20_000_000, 0, 0, 1200);
    delayed.Render(after, 20_000_000, 0, 0, 1200);
    int Peak(float[] data) => Enumerable.Range(0, data.Length).MaxBy(i => Math.Abs(data[i]));
    Near(Peak(after) - Peak(before), 3600, 1, "75 ms source delay in frames");
    Check(direct.Statistics.Primed && delayed.Statistics.Primed && direct.Statistics.Underruns == 0 && delayed.Statistics.Underruns == 0, "Source delay starved another route.");
    Near(before[Peak(before)], after[Peak(after)], 1e-5, "Delayed impulse gain");
}

static void Channels()
{
    var stereo = new ChannelMatrix(2, 2); var result = new float[2];
    stereo.Mix(new float[] { 0.7f, -0.5f }, result); Near(result[0], 0.7, 1e-6, "Stereo L"); Near(result[1], -0.5, 1e-6, "Stereo R");
    var surround = new ChannelMatrix(6, 2, 0x3F); surround.Mix(Enumerable.Repeat(1f, 6).ToArray(), result);
    Check(result.All(x => Math.Abs(x) <= 1.000001), "Surround downmix clipped.");
    var mono = new ChannelMatrix(2, 1); var single = new float[1]; mono.Mix(new float[] { 1, -1 }, single); Near(single[0], 0, 1e-6, "Mono cancellation");
    var upmix = new ChannelMatrix(2, 6, 3, 0x3F); var six = new float[6]; upmix.Mix(new float[] { 0.5f, -0.5f }, six);
    Check(six[0] == 0.5f && six[1] == -0.5f && six.Skip(2).All(x => x == 0), "Stereo upmix changed speaker positions.");
}

static float[] ConvertTone(int sourceRate, int targetRate, double tone)
{
    var timeline = new AudioTimelineBuffer(sourceRate, 1);
    var input = new float[sourceRate * 2];
    for (int i = 0; i < input.Length; i++) input[i] = (float)(0.5 * Math.Sin(2 * Math.PI * tone * i / sourceRate));
    timeline.Write(input, 0);
    int frames = targetRate / 3;
    var output = new float[frames];
    var renderer = new AdaptiveRenderer(timeline, targetRate, 1, frames, new());
    renderer.Render(output, 20_000_000, 0, 0, 1200);
    Check(renderer.Statistics.Primed, "Renderer failed to prime.");
    return output;
}

static void Resampling()
{
    foreach (var pair in new[] { (44100, 48000), (48000, 44100), (96000, 48000), (48000, 48000) })
    {
        float[] result = ConvertTone(pair.Item1, pair.Item2, 1000);
        double signal = 0, error = 0;
        for (int i = pair.Item2 / 10; i < result.Length; i++)
        {
            double expected = 0.5 * Math.Sin(2 * Math.PI * 1000 * (0.8 + i / (double)pair.Item2));
            signal += expected * expected; error += Math.Pow(result[i] - expected, 2);
        }
        double snr = 10 * Math.Log10(signal / error);
        Console.WriteLine($"      {pair.Item1} -> {pair.Item2}: {snr:0.0} dB signal/error");
        Check(snr > 75, $"Resampling SNR only {snr:0.0} dB.");
    }
}

static void AliasRejection()
{
    float[] result = ConvertTone(96000, 48000, 32000);
    double rms = Math.Sqrt(result.Skip(4800).Average(x => (double)x * x));
    double rejection = 20 * Math.Log10(Math.Max(1e-12, rms / (0.5 / Math.Sqrt(2))));
    Console.WriteLine($"      32 kHz rejection: {rejection:0.0} dB");
    Check(rejection < -60, $"Aliased signal too loud: {rejection} dB");
}

static void Drift(double ppm)
{
    const int rate = 12000, frames = 120;
    var timeline = new AudioTimelineBuffer(rate, 1);
    var renderer = new AdaptiveRenderer(timeline, rate, 1, frames, new());
    var input = new float[frames]; var output = new float[frames];
    double capture = 0.01, render = 0.01;
    double renderPeriod = frames / (rate * (1 + ppm / 1_000_000));
    double totalPpm = 0, minQueue = double.MaxValue, maxQueue = 0; int count = 0;
    long sourceFrame = 0;
    while (Math.Min(capture, render) < 100)
    {
        if (capture <= render)
        {
            for (int i = 0; i < frames; i++) input[i] = (float)(Math.Sin(2 * Math.PI * 997 * (sourceFrame + i) / rate) * 0.2);
            timeline.Write(input, (capture - 0.01) * 10_000_000);
            sourceFrame += frames; capture += 0.01;
        }
        else
        {
            renderer.Render(output, render * 10_000_000, frames, 10, 65);
            if (render > 80)
            {
                RenderStatistics stats = renderer.Statistics;
                totalPpm += stats.CorrectionPpm; count++;
                minQueue = Math.Min(minQueue, stats.BufferMilliseconds); maxQueue = Math.Max(maxQueue, stats.BufferMilliseconds);
            }
            render += renderPeriod;
        }
    }
    RenderStatistics final = renderer.Statistics;
    double average = totalPpm / count;
    Console.WriteLine($"      clock {ppm:+0;-0} ppm: correction {average:0.0} ppm, queue {minQueue:0.0}..{maxQueue:0.0} ms");
    Near(average, -ppm, 100, "Clock correction");
    Check(minQueue > 15 && maxQueue < 90 && maxQueue - minQueue < 15, "Audio queue grew or depleted over time.");
    Check(final.Underruns == 0 && final.Resyncs == 0, $"Clock drift caused {final.Underruns} underruns, {final.Resyncs} resyncs.");
}

static void Controls()
{
    var timeline = new AudioTimelineBuffer(48000, 1);
    var data = Enumerable.Repeat(0.5f, 48000 * 2).ToArray(); timeline.Write(data, 0);
    var controls = new RouteControls { Volume = 0.4f, MasterVolume = 0.5f };
    var renderer = new AdaptiveRenderer(timeline, 48000, 1, 9600, controls);
    var block = new float[9600]; renderer.Render(block, 20_000_000, 0, 0, 1200);
    Near(block[^1], 0.1, 0.00002, "Independent gain");
    controls.Muted = true;
    renderer.Render(block, 22_000_000, 0, 0, 1200);
    Check(Math.Abs(block[^1]) < 0.00001, "Mute did not settle to silence.");
    timeline.Write(new float[48000], 20_000_000, true);
    renderer.Render(block, 30_000_000, 0, 0, 1200);
    Check(block.All(float.IsFinite), "Recovery produced invalid samples.");
    controls.DelayMilliseconds = 999; Check(controls.DelayMilliseconds == 500, "Delay not bounded.");
}

static void ConcurrentTimeline()
{
    var timeline = new AudioTimelineBuffer(48000, 2);
    int mismatches = 0;
    var writer = Task.Run(() =>
    {
        var data = new float[480 * 2];
        for (int packet = 0; packet < 5000; packet++)
        {
            for (int frame = 0; frame < 480; frame++) { data[frame * 2] = packet * 480 + frame; data[frame * 2 + 1] = -data[frame * 2]; }
            timeline.Write(data, packet * 100_000d);
        }
    });
    var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
    {
        var data = new float[960];
        do
        {
            TimelineSnapshot snapshot = timeline.Snapshot();
            if (snapshot.WriteFrame >= 480 && timeline.CopyFrames(snapshot.WriteFrame - 480, data, snapshot.Epoch))
                for (int frame = 0; frame < 480; frame++) if (data[frame * 2] != -data[frame * 2 + 1]) Interlocked.Increment(ref mismatches);
        } while (!writer.IsCompleted);
    })).ToArray();
    Task.WaitAll(readers.Append(writer).ToArray());
    Check(mismatches == 0, "Consumers observed torn PCM frames.");
}

static void FanOut()
{
    var timeline = new AudioTimelineBuffer(48000, 2);
    var data = new float[48000 * 2 * 2];
    for (int i = 0; i < data.Length; i += 2) { data[i] = 0.5f; data[i + 1] = -0.25f; }
    timeline.Write(data, 0);
    int[] rates = [44100, 48000, 96000, 32000, 48000, 44100, 48000, 96000];
    var renderers = new AdaptiveRenderer[rates.Length];
    for (int i = 0; i < rates.Length; i++)
    {
        var controls = new RouteControls { Volume = (i + 1f) / rates.Length, MasterVolume = 0.9f };
        renderers[i] = new(timeline, rates[i], 2, rates[i] / 5, controls);
    }
    Parallel.For(0, rates.Length, i =>
    {
        var output = new float[rates[i] / 5 * 2];
        renderers[i].Render(output, 20_000_000, 0, 0, 1200);
        Near(output[^2], 0.5 * 0.9 * (i + 1) / rates.Length, 0.00003, $"Output {i} left gain");
        Near(output[^1], -0.25 * 0.9 * (i + 1) / rates.Length, 0.00003, $"Output {i} right gain");
    });
    renderers[0].Controls.Muted = true;
    for (int i = 1; i < rates.Length; i++) Check(!renderers[i].Controls.Muted, "Mute leaked to another output.");
    Check(renderers.All(x => x.Statistics.Primed && x.Statistics.Underruns == 0), "A simultaneous output failed to prime.");
}

static void Budget()
{
    Near(LatencyBudget.Calculate(10, 0, 2.667, 8, 48000), 18.6666667, 1e-5, "Fast source");
    Near(LatencyBudget.Calculate(10, 0, 10, 8, 48000), 22.6666667, 1e-5, "Slow source guard");
    Near(LatencyBudget.Calculate(12, 0, 10, 8, 48000, 6), 30.6666667, 1e-5, "Loopback delivery guard");
    Near(LatencyBudget.Calculate(22, 3, 10, 38, 48000), 63.6666667, 1e-5, "Balanced guard");
    bool rejected = false;
    try { LatencyBudget.Calculate(10, 0, double.NaN, 8, 48000); } catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected, "Invalid driver timing was accepted.");
}

static void PhonePackets()
{
    Guid session = Guid.NewGuid();
    byte[] packet = new byte[PhoneProtocol.HeaderSize + PhoneProtocol.AudioBytes];
    PhoneProtocol.WriteHeader(packet, new(PhonePacketType.Audio, session, uint.MaxValue, 91000, 240, 1));
    Check(PhoneProtocol.TryRead(packet, out var h) && h.Session == session && h.Sequence == uint.MaxValue && h.FramePosition == 91000, "Audio header did not roundtrip.");
    for (int length = 0; length < packet.Length; length++) Check(!PhoneProtocol.TryRead(packet.AsSpan(0, length), out _), "Truncated audio was accepted.");
    Check(!PhoneProtocol.TryRead(packet.Concat(new byte[] { 0 }).ToArray(), out _), "Oversized audio was accepted.");
    packet[38] = 2; Check(!PhoneProtocol.TryRead(packet, out _), "Unknown flags were accepted.");
}

static void PhoneClocks()
{
    foreach (int ppm in new[] { -1200, 1200 })
    {
        var controller = new ReceiverClockController();
        const int target = 2880;
        double queued = target, total = 0; int count = 0;
        for (int tick = 0; tick < 360; tick++)
        {
            int observed = (int)Math.Round(queued + 100 * Math.Sin(tick * 2 * Math.PI / 9));
            double correction = controller.Update(observed, target, 0.5);
            queued += 48000 * 0.5 * (correction - ppm / 1_000_000d);
            Check(queued is > 480 and < 8000, "Phone queue grew or drained during drift correction.");
            if (tick >= 300) { total += correction * 1_000_000; count++; }
        }
        Near(total / count, ppm, 120, "Phone clock correction ppm");
        Console.WriteLine($"      phone clock {ppm:+0;-0} ppm: queue {queued:0} frames, correction {total / count:0} ppm");
    }
}
