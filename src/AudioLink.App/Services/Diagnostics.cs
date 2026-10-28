using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AudioLink.Audio;
using AudioLink.Core;

namespace AudioLink.Services;

internal static class Diagnostics
{
    private sealed class DiagnosticReport
    {
        public string Timestamp { get; set; } = DateTimeOffset.Now.ToString("O");
        public string OS { get; set; } = RuntimeInformation.OSDescription;
        public string Runtime { get; set; } = RuntimeInformation.FrameworkDescription;
        public string Mode { get; set; } = "";
        public string? SourceId { get; set; }
        public IReadOnlyList<EndpointInfo> Devices { get; set; } = [];
        public List<object> Samples { get; } = [];
        public List<string> Errors { get; } = [];
        public List<object> ToneMeasurements { get; } = [];
        public bool Passed { get; set; }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        int modeIndex = Array.FindIndex(args, x => x is "--diagnostics" or "--smoke" or "--probe");
        string mode = args[modeIndex];
        string reportPath = args.ElementAtOrDefault(modeIndex + 1) is { } candidate && !candidate.StartsWith("--", StringComparison.Ordinal)
            ? Path.GetFullPath(candidate) : Path.Combine(AppLog.DataDirectory, "diagnostics.json");
        var report = new DiagnosticReport { Mode = mode };
        try
        {
            report.Devices = await Task.Run(DeviceCatalog.Enumerate);
            if (mode == "--diagnostics") report.Passed = report.Devices.Count > 0;
            else
            {
                string? sourceOverride = GetOption(args, "--source");
                EndpointInfo source = sourceOverride is not null
                    ? report.Devices.FirstOrDefault(x => x.Id == sourceOverride) ?? throw new InvalidOperationException("指定的音源设备未连接。")
                    : report.Devices.FirstOrDefault(x => x.IsDefault) ?? report.Devices.First();
                report.SourceId = source.Id;
                EndpointInfo[] targets = report.Devices.Where(x => x.Id != source.Id).ToArray();
                if (targets.Length == 0) throw new InvalidOperationException("实际多设备测试至少需要两个已连接的播放端点。");
                int duration = int.TryParse(GetOption(args, "--seconds"), out int seconds) ? Math.Clamp(seconds, 3, 600) : 15;
                LatencyProfile profile = LatencyProfile.All.FirstOrDefault(x => x.Id == GetOption(args, "--profile")) ?? LatencyProfile.All[1];
                var controls = targets.ToDictionary(x => x.Id, _ => new RouteControls());
                var errorsGate = new object();
                await using var engine = new RoutingEngine();
                engine.DeviceFaulted += (id, ex) => { lock (errorsGate) report.Errors.Add($"{id}: {ex}"); };
                await engine.ApplyAsync(source.Id, targets.Select(x => new RouteRequest(x.Id, controls[x.Id])).ToArray(), profile);
                using var probeStop = new CancellationTokenSource();
                RenderWorker? probe = null;
                var monitors = new List<CaptureWorker>();
                if (mode == "--probe")
                {
                    foreach (EndpointInfo device in report.Devices)
                    {
                        var monitor = new CaptureWorker(device.Id, probeStop.Token);
                        monitors.Add(monitor); monitor.Start(); await monitor.Ready.WaitAsync(TimeSpan.FromSeconds(6));
                    }
                    probe = new(source.Id, null, new(), 20, 0, () => 0, probeStop.Token, true);
                    probe.Start(); await probe.Ready.WaitAsync(TimeSpan.FromSeconds(6));
                }
                try
                {
                    var watch = Stopwatch.StartNew();
                    while (watch.Elapsed.TotalSeconds < duration)
                    {
                        await Task.Delay(250);
                        report.Samples.Add(new { Seconds = watch.Elapsed.TotalSeconds, Statistics = engine.GetStatistics() });
                    }
                    EngineStatistics last = engine.GetStatistics();
                    report.Passed = last.Running && last.CapturedFrames >= source.Format.SampleRate && last.Outputs.Count == targets.Length && last.CaptureDiscontinuities == 0 &&
                        last.Outputs.All(x => x.RenderedFrames >= x.Format.SampleRate && x.Audio.Primed && x.Audio.Underruns == 0 && x.DriverUnderruns == 0);
                    if (mode == "--probe")
                    {
                        foreach (CaptureWorker monitor in monitors)
                        {
                            double amplitude = MeasureTone(monitor.Timeline, 997);
                            report.ToneMeasurements.Add(new { monitor.DeviceId, Amplitude = amplitude, monitor.Frames });
                            if (amplitude < 0.000001) { report.Passed = false; report.Errors.Add($"未在端点 {monitor.DeviceId} 捕获到 997 Hz 测试信号；检查设备系统静音或回环支持。"); }
                        }
                        // Check an individual output's gain and mute through its real loopback.
                        EndpointInfo target = targets[0];
                        CaptureWorker meter = monitors.First(x => x.DeviceId == target.Id);
                        double full = MeasureTone(meter.Timeline, 997);
                        controls[target.Id].Volume = 0.4f;
                        await Task.Delay(1500);
                        double reduced = MeasureTone(meter.Timeline, 997);
                        controls[target.Id].Muted = true;
                        await Task.Delay(1500);
                        double muted = MeasureTone(meter.Timeline, 997);
                        double ratio = full > 0 ? reduced / full : 0;
                        report.ToneMeasurements.Add(new { target.Id, Full = full, Reduced = reduced, Muted = muted, GainRatio = ratio });
                        if (ratio is < 0.32 or > 0.48 || muted > Math.Max(0.000001, full * 0.01)) report.Passed = false;
                        if (args.Contains("--check-device-volume", StringComparer.Ordinal))
                        {
                            controls[target.Id].Volume = 1;
                            controls[target.Id].Muted = false;
                            await Task.Delay(800);
                            var originals = new Dictionary<string, EndpointVolumeState>(StringComparer.Ordinal);
                            try
                            {
                                foreach (EndpointInfo device in report.Devices)
                                {
                                    using var controller = new EndpointVolumeController(device.Id);
                                    originals[device.Id] = controller.Read();
                                }
                                foreach (EndpointInfo device in report.Devices)
                                {
                                    using var controller = new EndpointVolumeController(device.Id);
                                    EndpointVolumeState initial = originals[device.Id];
                                    controller.SetVolume(initial.Volume / 2);
                                    EndpointVolumeState reducedState = controller.Read();
                                    controller.SetMute(!initial.Muted);
                                    EndpointVolumeState muteState = controller.Read();
                                    report.ToneMeasurements.Add(new { Kind = "WindowsDeviceVolume", device.Id, Initial = initial, Reduced = reducedState, Toggled = muteState });
                                    if (Math.Abs(reducedState.Volume - initial.Volume / 2) > 0.001 || muteState.Muted == initial.Muted) report.Passed = false;
                                    foreach ((string otherId, EndpointVolumeState otherState) in originals.Where(x => x.Key != device.Id))
                                    {
                                        using var other = new EndpointVolumeController(otherId);
                                        EndpointVolumeState unchanged = other.Read();
                                        if (Math.Abs(unchanged.Volume - otherState.Volume) > 0.001 || unchanged.Muted != otherState.Muted)
                                        { report.Errors.Add($"设备 {device.Id} 的音量与 {otherId} 联动，无法独立控制。"); report.Passed = false; }
                                    }
                                    controller.Restore(initial);
                                }
                                using var sourceVolume = new EndpointVolumeController(source.Id);
                                sourceVolume.SetMute(false);
                                await Task.Delay(800);
                                double targetBefore = MeasureTone(meter.Timeline, 997);
                                sourceVolume.SetVolume(originals[source.Id].Volume / 2);
                                await Task.Delay(800);
                                double afterSourceVolume = MeasureTone(meter.Timeline, 997);
                                sourceVolume.SetMute(true);
                                await Task.Delay(800);
                                double afterSourceMute = MeasureTone(meter.Timeline, 997);
                                report.ToneMeasurements.Add(new { Kind = "SourceIndependence", target.Id, Before = targetBefore, AfterSourceVolume = afterSourceVolume, AfterSourceMute = afterSourceMute });
                                if (targetBefore > 0.000001 && (Math.Abs(afterSourceVolume / targetBefore - 1) > 0.1 || Math.Abs(afterSourceMute / targetBefore - 1) > 0.1))
                                { report.Errors.Add("此源设备的系统音量或静音影响回环信号。"); report.Passed = false; }
                            }
                            finally
                            {
                                foreach ((string id, EndpointVolumeState state) in originals)
                                {
                                    try
                                    {
                                        using var controller = new EndpointVolumeController(id);
                                        controller.Restore(state);
                                        EndpointVolumeState restored = controller.Read();
                                        if (Math.Abs(restored.Volume - state.Volume) > 0.001 || restored.Muted != state.Muted)
                                            report.Errors.Add($"设备 {id} 的音量未恢复到原值。");
                                    }
                                    catch (Exception ex) { report.Errors.Add($"恢复设备 {id} 的音量失败：{ex.Message}"); }
                                }
                            }
                        }
                    }
                }
                finally
                {
                    probeStop.Cancel();
                    if (probe is not null) await probe.Completion;
                    foreach (CaptureWorker monitor in monitors) await monitor.Completion;
                    await engine.StopAsync();
                }
                if (report.Errors.Count > 0) report.Passed = false;
            }
        }
        catch (Exception ex) { report.Errors.Add(ex.ToString()); report.Passed = false; AppLog.Write("诊断", ex); }
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.Passed ? 0 : 1;
    }

    private static string? GetOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 ? args.ElementAtOrDefault(index + 1) : null;
    }
    private static double MeasureTone(AudioTimelineBuffer timeline, double frequency)
    {
        TimelineSnapshot snapshot = timeline.Snapshot();
        int frames = Math.Min(timeline.SampleRate / 2, (int)snapshot.WriteFrame);
        if (frames < 100) return 0;
        var samples = new float[frames * timeline.Channels];
        if (!timeline.CopyFrames(snapshot.WriteFrame - frames, samples, snapshot.Epoch)) return 0;
        double sine = 0, cosine = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            double phase = 2 * Math.PI * frequency * frame / timeline.SampleRate;
            sine += samples[frame * timeline.Channels] * Math.Sin(phase);
            cosine += samples[frame * timeline.Channels] * Math.Cos(phase);
        }
        return 2 * Math.Sqrt(sine * sine + cosine * cosine) / frames;
    }
}
