using System.Text.Json;
using AudioLink.Audio;

namespace AudioLink.Services;

public sealed class DevicePreference
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public NativeFormat Format { get; set; }
    public bool Selected { get; set; }
    public double Volume { get; set; } = 100;
    public bool Muted { get; set; }
    public int DelayMs { get; set; }
}
public sealed class UserSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string? SourceId { get; set; }
    public bool SourceSyncEnabled { get; set; }
    public string? SyncInputId { get; set; }
    public string ProfileId { get; set; } = "balanced";
    public double MasterVolume { get; set; } = 100;
    public List<DevicePreference> Devices { get; set; } = [];
}
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public SettingsStore(string? path = null) => _path = path ?? Path.Combine(AppLog.DataDirectory, "settings.json");
    public UserSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            UserSettings value = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path)) ?? new();
            value.Devices ??= [];
            value.MasterVolume = double.IsFinite(value.MasterVolume) ? Math.Clamp(value.MasterVolume, 0, 100) : 100;
            value.ProfileId = LatencyProfile.All.Any(x => x.Id == value.ProfileId) ? value.ProfileId : "balanced";
            value.Devices = value.Devices.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Id)).DistinctBy(x => x.Id).Take(100).ToList();
            foreach (DevicePreference device in value.Devices)
            {
                device.Volume = double.IsFinite(device.Volume) ? Math.Clamp(device.Volume, 0, 100) : 100;
                device.DelayMs = Math.Clamp(device.DelayMs, 0, 500);
            }
            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Write("设置无法读取，使用默认值", ex);
            return new();
        }
    }
    public void Save(UserSettings settings)
    {
        lock (_gate)
        {
            string directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
