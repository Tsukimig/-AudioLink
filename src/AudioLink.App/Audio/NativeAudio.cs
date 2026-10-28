using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioLink.Audio;

public readonly record struct NativeFormat(int SampleRate, int Channels, int Bits, bool IsFloat, uint ChannelMask)
{
    private static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    public int BlockAlign => Channels * Bits / 8;
    public string Description => $"{SampleRate / 1000d:0.#} kHz · {Bits}-bit{(IsFloat ? " float" : " PCM")} · {(Channels == 1 ? "单声道" : Channels == 2 ? "立体声" : $"{Channels} 声道")}";
    public static NativeFormat From(WaveFormat format)
    {
        bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat;
        uint mask = 0;
        if (format is WaveFormatExtensible extensible)
        {
            floating = extensible.SubFormat == IeeeFloat;
            if (!floating && extensible.SubFormat != Pcm) throw new NotSupportedException($"不支持的混音子格式：{extensible.SubFormat}");
            // NAudio 2.2.1 does not expose dwChannelMask. Its marshalled Windows
            // WAVEFORMATEXTENSIBLE layout puts this DWORD at byte offset 20.
            IntPtr native = Marshal.AllocHGlobal(Marshal.SizeOf(extensible));
            try { Marshal.StructureToPtr(extensible, native, false); mask = unchecked((uint)Marshal.ReadInt32(native, 20)); }
            finally { Marshal.FreeHGlobal(native); }
        }
        else if (format.Encoding is not (WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat))
            throw new NotSupportedException($"不支持的混音格式：{format.Encoding}");
        if (format.BitsPerSample is not (16 or 24 or 32) || (floating && format.BitsPerSample != 32))
            throw new NotSupportedException($"不支持此设备的音频格式：{format}");
        return new(format.SampleRate, format.Channels, format.BitsPerSample, floating, mask);
    }
}

internal static class AudioClock
{
    public static double Now100ns => Stopwatch.GetTimestamp() * (10_000_000d / Stopwatch.Frequency);
}

internal sealed class AudioThreadPriority : IDisposable
{
    private readonly IntPtr _handle;
    public AudioThreadPriority()
    {
        uint index = 0;
        _handle = AvSetMmThreadCharacteristics("Pro Audio", ref index);
        if (_handle != IntPtr.Zero) AvSetMmThreadPriority(_handle, 1);
    }
    public void Dispose() { if (_handle != IntPtr.Zero) AvRevertMmThreadCharacteristics(_handle); }
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvSetMmThreadPriority(IntPtr handle, int priority);
    [DllImport("avrt.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
