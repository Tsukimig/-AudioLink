using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using AudioLink.Services;

namespace AudioLink;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private int _fatalErrorReported;
    private bool _diagnosticMode;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _diagnosticMode = e.Args.Any(x => x is "--diagnostics" or "--smoke" or "--probe" or "--verify-ui" or "--verify-phone" or "--verify-history");
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (Interlocked.Exchange(ref _fatalErrorReported, 1) != 0) return;
            AppLog.Write("界面异常，停止程序", args.Exception);
            if (!_diagnosticMode) MessageBox.Show($"程序遇到异常，将停止运行：{args.Exception.Message}\n\n详细日志位于：{AppLog.DataDirectory}", "同频 AudioLink", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Write("进程异常", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { AppLog.Write("后台任务异常", args.Exception); args.SetObserved(); };
        if (_diagnosticMode)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code = e.Args.Contains("--verify-history") ? await HistoryVerification.RunAsync(e.Args) : e.Args.Contains("--verify-phone") ? await PhoneVerification.RunAsync(e.Args) : e.Args.Contains("--verify-ui") ? await UiVerification.RunAsync(e.Args) : await Diagnostics.RunAsync(e.Args);
            Shutdown(code);
            return;
        }
        _singleInstance = new Mutex(true, "Local\\AudioLink.Desktop", out bool firstInstance);
        if (!firstInstance)
        {
            foreach (Process process in Process.GetProcessesByName("AudioLink"))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindow(process.MainWindowHandle, 9);
                    SetForegroundWindow(process.MainWindowHandle);
                    break;
                }
            }
            _singleInstance.Dispose(); _singleInstance = null;
            Shutdown(); return;
        }
        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            AppLog.Write("启动失败", ex);
            MessageBox.Show($"无法启动 AudioLink：{ex.Message}\n日志：{AppLog.DataDirectory}", "同频 AudioLink", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.ReleaseMutex(); _singleInstance?.Dispose();
        base.OnExit(e);
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}
