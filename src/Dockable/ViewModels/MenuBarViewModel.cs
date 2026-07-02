using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dockable.Interop;
using Dockable.Models;

namespace Dockable.ViewModels;

/// <summary>
/// State for the macOS-style menu bar. Shares the dock's <see cref="DockSettings"/> (for theme + glass)
/// and adds the bar's own live, code-driven strings (focused-window title, keyboard layout, clock).
/// The window's code-behind keeps these in sync — mirroring how <see cref="DockViewModel"/> geometry
/// is driven from <c>DockWindow</c>.
/// </summary>
public sealed partial class MenuBarViewModel : ObservableObject
{
    private readonly DockViewModel _dock;

    public MenuBarViewModel(DockViewModel dock) => _dock = dock;

    /// <summary>Shared dock settings (theme, glass effect) so the bar matches the dock.</summary>
    public DockSettings Settings => _dock.Settings;

    /// <summary>The Dock Preferences window's handle while open (zero otherwise) — the one
    /// own-process window the menu bar represents like a regular app.</summary>
    public IntPtr PreferencesHwnd => _dock.PreferencesHwnd;

    /// <summary>The focused app's friendly display name, e.g. "Google Chrome" (leading side of the bar).</summary>
    [ObservableProperty] private string _appName = string.Empty;

    /// <summary>Top-level items of the focused window's in-window menu ("File", "Edit", …), rendered
    /// after the app name. Empty when the focused app has no readable menu bar.</summary>
    public ObservableCollection<AppMenuEntry> MenuEntries { get; } = new();

    /// <summary>The current keyboard layout's short code (e.g. "EN").</summary>
    [ObservableProperty] private string _keyboardLabel = string.Empty;

    /// <summary>The formatted current date/time (trailing side of the bar).</summary>
    [ObservableProperty] private string _timeText = string.Empty;
}
