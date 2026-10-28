using AudioLink.Core;
using AudioLink.Network;

namespace AudioLink.Audio;

internal sealed class PhoneRenderWorker : AudioWorker
{
    private readonly PhonePeer _peer;
    private readonly AudioTimelineBuffer _timeline;
    private readonly RouteControls _controls;
    private readonly uint _sourceMask;
    private readonly Func<double> _commonLatency;
    private AdaptiveRenderer? _renderer;
    private long _frames, _sendMisses;
    public override EndpointStatistics? OutputStatistics => _renderer is null ? null : new(DeviceId, new(48000, 2, 16, false, 3),
        _peer.PlaybackLatencyMs, _peer.PlaybackLatencyMs, 0, Interlocked.Read(ref _frames), Interlocked.Read(ref _sendMisses), _renderer.Statistics, 5, "手机 · USB / 网络", 0);
    public PhoneRenderWorker(PhonePeer peer, AudioTimelineBuffer timeline, RouteControls controls, uint sourceMask, Func<double> commonLatency, CancellationToken token) : base(peer.Id, token)
    { _peer = peer; _timeline = timeline; _controls = controls; _sourceMask = sourceMask; _commonLatency = commonLatency; }
    protected override void Run()
    {
        _renderer = new(_timeline, PhoneProtocol.SampleRate, 2, PhoneProtocol.FramesPerPacket, _controls, _sourceMask, 3);
        float[] samples = new float[PhoneProtocol.FramesPerPacket * 2];
        byte[] packet = new byte[PhoneProtocol.HeaderSize + PhoneProtocol.AudioBytes];
        uint sequence = 0;
        ulong framePosition = (ulong)(AudioClock.Now100ns * PhoneProtocol.SampleRate / 10_000_000);
        double next = AudioClock.Now100ns;
        using var timer = new AudioPacketTimer();
        WaitHandle[] waits = [Token.WaitHandle, timer];
        bool firstPacket = true;
        SetReady();
        try
        {
            while (!Token.IsCancellationRequested && _peer.IsConnected)
            {
                double now = AudioClock.Now100ns;
                if (now < next) { timer.Arm(next - now); if (WaitHandle.WaitAny(waits) == 0) break; continue; }
                bool reset = now - next > 2_000_000;
                if (reset) next = now;
                _renderer.Render(samples, now, 0, _peer.PlaybackLatencyMs, _commonLatency());
                PcmConverter.Encode(samples, packet.AsSpan(PhoneProtocol.HeaderSize), 16, false);
                PhoneProtocol.WriteHeader(packet, new(PhonePacketType.Audio, _peer.Session, sequence++, framePosition, PhoneProtocol.FramesPerPacket, (ushort)(reset || firstPacket ? 1 : 0)));
                firstPacket = false;
                if (!_peer.Send(packet)) Interlocked.Increment(ref _sendMisses);
                Interlocked.Add(ref _frames, PhoneProtocol.FramesPerPacket);
                framePosition += PhoneProtocol.FramesPerPacket;
                next += 50_000 / (1 + _peer.RateCorrection);
            }
        }
        finally { _peer.SendStopped(); }
    }
}
