using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using AudioLink.ViewModels;

namespace AudioLink;

public partial class MainWindow : Window
{
    private readonly MainViewModel _model;
    private bool _allowClose;
    private bool _closePending;
    private PhoneConnectionWindow? _phoneWindow;
    internal MainViewModel Model => _model;
    public MainWindow()
    {
        InitializeComponent();
        Title = $"同频 AudioLink {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        _model = new(Dispatcher);
        DataContext = _model;
        _model.WaveformTick += () => SignalView.Push(_model.SourcePeak);
        _model.HelpRequested += ShowHelp;
        _model.PhoneRequested += ShowPhoneConnection;
        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsClearingHistory))
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
                {
                    if (IsActive) (_model.IsClearingHistory ? CancelHistoryButton : ClearHistoryButton).Focus();
                }));
        };
        // Fit the usable desktop on displays with larger text / DPI scaling.
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 30);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 30);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int dark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) => await _model.InitializeAsync();
    private void WindowScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Bound the inner lists while keeping the entire page reachable on a small work area.
        if (RootLayout is null) return;
        RootLayout.Width = Math.Max(RootLayout.MinWidth, e.NewSize.Width - RootLayout.Margin.Left - RootLayout.Margin.Right - SystemParameters.VerticalScrollBarWidth);
        RootLayout.Height = Math.Max(RootLayout.MinHeight, e.NewSize.Height - RootLayout.Margin.Top - RootLayout.Margin.Bottom - SystemParameters.HorizontalScrollBarHeight);
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closePending) return;
        _closePending = true;
        IsEnabled = false;
        try { await _model.ShutdownAsync(); }
        catch (Exception ex) { Services.AppLog.Write("关闭音频引擎", ex); }
        _allowClose = true;
        // Closing can complete synchronously when idle. Queue the final Close
        // after WPF has finished the original cancellable Closing event.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    private void DelayBox_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = e.Text.Any(c => c is < '0' or > '9');
    private void DelayBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not OutputDeviceViewModel device) return;
        if (int.TryParse(box.Text, out int delay)) device.DelayMs = Math.Clamp(delay, 0, 500);
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }
    private void ShowHelp() => new HelpWindow { Owner = this }.ShowDialog();
    private void ShowPhoneConnection()
    {
        if (_phoneWindow is not null) { _phoneWindow.Activate(); return; }
        _phoneWindow = new PhoneConnectionWindow(_model.Phones) { Owner = this };
        _phoneWindow.Closed += (_, _) => _phoneWindow = null;
        _phoneWindow.Show();
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
