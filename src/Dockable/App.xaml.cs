using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Dockable.Interop;
using Dockable.Services;
using Dockable.ViewModels;

namespace Dockable;

public partial class App : Application
{
    public SettingsStore SettingsStore { get; } = new();
    public DockViewModel DockViewModel { get; private set; } = null!;

    private DockWindow? _dockWindow;
    private Mutex? _singleInstanceMutex;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance: only the first copy runs a dock; later copies bow out immediately.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\Dockable.SingleInstance", out bool isNew);
        if (!isNew)
        {
            _singleInstanceMutex.Dispose(); // we don't own it; just let the running dock be
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            LogCrash(args.ExceptionObject as Exception, "AppDomain");
            Taskbar.Restore(); // put the taskbar back the way we found it before we go down
        };

        // Remember the taskbar's state now, before we change it, so we can restore it exactly.
        Taskbar.CaptureOriginalState();

        try
        {
            DockViewModel = new DockViewModel(SettingsStore);
            DockViewModel.Load();

            _dockWindow = new DockWindow { DataContext = DockViewModel };
            _dockWindow.Show();
        }
        catch (Exception ex)
        {
            // Without this, a startup failure leaves a running process with no visible dock and
            // no feedback. Surface it, log it, restore the taskbar, and exit.
            LogCrash(ex, "Startup");
            Taskbar.Restore();
            MessageBox.Show(
                $"Dockable failed to start:\n\n{ex.Message}\n\nDetails were written to %APPDATA%\\Dockable\\crash.log.",
                "Dockable", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Only the instance that actually started the dock should persist settings / restore the
        // taskbar — a bowing-out duplicate must not clobber either.
        if (_dockWindow is not null)
        {
            DockViewModel?.Save();
            Taskbar.Restore(); // put the taskbar back to its pre-launch state
        }

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception, "Dispatcher");
        // Keep the dock alive on non-fatal UI exceptions rather than dying silently.
        e.Handled = true;
    }

    private static void LogCrash(Exception? ex, string source)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockable");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:O}] ({source}) {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}
