using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AudioLink;
public partial class HelpWindow : Window
{
    public HelpWindow() => InitializeComponent();
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int dark = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
