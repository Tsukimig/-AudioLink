using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace AudioLink.Audio;

/// <summary>Owns an initialized client. Low latency uses the driver's legal shared-mode periods.</summary>
internal sealed class SharedAudioStream : IDisposable
{
    public AudioClient Client { get; }
    public WaveFormat WaveFormat { get; }
    public double PeriodMilliseconds { get; }
    public string Backend { get; }

    private SharedAudioStream(AudioClient client, WaveFormat format, double period, string backend)
    { Client = client; WaveFormat = format; PeriodMilliseconds = period; Backend = backend; }

    public static SharedAudioStream OpenRender(MMDevice device, int bufferMs, bool preferMinimumPeriod)
    {
        if (preferMinimumPeriod)
        {
            try { return OpenMinimumPeriod(device.ID); }
            catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
            { Services.AppLog.Write($"设备不接受最小共享周期，使用兼容模式：{device.FriendlyName}", ex); }
        }
        AudioClient client = device.AudioClient;
        try
        {
            WaveFormat format = client.MixFormat;
            client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback,
                preferMinimumPeriod ? 0 : bufferMs * 10_000L, 0, format, Guid.Empty);
            return new(client, format, client.DefaultDevicePeriod / 10_000d, preferMinimumPeriod ? "共享最小缓冲" : "WASAPI");
        }
        catch { client.Dispose(); throw; }
    }

    private static SharedAudioStream OpenMinimumPeriod(string id)
    {
        Type enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), throwOnError: true)!;
        var enumerator = (IDeviceEnumeratorNative)Activator.CreateInstance(enumeratorType)!;
        IDeviceNative? device = null;
        object? raw = null;
        AudioClient? client = null;
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(id, out device));
            Guid iid = typeof(IAudioClient3Native).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out raw));
            var modern = (IAudioClient3Native)raw;
            client = new((IAudioClient)raw);
            WaveFormat format = client.MixFormat;
            Marshal.ThrowExceptionForHR(modern.GetSharedModeEnginePeriod(format, out uint normal, out uint fundamental, out uint minimum, out uint maximum));
            if (fundamental == 0 || minimum == 0 || maximum < minimum) throw new NotSupportedException("驱动未提供有效的共享周期。");
            uint period = checked((minimum + fundamental - 1) / fundamental * fundamental);
            if (period > maximum) period = normal;
            int hr = modern.InitializeSharedAudioStream(AudioClientStreamFlags.EventCallback, period, format, IntPtr.Zero);
            if (hr < 0)
            {
                // Other shared clients may have locked a legal engine period. Re-open
                // at that period, without interrupting or changing those clients.
                int query = modern.GetCurrentSharedModeEnginePeriod(out IntPtr currentFormat, out uint currentPeriod);
                if (currentFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(currentFormat);
                if (query >= 0 && currentPeriod >= minimum && currentPeriod <= maximum && currentPeriod % fundamental == 0)
                {
                    period = currentPeriod;
                    hr = modern.InitializeSharedAudioStream(AudioClientStreamFlags.EventCallback, period, format, IntPtr.Zero);
                }
            }
            Marshal.ThrowExceptionForHR(hr);
            var result = new SharedAudioStream(client, format, period * 1000d / format.SampleRate, "WASAPI 3");
            client = null; raw = null; // Ownership transfers to the NAudio wrapper.
            return result;
        }
        catch
        {
            if (client is not null) { client.Dispose(); raw = null; }
            if (raw is not null) Marshal.ReleaseComObject(raw);
            throw;
        }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public void Dispose() => Client.Dispose();

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceEnumeratorNative
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint states, out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDeviceNative device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDeviceNative device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceNative
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    // All inherited slots are declared in Windows SDK order. PreserveSig keeps
    // HRESULTs explicit, including S_FALSE and engine-period locking failures.
    [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient3Native
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal, out long minimum);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        [PreserveSig] int IsOffloadCapable(int category, [MarshalAs(UnmanagedType.Bool)] out bool capable);
        [PreserveSig] int SetClientProperties(IntPtr properties);
        [PreserveSig] int GetBufferSizeLimits(IntPtr format, [MarshalAs(UnmanagedType.Bool)] bool eventDriven, out long minimum, out long maximum);
        [PreserveSig] int GetSharedModeEnginePeriod([In] WaveFormat format, out uint normal, out uint fundamental, out uint minimum, out uint maximum);
        [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint period);
        [PreserveSig] int InitializeSharedAudioStream(AudioClientStreamFlags flags, uint period, [In] WaveFormat format, IntPtr session);
    }
}
