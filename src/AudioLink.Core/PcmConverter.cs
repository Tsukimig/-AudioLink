using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace AudioLink.Core;

public static class PcmConverter
{
    public static void Decode(ReadOnlySpan<byte> input, Span<float> output, int bits, bool ieeeFloat)
    {
        int bytes = bits / 8;
        if (ieeeFloat && bits == 32)
        {
            MemoryMarshal.Cast<byte, float>(input).CopyTo(output);
            for (int i = 0; i < output.Length; i++) if (!float.IsFinite(output[i])) output[i] = 0;
            return;
        }
        if (bits is not (16 or 24 or 32) || ieeeFloat) throw new NotSupportedException($"Unsupported PCM: {bits} bits, float={ieeeFloat}");
        for (int i = 0, p = 0; i < output.Length; i++, p += bytes)
        {
            output[i] = bits switch
            {
                16 => BinaryPrimitives.ReadInt16LittleEndian(input[p..]) / 32768f,
                24 => ((input[p] | (input[p + 1] << 8) | (input[p + 2] << 16)) << 8 >> 8) / 8388608f,
                _ => BinaryPrimitives.ReadInt32LittleEndian(input[p..]) / 2147483648f
            };
        }
    }

    public static void Encode(ReadOnlySpan<float> input, Span<byte> output, int bits, bool ieeeFloat)
    {
        if (ieeeFloat && bits == 32)
        {
            MemoryMarshal.AsBytes(input).CopyTo(output);
            return;
        }
        if (bits is not (16 or 24 or 32) || ieeeFloat) throw new NotSupportedException($"Unsupported PCM: {bits} bits, float={ieeeFloat}");
        int bytes = bits / 8;
        for (int i = 0, p = 0; i < input.Length; i++, p += bytes)
        {
            double sample = float.IsFinite(input[i]) ? Math.Clamp(input[i], -1, 1) : 0;
            switch (bits)
            {
                case 16:
                    BinaryPrimitives.WriteInt16LittleEndian(output[p..], (short)Math.Clamp(Math.Round(sample * 32768), -32768, 32767));
                    break;
                case 24:
                    int s24 = (int)Math.Clamp(Math.Round(sample * 8388608), -8388608, 8388607);
                    output[p] = (byte)s24; output[p + 1] = (byte)(s24 >> 8); output[p + 2] = (byte)(s24 >> 16);
                    break;
                default:
                    BinaryPrimitives.WriteInt32LittleEndian(output[p..], (int)Math.Clamp(Math.Round(sample * 2147483648d), int.MinValue, int.MaxValue));
                    break;
            }
        }
    }
}
