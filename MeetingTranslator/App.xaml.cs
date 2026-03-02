using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MeetingTranslator;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UI-thread exceptions
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Non-UI-thread exceptions (CLR)
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // Unobserved Task exceptions (fire-and-forget Tasks)
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("DispatcherUnhandledException", e.Exception);
        e.Handled = true; // prevent app from closing
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogException("AppDomain.UnhandledException", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogException("UnobservedTaskException", e.Exception);
        e.SetObserved(); // prevent process termination
    }

    private static void LogException(string source, Exception? ex)
    {
        try
        {
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}{Environment.NewLine}";
            File.AppendAllText(LogPath, entry);
        }
        catch { /* best-effort */ }
    }
}

