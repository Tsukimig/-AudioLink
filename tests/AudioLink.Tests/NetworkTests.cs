using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AudioLink.Network;

internal static class NetworkTests
{
    public static void Pairing() => PairingAsync().GetAwaiter().GetResult();
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task<PhonePacketHeader> Receive(UdpClient socket)
    {
        var result = await socket.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check(PhoneProtocol.TryRead(result.Buffer, out var header), "Server sent an invalid control packet.");
        return header;
    }
    private static async Task PairingAsync()
    {
        await using var server = new PhoneServer();server.Start(IPAddress.Loopback, 0);
        using var original = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));original.Connect(IPAddress.Loopback, server.Port);
        Guid id = Guid.NewGuid();string badCode = server.PairingCode == "000000" ? "111111" : "000000";
        byte[] Hello(string code, Guid session) => PhoneProtocol.Control(PhonePacketType.Hello, session, JsonSerializer.Serialize(new { id, name = "Pairing regression", code, latencyMs = 60, targetFrames = 2880 }));
        await original.SendAsync(Hello(badCode, Guid.Empty));
        Check((await Receive(original)).Type == PhonePacketType.Rejected && server.Peers.Count == 0, "An unpaired client was accepted.");
        await original.SendAsync(Hello(server.PairingCode, Guid.Empty));
        var welcome = await Receive(original);
        Check(welcome.Type == PhonePacketType.Welcome && welcome.Session != Guid.Empty, "Valid client did not receive a session.");
        PhonePeer peer = server.Peers.Single();
        await original.SendAsync(PhoneProtocol.Control(PhonePacketType.Heartbeat, welcome.Session, "{\"queueFrames\":\"bad\",\"targetFrames\":[],\"playing\":true}"));
        Check((await Receive(original)).Type == PhonePacketType.Heartbeat, "Malformed feedback stopped the receiver loop.");
        await original.SendAsync(PhoneProtocol.Control(PhonePacketType.Heartbeat, welcome.Session, "{\"queueFrames\":1800,\"targetFrames\":2880,\"playing\":true,\"underruns\":0}"));
        await Receive(original);
        Check(peer.RateCorrection > 0 && peer.ReceiverQueueFrames == 1800 && peer.ReceiverPlaying, "Receiver clock feedback was ignored.");
        using var resumed = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));resumed.Connect(IPAddress.Loopback, server.Port);
        await resumed.SendAsync(Hello(badCode, welcome.Session));
        var reconnect = await Receive(resumed);
        Check(reconnect.Type == PhonePacketType.Welcome && reconnect.Session == welcome.Session && server.Peers.Count == 1 && !peer.IsConnected, "Resume duplicated a device or lost the session.");
        PhonePeer current = server.Peers.Single();
        Check(!server.ForgetDisconnected(current.Id) && current.IsConnected, "History cleanup removed a connected phone.");
        current.SendStopped();Check((await Receive(resumed)).Type == PhonePacketType.Stopped, "Stop sharing notification failed.");
        await resumed.SendAsync(PhoneProtocol.Control(PhonePacketType.Goodbye, welcome.Session));
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (current.IsConnected && wait.ElapsedMilliseconds < 1000) await Task.Delay(10);
        Check(!current.IsConnected, "Phone disconnect was not reflected.");
        Check(server.ForgetDisconnected(current.Id) && server.Peers.Count == 0, "Disconnected phone history was not forgotten.");
        await resumed.SendAsync(Hello(badCode, welcome.Session));
        Check((await Receive(resumed)).Type == PhonePacketType.Rejected && server.Peers.Count == 0, "An old session resumed after its history was forgotten.");
        await resumed.SendAsync(Hello(server.PairingCode, Guid.Empty));
        var fresh = await Receive(resumed);
        Check(fresh.Type == PhonePacketType.Welcome && fresh.Session != welcome.Session && server.Peers.Single().ClientId == id,
            "A forgotten phone could not be discovered and paired again.");
    }
}
