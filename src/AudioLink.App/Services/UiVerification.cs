using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioLink.Audio;
using AudioLink.ViewModels;

namespace AudioLink.Services;

/// <summary>Off-desktop UI integration check, including real bindings and audio controls.</summary>
internal static class UiVerification
{
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
    public static async Task<int> RunAsync(string[] args)
    {
        string path = Path.GetFullPath(args.ElementAtOrDefault(Array.IndexOf(args, "--verify-ui") + 1) ?? "ui-verification.json");
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string settingsPath = Path.Combine(AppLog.DataDirectory, "settings.json");
        string? originalSettings = File.Exists(settingsPath) ? await File.ReadAllTextAsync(settingsPath) : null;
        var checks = new List<string>(); var failures = new List<string>();
        var bindingErrors = new BindingErrors();
        var originalVolumes = new Dictionary<string, EndpointVolumeState>(StringComparer.Ordinal);
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        MainWindow? window = null;
        try
        {
            // Keep validation windows entirely outside the desktop. No error dialogs.
            window = new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -12000, Top = -12000, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            await WaitUntil(() => window.Model.Initialized, "UI initialization");
            window.UpdateLayout();
            await Task.Delay(150);
            Capture(window, Path.Combine(directory, "interface.png"));
            checks.Add("Actual device rows, source selector and read-only meters rendered without a binding exception.");
            MainViewModel model = window.Model;
            model.SourceSyncEnabled = false;
            int sourceIndex = Array.IndexOf(args, "--source");
            if (sourceIndex >= 0)
                model.SelectedSource = model.Sources.FirstOrDefault(x => x.Id == args.ElementAtOrDefault(sourceIndex + 1) && x.IsAvailable)
                    ?? throw new InvalidOperationException("Requested UI verification source is not connected.");
            foreach (OutputDeviceViewModel device in model.Outputs.Append(model.SourceOutput).OfType<OutputDeviceViewModel>().Where(x => x.Available).DistinctBy(x => x.Id))
            {
                using var controller = new EndpointVolumeController(device.Id);
                originalVolumes[device.Id] = controller.Read();
            }
            if (model.Outputs.FirstOrDefault(x => x.Available) is { } output)
            {
                foreach (OutputDeviceViewModel device in model.Outputs) device.Selected = false;
                output.Selected = true; output.Volume = 37; output.Muted = false; output.MuteCommand.Execute(null); output.DelayMs = 75;
                using (var controller = new EndpointVolumeController(output.Id))
                {
                    EndpointVolumeState state = controller.Read();
                    if (!output.UsesDeviceVolume || Math.Abs(state.Volume - 0.37) > 0.001 || !state.Muted || output.Controls.Volume != 1 || output.Controls.Muted || output.Controls.DelayMilliseconds != 75)
                        throw new InvalidOperationException("Device volume and route gain were not controlled independently.");
                    controller.SetVolume(0.24f);
                    await WaitUntil(() => Math.Abs(output.Volume - 24) < 0.05, "Windows volume notification updates slider");
                }
                output.Volume = originalVolumes[output.Id].Volume * 100;
                output.Muted = originalVolumes[output.Id].Muted;
                output.DelayMs = 0;
                checks.Add($"Output slider/mute control Windows device volume for {output.Name} ({output.Id}); external notifications update the slider, and stream gain is not applied twice.");
                if (model.SourceOutput is { UsesDeviceVolume: true } source)
                {
                    EndpointVolumeState original = originalVolumes[source.Id];
                    source.Volume = original.Volume * 50;
                    using (var controller = new EndpointVolumeController(source.Id))
                        if (Math.Abs(controller.Read().Volume - original.Volume / 2) > 0.001) throw new InvalidOperationException("Source volume slider did not update its device.");
                    source.Volume = original.Volume * 100;
                    checks.Add($"Source {source.Name} ({source.Id}) has its own functioning Windows volume slider.");
                }
                model.Profile = LatencyProfile.All[0];
                window.UpdateLayout();
                model.ToggleCommand.Execute(null);
                await WaitUntil(() => model.Running, "Start sharing from UI");
                output.Selected = false;
                await WaitUntil(() => !model.Running, "Remove last shared output");
                output.Selected = true;
                await WaitUntil(() => model.Running, "Reconnect shared output");
                model.ToggleCommand.Execute(null);
                await WaitUntil(() => !model.Running && !model.Busy, "Stop sharing from UI");
                checks.Add("Minimum-latency profile, start, remove a target, reconnect and stop succeed through UI commands.");
            }
            if (args.Contains("--check-source-sync"))
            {
                Dictionary<NAudio.CoreAudioApi.Role, string> defaults = DefaultPlaybackLease.ReadDefaults();
                EndpointInfo originalSource = model.Sources.First(x => x.IsAvailable && !x.IsVirtual);
                model.SelectedSource = originalSource;
                EndpointInfo bus = model.SyncInputs.FirstOrDefault(x => x.IsAvailable) ?? throw new InvalidOperationException("Source sync test needs an installed virtual endpoint.");
                foreach (OutputDeviceViewModel device in model.Outputs) device.Selected = false;
                model.SyncInput = bus;model.SourceSyncEnabled = true;
                OutputDeviceViewModel source = model.SourceOutput ?? throw new InvalidOperationException("Source device is missing.");
                source.DelayMs = 75;
                window.UpdateLayout();
                ComboBox busCombo = Descendants(window).OfType<ComboBox>().Single(x => AutomationProperties.GetName(x) == "虚拟中转音源");
                if (!Descendants(busCombo).OfType<TextBlock>().Any(x => x.Text == bus.DisplayName)) throw new InvalidOperationException("Virtual bus selector did not display the device name.");
                TextBox delayBox = Descendants(window).OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == "音源追加延迟毫秒");
                delayBox.Text = "95";delayBox.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                if (source.DelayMs != 95 || source.Controls.DelayMilliseconds != 95) throw new InvalidOperationException("Source delay UI does not update its render route.");
                model.ToggleCommand.Execute(null);
                await WaitUntil(() => model.Running && !model.Busy, "Start synchronized source playback");
                if (!model.RoutingStatistics.Outputs.Any(x => x.DeviceId == originalSource.Id)) throw new InvalidOperationException("Original source was not rendered from the virtual bus.");
                Dictionary<NAudio.CoreAudioApi.Role, string> activeDefaults = DefaultPlaybackLease.ReadDefaults();
                if (activeDefaults[NAudio.CoreAudioApi.Role.Multimedia] != bus.Id || activeDefaults[NAudio.CoreAudioApi.Role.Console] != bus.Id)
                    throw new InvalidOperationException("System audio did not switch to the chosen virtual bus.");
                if (defaults.TryGetValue(NAudio.CoreAudioApi.Role.Communications, out string? communication) && activeDefaults[NAudio.CoreAudioApi.Role.Communications] != communication)
                    throw new InvalidOperationException("Source sync changed the communications endpoint.");
                DefaultPlaybackLease.RecoverPrevious();
                if (DefaultPlaybackLease.ReadDefaults()[NAudio.CoreAudioApi.Role.Multimedia] != bus.Id) throw new InvalidOperationException("Recovery interrupted a live source-sync session.");
                await Task.Delay(250);window.UpdateLayout();
                Capture(window, Path.Combine(directory, "interface-source-sync.png"));
                model.ToggleCommand.Execute(null);
                await WaitUntil(() => !model.Running && !model.Busy, "Stop synchronized source playback");
                Dictionary<NAudio.CoreAudioApi.Role, string> restoredDefaults = DefaultPlaybackLease.ReadDefaults();
                if (defaults.Any(x => !restoredDefaults.TryGetValue(x.Key, out string? value) || value != x.Value)) throw new InvalidOperationException("System playback defaults were not restored.");
                if (File.Exists(Path.Combine(AppLog.DataDirectory, "default-playback-lease.json"))) throw new InvalidOperationException("Playback restoration journal was not cleared.");
                model.SourceSyncEnabled = false;source.DelayMs = 0;
                checks.Add("Source delay textbox updates real render controls; virtual-bus playback includes the original device; system defaults and restoration journal recover on stop.");
            }
            var actual = model.Outputs.ToArray();
            for (int i = 0; i < 7; i++)
            {
                var fixture = new EndpointInfo($"ui-fixture-{i}", $"多设备布局验证 {i + 1}", new(48000, 2, 32, true, 3), false);
                model.Outputs.Add(new(fixture, null, 100, _ => { }));
            }
            window.Width = 1000; window.Height = 700; window.UpdateLayout();
            ListBox list = Descendants(window).OfType<ListBox>().Single();
            list.ScrollIntoView(model.Outputs[^1]);
            await Task.Delay(150); window.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromItem(model.Outputs[^1]) is not FrameworkElement last || !last.IsVisible) throw new InvalidOperationException("Multiple-device list did not scroll to the last device.");
            checks.Add("Seven additional output rows virtualize and scroll at 1000 x 700.");
            Capture(window, Path.Combine(directory, "interface-small-multiple.png"));
            foreach (OutputDeviceViewModel fixture in model.Outputs.Except(actual).ToArray()) model.Outputs.Remove(fixture);
            window.Width = 1160; window.Height = 820; window.UpdateLayout();
            var help = new HelpWindow { Owner = window, WindowStartupLocation = WindowStartupLocation.Manual, Left = -12000, Top = -12000, ShowActivated = false, ShowInTaskbar = false };
            help.Show(); help.UpdateLayout(); Capture(help, Path.Combine(directory, "help.png")); help.Close();
            checks.Add("Help dialog renders.");
        }
        catch (Exception ex) { failures.Add(ex.ToString()); AppLog.Write("后台界面验证", ex); }
        finally
        {
            if (window is not null) { await window.Model.ShutdownAsync(); window.Close(); }
            foreach ((string id, EndpointVolumeState state) in originalVolumes)
            {
                try
                {
                    using var controller = new EndpointVolumeController(id);
                    controller.Restore(state);
                    EndpointVolumeState restored = controller.Read();
                    if (Math.Abs(restored.Volume - state.Volume) > 0.001 || restored.Muted != state.Muted)
                        failures.Add($"Restore device volume {id}: readback did not match the original state.");
                }
                catch (Exception ex) { failures.Add($"Restore device volume {id}: {ex.Message}"); }
            }
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
            if (originalSettings is not null) await File.WriteAllTextAsync(settingsPath, originalSettings);
            else if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
        failures.AddRange(bindingErrors.Errors);
        bool passed = failures.Count == 0;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Passed = passed, Checks = checks, Errors = failures }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
    private static async Task WaitUntil(Func<bool> predicate, string name)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.Elapsed.TotalSeconds > 10) throw new TimeoutException(name); await Task.Delay(50); }
    }
    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path); png.Save(stream);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (DependencyObject descendant in Descendants(child)) yield return descendant;
        }
    }
}
