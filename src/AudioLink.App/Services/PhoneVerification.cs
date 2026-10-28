using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AudioLink.Audio;
using AudioLink.Core;
using AudioLink.Network;

namespace AudioLink.Services;

/// <summary>Real Windows pacing to the actual Java receiver parser, without changing audio devices.</summary>
internal static class PhoneVerification
{
    private sealed class ToneSource : AudioWorker
    {
        public AudioTimelineBuffer Timeline { get; } = new(48000, 2);
        public ToneSource(CancellationToken token) : base("test-source", token) { }
        protected override void Run()
        {
            using var timer = new AudioPacketTimer();
            WaitHandle[] waits = [Token.WaitHandle, timer];
            float[] samples = new float[960];long frame = 0;
            double next = AudioClock.Now100ns;
            SetReady();
            while (!Token.IsCancellationRequested)
            {
                double now = AudioClock.Now100ns;
                if (now < next) { timer.Arm(next - now);if (WaitHandle.WaitAny(waits) == 0) break;continue; }
                for (int i = 0; i < 480; i++, frame++) samples[i * 2] = samples[i * 2 + 1] = (float)(0.1 * Math.Sin(2 * Math.PI * 997 * frame / 48000));
                Timeline.Write(samples, next - 100_000);
                next += 100_000;
            }
        }
    }
    private sealed record SinkSnapshot(long Packets, long SequenceGaps, double Rms, double MeanMs, double P95Ms, double P99Ms, double MaximumMs, long BurstsUnder1Ms, double MaximumAdjacentStep);
    private sealed class Sink : IAsyncDisposable
    {
        private readonly UdpClient _client = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly object _gate = new();
        private readonly List<double> _intervals = [];
        private readonly Queue<float> _samples = new();
        private readonly CancellationTokenSource _stop = new();
        private Task? _receive, _heartbeat;
        private long _packets, _gaps, _lastTime;
        private uint _expected;
        public Guid Session { get; private set; }
        public async Task ConnectAsync(int port, string code)
        {
            _client.Connect(IPAddress.Loopback, port);
            var hello = PhoneProtocol.Control(PhonePacketType.Hello, Guid.Empty, JsonSerializer.Serialize(new { id = Guid.NewGuid(), name = "Windows pacing receiver", code, latencyMs = 60, targetFrames = 2880 }));
            await _client.SendAsync(hello);
            UdpReceiveResult welcome = await _client.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(4));
            if (!PhoneProtocol.TryRead(welcome.Buffer, out var header) || header.Type != PhonePacketType.Welcome) throw new IOException("Phone handshake failed.");
            Session = header.Session;
            _receive = ReceiveAsync(_stop.Token);
            _heartbeat = HeartbeatAsync(_stop.Token);
        }
        private async Task HeartbeatAsync(CancellationToken cancellation)
        {
            byte[] packet = PhoneProtocol.Control(PhonePacketType.Heartbeat, Session, "{\"playing\":true,\"queueFrames\":2880,\"targetFrames\":2880}");
            while (!cancellation.IsCancellationRequested) { await _client.SendAsync(packet, cancellation);await Task.Delay(300, cancellation); }
        }
        private async Task ReceiveAsync(CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                UdpReceiveResult result = await _client.ReceiveAsync(cancellation);
                if (!PhoneProtocol.TryRead(result.Buffer, out var header) || header.Type != PhonePacketType.Audio || header.Session != Session) continue;
                long now = Stopwatch.GetTimestamp();
                float[] values = new float[480];PcmConverter.Decode(result.Buffer.AsSpan(40), values, 16, false);
                lock (_gate)
                {
                    if (_packets > 0) { _gaps += (uint)(header.Sequence - _expected);_intervals.Add((now - _lastTime) * 1000d / Stopwatch.Frequency); }
                    _lastTime = now;_expected = header.Sequence + 1;_packets++;
                    for (int i = 0; i < values.Length; i += 2) { _samples.Enqueue(values[i]);if (_samples.Count > 24000) _samples.Dequeue(); }
                }
            }
        }
        public SinkSnapshot Snapshot()
        {
            lock (_gate)
            {
                double[] intervals = _intervals.Order().ToArray();
                double percentile(double fraction) => intervals.Length == 0 ? 0 : intervals[Math.Min(intervals.Length - 1, (int)(intervals.Length * fraction))];
                float[] samples = _samples.ToArray();double maxStep = 0;
                for (int i = 1; i < samples.Length; i++) maxStep = Math.Max(maxStep, Math.Abs(samples[i] - samples[i - 1]));
                return new(_packets, _gaps, _samples.Count == 0 ? 0 : Math.Sqrt(_samples.Sum(x => (double)x * x) / _samples.Count),
                    intervals.Length == 0 ? 0 : intervals.Average(), percentile(0.95), percentile(0.99), intervals.LastOrDefault(), intervals.Count(x => x < 1), maxStep);
            }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            foreach (Task? task in new[] { _receive, _heartbeat }) if (task is not null) try { await task; } catch (OperationCanceledException) { }
            _client.Dispose();_stop.Dispose();
        }
    }
    public static async Task<int> RunAsync(string[] args)
    {
        string Option(string name, string fallback) { int i = Array.IndexOf(args, name);return i < 0 ? fallback : args.ElementAtOrDefault(i + 1) ?? fallback; }
        string path = Path.GetFullPath(Option("--verify-phone", "phone-verification.json"));
        string java = Option("--java", "");string classes = Option("--java-classes", "");
        int seconds = int.TryParse(Option("--seconds", "15"), out int duration) ? Math.Clamp(duration, 10, 120) : 15;
        string androidCodeFile = Option("--android-code-file", "");
        var errors = new List<string>();var checks = new List<string>();
        SinkSnapshot? pacing = null;double full = 0, reduced = 0, muted = 0;string javaOutput = "";
        object? androidFeedback = null, androidIdle = null;SinkSnapshot? waveform = null;
        using var stop = new CancellationTokenSource();
        await using var server = new PhoneServer();
        await using var sink = new Sink();
        var tone = new ToneSource(stop.Token);
        var workers = new List<PhoneRenderWorker>();Process? javaProcess = null;Task<string>? javaText = null;Task<string>? javaErrors = null;
        var routeControls = new Dictionary<Guid, RouteControls>();
        try
        {
            server.Start(IPAddress.Loopback, 0);
            await sink.ConnectAsync(server.Port, server.PairingCode);
            if (File.Exists(java) && Directory.Exists(classes))
            {
                var start = new ProcessStartInfo(java) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string argument in new[] { "-cp", classes, "com.audiolink.receiver.ReceiverTests", "network", "127.0.0.1", server.Port.ToString(), server.PairingCode }) start.ArgumentList.Add(argument);
                javaProcess = Process.Start(start) ?? throw new IOException("Java receiver did not start.");
                javaText = javaProcess.StandardOutput.ReadToEndAsync();javaErrors = javaProcess.StandardError.ReadToEndAsync();
                var wait = Stopwatch.StartNew();
                while (server.Peers.Count(x => x.IsConnected) < 2 && wait.Elapsed.TotalSeconds < 5) await Task.Delay(25);
                if (server.Peers.Count(x => x.IsConnected) != 2) throw new IOException("Java receiver did not pair.");
            }
            else throw new IOException("Pass --java and --java-classes to test the Android receiver implementation.");
            if (androidCodeFile.Length > 0)
            {
                await File.WriteAllTextAsync(androidCodeFile, JsonSerializer.Serialize(new { Host = $"10.0.2.2:{server.Port}", Code = server.PairingCode }));
                var wait = Stopwatch.StartNew();
                while (server.Peers.Count(x => x.IsConnected) < 3 && wait.Elapsed.TotalSeconds < 90) await Task.Delay(50);
                if (server.Peers.Count(x => x.IsConnected) != 3) throw new IOException("Android device did not pair.");
            }
            tone.Start();await tone.Ready;await Task.Delay(200);
            var controls = new RouteControls();
            foreach (PhonePeer peer in server.Peers.Where(x => x.IsConnected))
            {
                var route = peer.Session == sink.Session ? controls : new();routeControls.Add(peer.Session, route);
                var worker = new PhoneRenderWorker(peer, tone.Timeline, route, 3, () => 160, stop.Token);
                workers.Add(worker);worker.Start();await worker.Ready;
            }
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            waveform = sink.Snapshot();full = waveform.Rms;
            if (waveform.MaximumAdjacentStep > 0.02) errors.Add("Received steady sine has an unexpected PCM discontinuity.");
            controls.Volume = 0.4f;await Task.Delay(1300);reduced = sink.Snapshot().Rms;
            controls.Muted = true;await Task.Delay(1300);muted = sink.Snapshot().Rms;
            pacing = sink.Snapshot();
            if (pacing.SequenceGaps != 0 || pacing.Packets < seconds * 180 || pacing.P95Ms > 10 || pacing.P99Ms > 25) errors.Add("Packet pacing or delivery did not meet the continuity budget.");
            if (full < 0.02 || Math.Abs(reduced / full - 0.4) > 0.015 || muted > 0.00001) errors.Add("Phone gain / mute failed.");
            foreach (PhoneRenderWorker worker in workers)
                if (worker.Error is not null || worker.OutputStatistics is not { Audio.Underruns: 0, DriverUnderruns: 0 }) errors.Add($"Sender failure: {worker.Error?.Message ?? worker.OutputStatistics?.ToString()}");
            if (tone.Error is not null) errors.Add(tone.Error.ToString());
            checks.Add("Multiple paired unicast receivers consumed the same synthetic timeline; no physical audio devices were changed.");
            checks.Add("Actual Windows high resolution scheduling and the Android Java protocol / jitter buffer ran over loopback UDP.");
            checks.Add("Per-phone volume and mute were measured on received PCM.");
            if (androidCodeFile.Length > 0)
            {
                PhonePeer android = server.Peers.Single(x => !x.Name.Contains("receiver", StringComparison.OrdinalIgnoreCase));
                object Feedback() => new { android.Name, android.ReceiverPlaying, android.ReceiverQueueFrames, android.ReceiverTargetFrames, android.ReceiverNativeBufferFrames, android.ReceiverNativeQueueFrames, android.ReceiverNativePlayState, android.ReceiverSilentIdle, android.ReceiverPerformanceMode, android.ReceiverUnderruns, CorrectionPpm = android.RateCorrection * 1_000_000 };
                androidFeedback = Feedback();
                if (!android.ReceiverPlaying || android.ReceiverQueueFrames == 0 || android.ReceiverUnderruns > 0) errors.Add("Android AudioTrack reported an underrun or did not play.");
                routeControls[android.Session].Muted = true;await Task.Delay(1800);
                androidIdle = Feedback();
                if (!android.ReceiverSilentIdle || android.ReceiverPlaying || android.ReceiverNativePlayState != 2 || android.ReceiverNativeQueueFrames != 0) errors.Add("Digital silence did not pause / flush the Android audio output.");
                routeControls[android.Session].Muted = false;await Task.Delay(1800);
                if (!android.ReceiverPlaying || android.ReceiverSilentIdle || android.ReceiverUnderruns > 0) errors.Add("Android did not resume cleanly after silent standby.");
                checks.Add("An installed Android APK consumed the stream and reported actual AudioTrack queue / underrun feedback.");
                checks.Add("Digital silence paused and flushed AudioTrack; nonzero PCM resumed playback without a reported underrun.");
            }
        }
        catch (Exception ex) { errors.Add(ex.ToString()); }
        finally
        {
            stop.Cancel();
            foreach (PhoneRenderWorker worker in workers) await worker.Completion;
            if (tone.IsAlive && workers.Count > 0) await tone.Completion;
            if (javaProcess is not null)
            {
                try { await javaProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { javaProcess.Kill(true);await javaProcess.WaitForExitAsync();errors.Add("Java receiver did not stop."); }
                javaOutput = (javaText is null ? "" : await javaText) + (javaErrors is null ? "" : await javaErrors);
                if (javaProcess.ExitCode != 0) errors.Add(javaOutput);
                javaProcess.Dispose();
            }
            if (androidCodeFile.Length > 0 && File.Exists(androidCodeFile)) File.Delete(androidCodeFile);
        }
        bool passed = errors.Count == 0;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Passed = passed, Checks = checks, Pacing = pacing, Waveform = waveform, Full = full, Reduced = reduced, Muted = muted, GainRatio = full > 0 ? reduced / full : 0, JavaReceiver = javaOutput, Android = androidFeedback, AndroidSilentStandby = androidIdle, Errors = errors }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}
