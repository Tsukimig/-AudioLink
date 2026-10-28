using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AudioLink.Network;

public sealed class PhonePeer
{
    private readonly Socket _socket;
    private IPEndPoint _remote;
    private long _lastSeen, _sendDrops;
    private int _connected = 1;
    private readonly object _feedbackGate = new();
    private readonly ReceiverClockController _clock = new();
    private long _lastFeedback;
    private double _rateCorrection, _playbackLatency;
    private int _targetFrames;
    private int _queueFrames, _receiverPlaying, _nativeBufferFrames, _nativeQueueFrames;
    private int _silentIdle, _nativePlayState, _performanceMode;
    private long _underruns;
    public Guid ClientId { get; }
    public Guid Session { get; }
    public string Id => "android:" + ClientId.ToString("N");
    public string Name { get; }
    public double PlaybackLatencyMs => Volatile.Read(ref _playbackLatency);
    public double RateCorrection => Environment.TickCount64 - Interlocked.Read(ref _lastFeedback) < 2000 ? Volatile.Read(ref _rateCorrection) : 0;
    public int ReceiverQueueFrames => Volatile.Read(ref _queueFrames);
    public int ReceiverTargetFrames => Volatile.Read(ref _targetFrames);
    public int ReceiverNativeBufferFrames => Volatile.Read(ref _nativeBufferFrames);
    public int ReceiverNativeQueueFrames => Volatile.Read(ref _nativeQueueFrames);
    public bool ReceiverSilentIdle => Volatile.Read(ref _silentIdle) != 0;
    public int ReceiverNativePlayState => Volatile.Read(ref _nativePlayState);
    public int ReceiverPerformanceMode => Volatile.Read(ref _performanceMode);
    public long ReceiverUnderruns => Interlocked.Read(ref _underruns);
    public bool ReceiverPlaying => Volatile.Read(ref _receiverPlaying) != 0;
    public bool IsConnected => Volatile.Read(ref _connected) != 0;
    public long LastSeen => Interlocked.Read(ref _lastSeen);
    public long SendDrops => Interlocked.Read(ref _sendDrops);
    internal PhonePeer(Socket socket, IPEndPoint remote, Guid client, Guid session, string name, double latency, int targetFrames)
    { _socket = socket; _remote = remote; ClientId = client; Session = session; Name = name; _playbackLatency = latency; _targetFrames = targetFrames; Touch(); }
    internal bool Matches(IPEndPoint remote) => _remote.Equals(remote);
    internal void Touch() { Interlocked.Exchange(ref _lastSeen, Environment.TickCount64); Volatile.Write(ref _connected, 1); }
    internal void Disconnect() => Volatile.Write(ref _connected, 0);
    internal bool Feedback(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty) return false;
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return false;
        lock (_feedbackGate)
        {
            long now = Environment.TickCount64;
            double dt = _lastFeedback == 0 ? 0.5 : Math.Clamp((now - _lastFeedback) / 1000d, 0.05, 2);
            Interlocked.Exchange(ref _lastFeedback, now);
            bool changed = false;
            if (root.TryGetProperty("underruns", out var underruns) && underruns.ValueKind == JsonValueKind.Number && underruns.TryGetInt64(out long misses) && misses is >= 0 and <= 1_000_000_000)
                Interlocked.Exchange(ref _underruns, misses);
            if (root.TryGetProperty("nativeBufferFrames", out var nativeBuffer) && nativeBuffer.ValueKind == JsonValueKind.Number && nativeBuffer.TryGetInt32(out int capacity) && capacity is > 0 and <= 48000)
                Volatile.Write(ref _nativeBufferFrames, capacity);
            if (root.TryGetProperty("nativeQueueFrames", out var nativeQueue) && nativeQueue.ValueKind == JsonValueKind.Number && nativeQueue.TryGetInt32(out int pending) && pending is >= 0 and <= 48000)
                Volatile.Write(ref _nativeQueueFrames, pending);
            if (root.TryGetProperty("nativePlayState", out var playState) && playState.ValueKind == JsonValueKind.Number && playState.TryGetInt32(out int state) && state is >= 1 and <= 3)
                Volatile.Write(ref _nativePlayState, state);
            if (root.TryGetProperty("performanceMode", out var performanceMode) && performanceMode.ValueKind == JsonValueKind.Number && performanceMode.TryGetInt32(out int performance) && performance is >= 0 and <= 2)
                Volatile.Write(ref _performanceMode, performance);
            if (root.TryGetProperty("silentIdle", out var idle) && idle.ValueKind is JsonValueKind.True or JsonValueKind.False)
                Volatile.Write(ref _silentIdle, idle.GetBoolean() ? 1 : 0);
            if (root.TryGetProperty("targetFrames", out var target) && target.ValueKind == JsonValueKind.Number && target.TryGetInt32(out int frames) && frames is >= 480 and <= 9600)
            {
                changed = frames != _targetFrames;
                Volatile.Write(ref _targetFrames, frames);
                Volatile.Write(ref _playbackLatency, frames / 48d);
            }
            bool active = root.TryGetProperty("playing", out var playing) && playing.ValueKind == JsonValueKind.True;
            Volatile.Write(ref _receiverPlaying, active ? 1 : 0);
            if (!active)
            { _clock.Reset(); Volatile.Write(ref _rateCorrection, 0); return changed; }
            if (root.TryGetProperty("queueFrames", out var queue) && queue.ValueKind == JsonValueKind.Number && queue.TryGetInt32(out int queued) && queued is >= 0 and <= 48000)
            {
                Volatile.Write(ref _queueFrames, queued);
                Volatile.Write(ref _rateCorrection, _clock.Update(queued, _targetFrames, dt));
            }
            return changed;
        }
    }
    public bool Send(ReadOnlySpan<byte> packet)
    {
        if (!IsConnected) return false;
        try { return _socket.SendTo(packet, SocketFlags.None, _remote) == packet.Length; }
        catch (SocketException) { Interlocked.Increment(ref _sendDrops); return false; }
        catch (ObjectDisposedException) { return false; }
    }
    public void SendStopped() => Send(PhoneProtocol.Control(PhonePacketType.Stopped, Session));
}

/// <summary>Explicitly started, paired unicast transport. Never captures audio by itself.</summary>
public sealed class PhoneServer : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, PhonePeer> _peers = [];
    private readonly TimeSpan _peerTimeout;
    private Socket? _socket;
    private CancellationTokenSource? _stop;
    private Task? _receive, _watch;
    private string _code = "";
    private long _codeExpiry, _attemptWindow;
    private int _attempts;
    public event Action? Changed;
    public bool IsRunning => _socket is not null;
    public int Port => (_socket?.LocalEndPoint as IPEndPoint)?.Port ?? PhoneProtocol.Port;
    public string PairingCode { get { lock (_gate) return _code; } }
    public int PairingSeconds { get { lock (_gate) return (int)Math.Max(0, (_codeExpiry - Environment.TickCount64) / 1000); } }
    public IReadOnlyList<PhonePeer> Peers { get { lock (_gate) return _peers.Values.ToArray(); } }
    public PhoneServer(TimeSpan? peerTimeout = null) => _peerTimeout = peerTimeout ?? TimeSpan.FromSeconds(6);
    public void Start(IPAddress? address = null, int port = PhoneProtocol.Port)
    {
        if (_socket is not null) return;
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(address ?? IPAddress.Any, port));
            socket.SendBufferSize = 256 * 1024; socket.ReceiveBufferSize = 64 * 1024;
            socket.Blocking = false;
        }
        catch { socket.Dispose(); throw; }
        _socket = socket; _stop = new(); RenewPairingCode();
        _receive = ReceiveAsync(socket, _stop.Token);
        _watch = WatchAsync(_stop.Token);
    }
    public void RenewPairingCode()
    {
        lock (_gate) { _code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6"); _codeExpiry = Environment.TickCount64 + 180_000; }
        Changed?.Invoke();
    }
    public PhonePeer? Find(string id) { lock (_gate) return _peers.Values.FirstOrDefault(x => x.Id == id && x.IsConnected); }
    public bool ForgetDisconnected(string id)
    {
        if (!id.StartsWith("android:", StringComparison.Ordinal) || !Guid.TryParseExact(id[8..], "N", out Guid clientId)) return false;
        lock (_gate)
        {
            if (!_peers.TryGetValue(clientId, out PhonePeer? peer)) return true;
            if (peer.IsConnected) return false;
            _peers.Remove(clientId);
        }
        Changed?.Invoke();
        return true;
    }
    private async Task ReceiveAsync(Socket socket, CancellationToken cancellation)
    {
        byte[] buffer = new byte[1500];
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                SocketReceiveFromResult received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellation).ConfigureAwait(false);
                if (received.RemoteEndPoint is not IPEndPoint remote || !IsLocal(remote.Address) || !PhoneProtocol.TryRead(buffer.AsSpan(0, received.ReceivedBytes), out var header)) continue;
                if (header.Type == PhonePacketType.Hello) HandleHello(socket, remote, header, buffer.AsMemory(PhoneProtocol.HeaderSize, received.ReceivedBytes - PhoneProtocol.HeaderSize));
                else HandlePeer(remote, header, buffer.AsMemory(PhoneProtocol.HeaderSize, received.ReceivedBytes - PhoneProtocol.HeaderSize));
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { break; }
            catch (SocketException) when (!cancellation.IsCancellationRequested) { await Task.Delay(50, cancellation).ConfigureAwait(false); }
            catch (JsonException) { }
            catch (ArgumentException) { }
        }
    }
    private void HandleHello(Socket socket, IPEndPoint remote, PhonePacketHeader header, ReadOnlyMemory<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String || !Guid.TryParse(idValue.GetString(), out Guid clientId) || clientId == Guid.Empty) return;
        string supplied = root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() ?? "" : "";
        string name = root.TryGetProperty("name", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() ?? "安卓手机" : "安卓手机";
        name = new string(name.Where(c => !char.IsControl(c)).Take(48).ToArray()).Trim();
        if (name.Length == 0) name = "安卓手机";
        double latency = root.TryGetProperty("latencyMs", out var latencyValue) && latencyValue.ValueKind == JsonValueKind.Number && latencyValue.TryGetDouble(out double reported) && double.IsFinite(reported) ? Math.Clamp(reported, 10, 250) : 40;
        int targetFrames = root.TryGetProperty("targetFrames", out var targetValue) && targetValue.ValueKind == JsonValueKind.Number && targetValue.TryGetInt32(out int target) && target is >= 480 and <= 9600 ? target : 960;
        PhonePeer? peer;
        bool changed = false;
        lock (_gate)
        {
            _peers.TryGetValue(clientId, out PhonePeer? previous);
            bool resumed = previous is not null && previous.Session == header.Session && header.Session != Guid.Empty;
            if (!resumed)
            {
                long now = Environment.TickCount64;
                if (now - _attemptWindow >= 1000) { _attempts = 0; _attemptWindow = now; }
                if (++_attempts > 12) return;
                if (now >= _codeExpiry || supplied.Length != 6 || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(supplied), System.Text.Encoding.ASCII.GetBytes(_code)))
                { SendControl(socket, remote, PhoneProtocol.Control(PhonePacketType.Rejected, Guid.Empty, "{\"error\":\"连接码不正确或已过期\"}")); return; }
            }
            if (previous is null && _peers.Values.Count(x => x.IsConnected) >= 8)
            { SendControl(socket, remote, PhoneProtocol.Control(PhonePacketType.Rejected, Guid.Empty, "{\"error\":\"最多连接八部手机\"}")); return; }
            if (previous is not null && previous.IsConnected && previous.Matches(remote) && resumed) { peer = previous; peer.Touch(); }
            else
            {
                previous?.Disconnect();
                peer = new(socket, remote, clientId, resumed ? header.Session : Guid.NewGuid(), name, latency, targetFrames);
                _peers[clientId] = peer; changed = true;
                foreach (Guid stale in _peers.Where(x => !x.Value.IsConnected && x.Key != clientId).Select(x => x.Key).Take(Math.Max(0, _peers.Count - 32)).ToArray()) _peers.Remove(stale);
            }
        }
        SendControl(socket, remote, PhoneProtocol.Control(PhonePacketType.Welcome, peer.Session, "{\"rate\":48000,\"channels\":2,\"frames\":240}"));
        if (changed) Changed?.Invoke();
    }
    private void HandlePeer(IPEndPoint remote, PhonePacketHeader header, ReadOnlyMemory<byte> payload)
    {
        PhonePeer? peer;
        bool disconnected = false;
        lock (_gate)
        {
            peer = _peers.Values.FirstOrDefault(x => x.Session == header.Session && x.Matches(remote) && x.IsConnected);
            if (peer is null) return;
            if (header.Type == PhonePacketType.Goodbye) { peer.Disconnect(); disconnected = true; }
            else if (header.Type == PhonePacketType.Heartbeat) peer.Touch();
            else return;
        }
        if (disconnected) Changed?.Invoke();
        else
        {
            if (peer.Feedback(payload)) Changed?.Invoke();
            peer.Send(PhoneProtocol.Control(PhonePacketType.Heartbeat, peer.Session));
        }
    }
    private async Task WatchAsync(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            await Task.Delay(250, cancellation).ConfigureAwait(false);
            bool changed = false;
            lock (_gate)
                foreach (PhonePeer peer in _peers.Values.Where(x => x.IsConnected && Environment.TickCount64 - x.LastSeen > _peerTimeout.TotalMilliseconds))
                { peer.Disconnect(); changed = true; }
            if (changed) Changed?.Invoke();
        }
    }
    private static void SendControl(Socket socket, IPEndPoint remote, byte[] packet)
    { try { socket.SendTo(packet, SocketFlags.None, remote); } catch (SocketException) { } catch (ObjectDisposedException) { } }
    private static bool IsLocal(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] is 10 or 127 || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 169 && bytes[1] == 254));
    }
    public async ValueTask DisposeAsync()
    {
        _stop?.Cancel(); _socket?.Dispose(); _socket = null;
        foreach (Task? task in new[] { _receive, _watch })
            if (task is not null) try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (SocketException) { } catch (ObjectDisposedException) { }
        lock (_gate) { foreach (PhonePeer peer in _peers.Values) peer.Disconnect(); }
        _stop?.Dispose(); _stop = null;
        Changed?.Invoke(); GC.SuppressFinalize(this);
    }
}
