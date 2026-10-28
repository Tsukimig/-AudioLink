using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using AudioLink.Network;

namespace AudioLink;

public partial class PhoneConnectionWindow : Window
{
    private sealed record AddressItem(string Label, bool Usb);
    private readonly PhoneServer _server;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    public PhoneConnectionWindow(PhoneServer server)
    {
        InitializeComponent(); _server = server;
        _timer.Tick += (_, _) => UpdateStatus();
        Closed += (_, _) => _timer.Stop();
    }
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try { _server.Start(); _server.RenewPairingCode(); RefreshAddresses(); UpdateStatus(); _timer.Start(); }
        catch (Exception ex) when (ex is SocketException or NetworkInformationException)
        { StatusText.Text = $"暂时无法开启手机连接：{ex.Message}"; RenewButton.IsEnabled = false; }
    }
    private void RefreshAddresses()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(x => new AddressItem(x.Address.ToString(), adapter.Description.Contains("USB", StringComparison.OrdinalIgnoreCase) || adapter.Description.Contains("RNDIS", StringComparison.OrdinalIgnoreCase) || adapter.Description.Contains("NDIS based", StringComparison.OrdinalIgnoreCase))))
            .DistinctBy(x => x.Label).OrderByDescending(x => x.Usb).ToArray();
        AddressBox.ItemsSource = addresses;
        if (addresses.Length > 0) AddressBox.SelectedIndex = 0;
    }
    private void UpdateStatus()
    {
        CodeBox.Text = _server.PairingCode;
        int connected = _server.Peers.Count(x => x.IsConnected);
        StatusText.Text = AddressBox.Items.Count == 0 ? "开启手机的 USB 网络共享后，点击刷新。" :
            connected > 0 ? "手机已连接" :
            _server.PairingSeconds > 0 ? "连接码有效期 3 分钟" : "连接码已过期，请换一个";
    }
    private void Refresh_Click(object sender, RoutedEventArgs e)
    { try { RefreshAddresses(); UpdateStatus(); } catch (NetworkInformationException ex) { StatusText.Text = ex.Message; } }
    private void Renew_Click(object sender, RoutedEventArgs e) { _server.RenewPairingCode(); UpdateStatus(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Apk_Click(object sender, RoutedEventArgs e)
    {
        string apk = Path.Combine(AppContext.BaseDirectory, "Android", "AudioLink-Android.apk");
        if (!File.Exists(apk)) { StatusText.Text = "安卓安装包位于发布包的 Android 目录。"; return; }
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add("/select,"); start.ArgumentList.Add(apk);
        try { Process.Start(start)?.Dispose(); } catch (System.ComponentModel.Win32Exception ex) { StatusText.Text = ex.Message; }
    }
}
