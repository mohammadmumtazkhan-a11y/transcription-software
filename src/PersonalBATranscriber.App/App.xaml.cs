using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PersonalBATranscriber.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogAndShowException(args.ExceptionObject as Exception, "AppDomain Unhandled Exception");
        };

        DispatcherUnhandledException += (s, args) =>
        {
            LogAndShowException(args.Exception, "Dispatcher Unhandled Exception");
            args.Handled = true;
        };
    }

    private static void LogAndShowException(Exception? ex, string source)
    {
        var message = ex != null ? $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}" : "Unknown exception.";
        
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PersonalBATranscriber", "error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:o}] [{source}] {message}\n\n");
        }
        catch { }

        MessageBox.Show($"Application Error ({source}):\n\n{ex?.Message}", "Personal BA Transcriber Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
