using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AudioLink.Services;
using NAudio.CoreAudioApi;

namespace AudioLink.Audio;

/// <summary>Temporarily use a virtual capture bus; restore only defaults we still own.</summary>
internal sealed class DefaultPlaybackLease : IDisposable
{
    private sealed record Journal(int ProcessId, long StartedUtcTicks, string BusId, Dictionary<Role, string> Defaults);
    private static readonly string JournalPath = Path.Combine(AppLog.DataDirectory, "default-playback-lease.json");
    private readonly Journal _journal;
    private bool _disposed;
    public string BusId => _journal.BusId;
    private DefaultPlaybackLease(Journal journal) => _journal = journal;
    public static DefaultPlaybackLease Acquire(EndpointInfo bus)
    {
        if (!bus.IsAvailable || !bus.IsVirtual || bus.IsPhone) throw new InvalidOperationException("音源同步需要独立的虚拟中转播放设备。");
        RecoverPrevious();
        if (File.Exists(JournalPath)) throw new InvalidOperationException("已有程序正在使用音源同步，请先停止另一实例的共享。");
        using var process = Process.GetCurrentProcess();
        var journal = new Journal(process.Id, process.StartTime.ToUniversalTime().Ticks, bus.Id, ReadDefaults());
        Directory.CreateDirectory(AppLog.DataDirectory);
        using (var stream = new FileStream(JournalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) JsonSerializer.Serialize(stream, journal);
        var lease = new DefaultPlaybackLease(journal);
        try
        {
            // Leave the communications role alone so calls retain their routing.
            SetDefault(bus.Id, Role.Console);
            SetDefault(bus.Id, Role.Multimedia);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    internal static Dictionary<Role, string> ReadDefaults()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaults = new Dictionary<Role, string>();
        foreach (Role role in Enum.GetValues<Role>())
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Render, role))
            { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role); defaults[role] = device.ID; }
        return defaults;
    }
    public static void RecoverPrevious()
    {
        if (!File.Exists(JournalPath)) return;
        Journal journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(JournalPath)) ?? throw new IOException("无法读取音源同步恢复记录。");
        try
        {
            using var process = Process.GetProcessById(journal.ProcessId);
            if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == journal.StartedUtcTicks) return;
        }
        catch (ArgumentException) { }
        Restore(journal);
    }
    private static void Restore(Journal journal)
    {
        Dictionary<Role, string> current = ReadDefaults();
        using var enumerator = new MMDeviceEnumerator();
        foreach ((Role role, string previous) in journal.Defaults)
        {
            if (!current.TryGetValue(role, out string? active) || active != journal.BusId || previous == journal.BusId) continue;
            string? target = null;
            try { using var device = enumerator.GetDevice(previous); if (device.State == DeviceState.Active) target = previous; }
            catch (COMException) { }
            // A disconnected original must not leave Windows on an unused bus.
            target ??= DeviceCatalog.Enumerate().FirstOrDefault(x => !x.IsVirtual && x.IsAvailable)?.Id;
            if (target is null) throw new IOException("原播放设备已断开，请在 Windows 中选择一个可用输出后重试。");
            SetDefault(target, role);
        }
        File.Delete(JournalPath);
    }
    public void Dispose()
    {
        if (_disposed) return;
        Restore(_journal);
        _disposed = true;
    }
    // Windows audio policy COM ABI; used only for default render endpoints.
    // Unsupported systems report a recoverable error rather than starting a loop.
    private static void SetDefault(string id, Role role)
    {
        object client = Activator.CreateInstance(Type.GetTypeFromCLSID(new("870af99c-171d-4f9e-af0d-e63df40c2bc9"), true)!)!;
        try { Marshal.ThrowExceptionForHR(((IPolicyConfig)client).SetDefaultEndpoint(id, role)); }
        finally { Marshal.ReleaseComObject(client); }
    }
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int mode, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpoint, IntPtr mix);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int mode, out long current, out long minimum);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
    }
}
