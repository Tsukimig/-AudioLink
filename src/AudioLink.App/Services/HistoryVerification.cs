using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using AudioLink.Audio;
using AudioLink.ViewModels;
using AudioLink.Network;

namespace AudioLink.Services;

/// <summary>Exercise real history commands against an isolated settings file, without opening a window or starting audio.</summary>
internal static class HistoryVerification
{
    public static async Task<int> RunAsync(string[] args)
    {
        string report = Path.GetFullPath(args.ElementAtOrDefault(Array.IndexOf(args, "--verify-history") + 1) ?? "history-verification.json");
        string directory = Path.Combine(Path.GetDirectoryName(report)!, "history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new SettingsStore(Path.Combine(directory, "settings.json"));
        string native = "history-native-" + Guid.NewGuid().ToString("N");
        string phone = "android:" + Guid.NewGuid().ToString("N");
        string hidden = "history-hidden-" + Guid.NewGuid().ToString("N");
        string source = "history-source-" + Guid.NewGuid().ToString("N");
        string bus = "history-bus-" + Guid.NewGuid().ToString("N");
        DevicePreference Preference(string id, string name, bool selected) => new()
            { Id = id, Name = name, Selected = selected, Volume = 37, Muted = true, DelayMs = 75, Format = new(48000, 2, 32, true, 3) };
        store.Save(new() { SourceId = source, SyncInputId = bus, Devices =
            [Preference(native, "历史扬声器", true), Preference(phone, "历史手机", true), Preference(hidden, "未选择的历史设备", false),
             Preference(source, "当前配置的断线音源", false), Preference(bus, "当前配置的虚拟中转设备", false)] });
        var checks = new List<string>();var failures = new List<string>();
        MainViewModel? model = null;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        async Task Refresh(MainViewModel current)
        {
            current.RefreshCommand.Execute(null);
            var timeout = Stopwatch.StartNew();
            while (!current.RefreshCommand.CanExecute(null))
            { if (timeout.Elapsed.TotalSeconds > 8) throw new TimeoutException("Read-only device refresh timed out.");await Task.Delay(20); }
        }
        async Task WaitUntil(Func<bool> ready)
        {
            var timeout = Stopwatch.StartNew();
            while (!ready()) { if (timeout.Elapsed.TotalSeconds > 4) throw new TimeoutException("Phone history state did not update.");await Task.Delay(20); }
        }
        void DeleteSelected(MainViewModel current, params string[] ids)
        {
            current.ClearHistoryCommand.Execute(null);
            Check(current.IsClearingHistory && !current.ConfirmHistoryCommand.CanExecute(null), "History selection did not start empty.");
            foreach (string id in ids)
            {
                var row = current.HistoryDevices.Single(x => x.Id == id);
                Check(row.CanDelete, "Disconnected history could not be selected.");
                row.IsSelected = true;
            }
            Check(current.ConfirmHistoryCommand.CanExecute(null), "Selected history could not be confirmed.");
            current.ConfirmHistoryCommand.Execute(null);
            Check(!current.IsClearingHistory, "Confirmed deletion did not leave selection mode.");
        }
        try
        {
            model = new(Dispatcher.CurrentDispatcher, store);
            // Deliberately use Refresh, not Initialize: no default-device
            // recovery, device monitor, playback workers, or window is started.
            await Refresh(model);
            OutputDeviceViewModel old = model.Outputs.Single(x => x.Id == native);
            model.ClearHistoryCommand.Execute(null);
            Check(model.HistoryDevices.Any(x => x.Id == hidden && x.CanDelete), "Hidden unselected history was not listed.");
            model.HistoryDevices.Single(x => x.Id == hidden).IsSelected = true;
            Check(!model.Outputs.Any(x => x.Id == hidden) && old.Selected, "History selection changed playback selection.");
            model.CancelHistoryCommand.Execute(null);
            Check(store.Load().Devices.Any(x => x.Id == hidden), "Cancel deleted a saved device.");
            DeleteSelected(model, native);
            Check(model.Outputs.All(x => x.Id != native) && store.Load().Devices.All(x => x.Id != native), "Single-device history was not removed from both UI and disk.");
            Check(store.Load().Devices.Any(x => x.Id == hidden), "Confirmation deleted unchecked history.");
            checks.Add("History mode includes hidden records; independent checkboxes, cancellation and confirmation preserve unchecked records and playback selection.");

            MainViewModel currentModel = model;
            model.Phones.Start(IPAddress.Loopback, 0);
            using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));receiver.Connect(IPAddress.Loopback, model.Phones.Port);
            async Task<Guid> Pair()
            {
                await receiver.SendAsync(PhoneProtocol.Control(PhonePacketType.Hello, Guid.Empty,
                    JsonSerializer.Serialize(new { id = Guid.ParseExact(phone[8..], "N"), name = "History reconnect check", code = currentModel.Phones.PairingCode })));
                var welcome = await receiver.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Check(PhoneProtocol.TryRead(welcome.Buffer, out var header) && header.Type == PhonePacketType.Welcome, "Phone could not pair for history verification.");
                await WaitUntil(() => currentModel.Outputs.Any(x => x.Id == phone && x.Available));
                return header.Session;
            }
            async Task Disconnect(Guid session)
            {
                await receiver.SendAsync(PhoneProtocol.Control(PhonePacketType.Goodbye, session));
                await WaitUntil(() => currentModel.Outputs.Any(x => x.Id == phone && !x.Available));
            }
            Guid originalSession = await Pair();
            model.ClearHistoryCommand.Execute(null);
            Check(model.HistoryDevices.All(x => x.Id != phone), "Connected phone was offered for history deletion.");
            model.CancelHistoryCommand.Execute(null);
            await Disconnect(originalSession);
            DeleteSelected(model, phone);
            Check(model.Phones.Peers.All(x => x.Id != phone), "Disconnected phone cache survived history deletion.");
            await Refresh(model);
            Check(model.Outputs.All(x => x.Id != phone), "Deleted phone cache recreated the history row.");
            Guid newSession = await Pair();
            OutputDeviceViewModel freshPhone = model.Outputs.Single(x => x.Id == phone);
            Check(newSession != originalSession && freshPhone.Volume == 100 && !freshPhone.Muted && freshPhone.DelayMs == 0 && freshPhone.Selected,
                "Reconnected phone recovered deleted settings instead of fresh defaults.");
            await Disconnect(newSession);
            checks.Add("A real loopback-paired phone is forgotten from the server cache, stays absent on refresh, and reconnects with fresh volume / mute / delay defaults.");

            model.ClearHistoryCommand.Execute(null);
            Check(model.HistoryDevices.All(x => !x.Device.Available), "Connected device appeared in history selection.");
            foreach (var protectedRow in model.HistoryDevices.Where(x => x.Id == source || x.Id == bus))
            {
                Check(!protectedRow.CanDelete, "Current source or bus was not protected.");
                protectedRow.IsSelected = true;
                Check(!protectedRow.IsSelected, "A protected device was selected for deletion.");
            }
            model.SelectHistoryCommand.Execute(null);
            model.ConfirmHistoryCommand.Execute(null);
            UserSettings cleared = store.Load();
            Check(new[] { native, phone, hidden }.All(id => cleared.Devices.All(x => x.Id != id)), "Bulk cleanup retained a disconnected phone or hidden old preference.");
            Check(cleared.SourceId == source && cleared.SyncInputId == bus && cleared.Devices.Any(x => x.Id == source) && cleared.Devices.Any(x => x.Id == bus), "Cleanup changed the configured source / bus.");
            model.ClearHistoryCommand.Execute(null);
            Check(!model.ConfirmHistoryCommand.CanExecute(null) && !model.SelectHistoryCommand.CanExecute(null), "Cleanup still offered protected devices for deletion.");
            model.CancelHistoryCommand.Execute(null);
            checks.Add("Select-all and confirmation remove eligible history while protecting connected devices and the configured source / bus.");

            await Refresh(model);
            Check(model.Outputs.All(x => x.Id != native && x.Id != phone && x.Id != hidden), "Deleted records reappeared after device refresh.");
            await model.ShutdownAsync();model = null;
            model = new(Dispatcher.CurrentDispatcher, store);
            await Refresh(model);
            Check(model.Outputs.All(x => x.Id != native && x.Id != phone && x.Id != hidden), "Deleted records reappeared after reloading saved settings.");
            checks.Add("Deleted history stays absent across refresh and a new view-model instance loading the same settings file.");
            checks.Add("No window, audio playback, system volume write, default-device change or user settings write was performed.");
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally { if (model is not null) await model.ShutdownAsync(); }
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = failures.Count == 0, Checks = checks, Errors = failures }, new JsonSerializerOptions { WriteIndented = true }));
        return failures.Count == 0 ? 0 : 1;
    }
}
