using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Dockable.Interop;
using Dockable.Localization;
using Dockable.Services;
using Dockable.ViewModels;

namespace Dockable;

public partial class App : Application
{
    public SettingsStore SettingsStore { get; } = new();
    public DockViewModel DockViewModel { get; private set; } = null!;

    private DockWindow? _dockWindow;
    private MenuBarWindow? _menuBarWindow;
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

            // Resolve the UI language (saved choice, else the Windows display language → English) and
            // persist the concrete code so the choice is sticky.
            DockViewModel.Settings.Language = Loc.Initialize(DockViewModel.Settings.Language);

            _dockWindow = new DockWindow { DataContext = DockViewModel };
            _dockWindow.Show();

            // Opt-in macOS-style menu bar at the top of the primary monitor.
            if (DockViewModel.Settings.ShowMenuBar)
                SetMenuBarVisible(true);
        }
        catch (Exception ex)
        {
            // Without this, a startup failure leaves a running process with no visible dock and
            // no feedback. Surface it, log it, restore the taskbar, and exit.
            LogCrash(ex, "Startup");
            Taskbar.Restore();
            MessageBox.Show(
                string.Format(Loc.T("Error_StartupFailed"), ex.Message),
                "Dockable", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>Shows or hides the top menu bar window, creating it on first show. Owned by the app so
    /// its lifetime is independent of the dock window.</summary>
    public void SetMenuBarVisible(bool show)
    {
        if (show)
        {
            if (_menuBarWindow is not null)
                return;
            _menuBarWindow = new MenuBarWindow { DataContext = new ViewModels.MenuBarViewModel(DockViewModel) };
            _menuBarWindow.Closed += (_, _) => _menuBarWindow = null;
            _menuBarWindow.Show();
        }
        else
        {
            _menuBarWindow?.Close(); // OnClosed releases the reserved top strip
            _menuBarWindow = null;
        }
    }

    /// <summary>Re-applies the menu bar's theme colours (if it's open) — invoked when the dock's
    /// Light/Dark/Auto theme changes so the bar stays coordinated with the dock.</summary>
    public void RefreshMenuBarTheme() => _menuBarWindow?.RefreshTheme();

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
