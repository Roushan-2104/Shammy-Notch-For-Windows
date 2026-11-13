using System.Windows;
using System.Windows.Media.Animation;
using Shammy.Infrastructure;

namespace Shammy;

public partial class App : Application
{
    Mutex? _instance;
    ShammyHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new Mutex(true, @"Local\Shammy.SingleInstance", out bool first);
        if (!first)
        {
            Shutdown();
            return;
        }

        // One stray exception shouldn't take the notch down, but a stream of them means the UI is in a
        // broken state; then it's better to exit cleanly than to keep running half-working.
        var recent = new Queue<DateTime>();
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
            var now = DateTime.UtcNow;
            recent.Enqueue(now);
            while (recent.Count > 0 && now - recent.Peek() > TimeSpan.FromMinutes(1)) recent.Dequeue();
            if (recent.Count >= 10)
            {
                Log.Error("Too many UI errors in a minute; exiting");
                Shutdown(2);
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Fatal exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        // Storyboard animations run at 60 fps. The notch's springs don't use storyboards (they step on WPF's own render
        // loop, at the display's rate), so they stay smooth; capping everything else spares the layered windows, which
        // are read back from the GPU on every repaint.
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = 60 });

        Log.Info($"Shammy {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion}");
        if (Paths.MigratedFrom is { } from) Log.Info($"Moved the data folder over from {Path.GetFileName(from)} (the app's old name)");
        AppSettings.MigrateStartupEntry();
        // After the updater has finished: it can still be restoring files for a few seconds after a restart.
        Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => RenameCleanup.Run(), TaskScheduler.Default);
        Theme.Accent.Apply();
        try
        {
            _host = new ShammyHost(AppSettings.Load());
            _host.Start();
        }
        catch (Exception ex)
        {
            // Without the notch there's nothing to show; don't linger as an invisible process.
            Log.Error("Startup failed", ex);
            MessageBox.Show($"Shammy couldn't start:\n\n{ex.Message}\n\nDetails are in {Paths.Logs}", "Shammy",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _instance?.Dispose();
        Log.Info("Shammy exited");
        base.OnExit(e);
    }
}
