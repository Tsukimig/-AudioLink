using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AudioLink.Audio;

/// <summary>Audio packets need 5 ms pacing, independent of the default Windows timer tick.</summary>
internal sealed class AudioPacketTimer : WaitHandle
{
    private readonly bool _periodRequested;
    private int _disposed;
    public AudioPacketTimer()
    {
        SafeWaitHandle handle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x1F0003);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            handle = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x1F0003);
            _periodRequested = TimeBeginPeriod(1) == 0;
        }
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        SafeWaitHandle = handle;
    }
    public void Arm(double remaining100ns)
    {
        long due = -Math.Max(1, (long)Math.Ceiling(remaining100ns));
        if (!SetWaitableTimer(SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    protected override void Dispose(bool explicitDisposing)
    {
        if (explicitDisposing && Interlocked.Exchange(ref _disposed, 1) == 0 && _periodRequested) _ = TimeEndPeriod(1);
        base.Dispose(explicitDisposing);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr callback, IntPtr context, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint period);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint period);
}
