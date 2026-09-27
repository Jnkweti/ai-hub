using System.Configuration;
using System.Data;
using System.Windows;
using AIHub.Core;
using System.IO;
using System.Runtime.InteropServices;

namespace AIHub.Desktop;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private AppInstanceLease? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        WindowsAppIdentity.SetProcess();
        base.OnStartup(e);
        try
        {
            var directory = Environment.GetEnvironmentVariable("AIHUB_DATA_DIR") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIHub");
            instance = AppInstanceLease.TryAcquire(directory);
            if (instance is null)
            {
                using var owner = AppInstanceLease.ReadOwner(directory);
                if (owner is not null)
                {
                    var window = owner.MainWindowHandle;
                    if (window != IntPtr.Zero) { if (IsIconic(window)) ShowWindow(window, 9); SetForegroundWindow(window); }
                }
                Shutdown(); return;
            }
            MainWindow = new MainWindow(); MainWindow.Show();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("AI Hub could not open its saved data. No conversations were overwritten.\n\n" + ex.Message,
                "AI Hub could not start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}

