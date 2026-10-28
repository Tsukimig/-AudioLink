using System.Buffers.Binary;
using System.Text;

namespace AudioLink.Network;

public enum PhonePacketType : byte { Hello = 1, Welcome = 2, Heartbeat = 3, Goodbye = 4, Audio = 5, Stopped = 6, Rejected = 7 }
public readonly record struct PhonePacketHeader(PhonePacketType Type, Guid Session, uint Sequence, ulong FramePosition, ushort Frames, ushort Flags);

/// <summary>Small, versioned UDP packets. Audio never exceeds a normal Ethernet MTU.</summary>
public static class PhoneProtocol
{
    public const int Port = 45891;
    public const int HeaderSize = 40;
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FramesPerPacket = 240;
    public const int AudioBytes = FramesPerPacket * Channels * 2;
    public const int MaximumControlBytes = 768;

    public static byte[] Control(PhonePacketType type, Guid session, string json = "")
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaximumControlBytes) throw new ArgumentException("Control message too large.", nameof(json));
        var packet = new byte[HeaderSize + payload.Length];
        WriteHeader(packet, new(type, session, 0, 0, 0, 0));
        payload.CopyTo(packet, HeaderSize);
        return packet;
    }
    public static void WriteHeader(Span<byte> packet, PhonePacketHeader header)
    {
        if (packet.Length < HeaderSize) throw new ArgumentException("Header is incomplete.", nameof(packet));
        "ALNK"u8.CopyTo(packet);
        packet[4] = 1; packet[5] = (byte)header.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(packet[6..], HeaderSize);
        header.Session.TryWriteBytes(packet[8..24]);
        BinaryPrimitives.WriteUInt32LittleEndian(packet[24..], header.Sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(packet[28..], header.FramePosition);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[36..], header.Frames);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[38..], header.Flags);
    }
    public static bool TryRead(ReadOnlySpan<byte> packet, out PhonePacketHeader header)
    {
        header = default;
        if (packet.Length < HeaderSize || !packet[..4].SequenceEqual("ALNK"u8) || packet[4] != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]) != HeaderSize || packet[5] is < 1 or > 7) return false;
        var type = (PhonePacketType)packet[5];
        ushort frames = BinaryPrimitives.ReadUInt16LittleEndian(packet[36..]);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(packet[38..]);
        if ((flags & ~1) != 0) return false;
        if (type == PhonePacketType.Audio)
        {
            if (frames != FramesPerPacket || packet.Length != HeaderSize + AudioBytes) return false;
        }
        else if (frames != 0 || flags != 0 || packet.Length > HeaderSize + MaximumControlBytes) return false;
        header = new(type, new Guid(packet[8..24]), BinaryPrimitives.ReadUInt32LittleEndian(packet[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(packet[28..]), frames, flags);
        return true;
    }
}
