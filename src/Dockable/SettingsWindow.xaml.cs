using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Dockable.Accessibility;
using Dockable.Interop;
using Dockable.Localization;
using Dockable.Models;
using Dockable.ViewModels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable;

/// <summary>
/// A macOS-style "Dock Preferences" window. Appearance (theme) and the Size/Magnification sliders are
/// wired live; the remaining controls are still visual-only (a later step).
/// </summary>
public partial class SettingsWindow : Window
{
    // Size slider maps directly to IconSize (DIP).
    private const double SizeMin = 12;
    private const double SizeMax = 64;

    // Magnification slider: position 0 = Off, positions 1..MagSteps map MaxIconSize Small..Large.
    private const double MagSmall = 24;
    private const double MagLarge = 104;
    private const int MagSteps = 9; // slider Maximum; step 1 = Small, step 9 = Large

    private static readonly Brush RingBrush = UiBrushes.Frozen(UiBrushes.AccentHex);

    private readonly DockViewModel _vm;
    private readonly Action<DockTheme> _setTheme;
    private readonly Action<DockEdge> _setEdge;
    private readonly Action<TaskbarVisibility> _setTaskbarVisibility;
    private readonly Action<GlassEffect> _setGlassEffect;
    private readonly Action<bool> _setShowMenuBar;
    private readonly Action _applyGlass;
    private readonly Action<PerformanceMode> _setPerformanceMode;
    private bool _initializing;

    public SettingsWindow(DockViewModel vm, Action<DockTheme> setTheme, Action<DockEdge> setEdge,
        Action<TaskbarVisibility> setTaskbarVisibility, Action<GlassEffect> setGlassEffect, Action<bool> setShowMenuBar,
        Action applyGlass, Action<PerformanceMode> setPerformanceMode)
    {
        _vm = vm;
        _setTheme = setTheme;
        _setEdge = setEdge;
        _setTaskbarVisibility = setTaskbarVisibility;
        _setGlassEffect = setGlassEffect;
        _setShowMenuBar = setShowMenuBar;
        _applyGlass = applyGlass;
        _setPerformanceMode = setPerformanceMode;
        _initializing = true; // set before InitializeComponent so initial events no-op
        InitializeComponent();
        Icon = AppIcon.Large;

        // About page: app icon + version (major.minor.build).
        AboutAppImage.Source = AppIcon.Large;
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        string verDisplay = ver is null ? "1.0.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
        AboutVersionText.Text = string.Format(Loc.T("About_Version"), verDisplay);

        var s = vm.Settings;

        // Language: list native names; select the active language by its culture code.
        LanguageCombo.ItemsSource = Loc.Languages.Select(l => l.Name).ToList();
        LanguageCombo.SelectedIndex = Math.Max(0, IndexOfLanguage(Loc.Instance.CurrentCode));

        // Appearance (theme)
        switch (s.Theme)
        {
            case DockTheme.Light: LightRadio.IsChecked = true; break;
            case DockTheme.Dark: DarkRadio.IsChecked = true; break;
            default: AutoRadio.IsChecked = true; break; // System == "Auto"
        }
        UpdateAppearanceRings();

        SizeSlider.Value = Math.Clamp(s.IconSize, SizeMin, SizeMax);
        MagnificationSlider.Value = MagnificationToStep(s);

        PositionCombo.SelectedIndex = (int)s.Edge;   // DockEdge: Bottom, Left, Right, Top (matches combo order)
        GlassEffectCombo.SelectedIndex = (int)s.GlassEffect; // GlassEffect: Simple, Acrylic, LiquidGlass
        MinimizeCombo.SelectedIndex = (int)s.MinimizeEffect; // MinimizeEffect: Suck, Scale, Genie
        PerformanceCombo.SelectedIndex = (int)s.PerformanceMode; // PerformanceMode: Auto, Quality, Performance
        EffectSpeedSlider.Value = SpeedToStep(s.EffectSpeed);
        OpenAtLoginSwitch.IsChecked = StartupManager.IsEnabled(StartupEntryName);
        IndicatorsSwitch.IsChecked = s.ShowRunningIndicators;
        AnimateOpeningSwitch.IsChecked = s.AnimateOpeningApps;
        MinimizeIntoIconSwitch.IsChecked = s.MinimizeIntoIcon;
        AutoHideDockSwitch.IsChecked = s.AutoHideDock;
        HideOnFullscreenSwitch.IsChecked = s.HideOnFullscreen;
        ShowSettingsInDockSwitch.IsChecked = s.ShowSettingsInDock;
        ShowMenuBarSwitch.IsChecked = s.ShowMenuBar;
        TaskbarCombo.SelectedIndex = (int)s.TaskbarVisibility; // Always, Auto, Never (matches combo order)

        // Liquid Glass tuning sliders (clamped to each slider's range).
        SyncGlassSlidersFromSettings();

        _initializing = false;

        ShowSection("General");        // default page + sidebar selection
        UpdateGlassParamsEnabled();    // grey the tuning sliders unless Liquid Glass is selected

        // The default size (740x685) may exceed a small display — shrink to fit the work area.
        var work = SystemParameters.WorkArea;
        if (Width > work.Width) Width = work.Width;
        if (Height > work.Height) Height = work.Height;
    }

    // --- Sidebar navigation ---

    private void NavItem_Click(object sender, MouseButtonEventArgs e)
        => ShowSection((string)((FrameworkElement)sender).Tag);

    /// <summary>Navigates the window to a section by id (e.g. from the dock's "About Dockable" menu).</summary>
    public void NavigateTo(string section) => ShowSection(section);

    // --- Settings search (the sidebar search bar) -------------------------------------------

    /// <summary>One searchable thing: a whole panel (Row = null) or an individual setting. The
    /// display/panel names resolve through Loc at query time (search follows the UI language);
    /// Tags are lowercase English synonyms and alternative names.</summary>
    private sealed record SettingEntry(string Panel, string NameKey, Func<FrameworkElement?>? Row, string Tags);

    private List<SettingEntry>? _settingsIndex;

    private List<SettingEntry> SettingsIndex => _settingsIndex ??= new List<SettingEntry>
    {
        // Panels themselves — clicking navigates, with no row emphasis.
        new("General", "Nav_General", null, "general system settings"),
        new("DockMenuBar", "Nav_DockMenuBar", null, "dock menu bar"),
        new("LiquidGlass", "Nav_LiquidGlass", null, "liquid glass shader backdrop"),
        new("About", "Nav_About", null, "about version credits author highlights claude"),

        // General
        new("General", "Row_Language", () => RowOf(LanguageCombo), "language locale translation idiom culture english portugues espanol ukrainian chinese"),
        new("General", "Row_Performance", () => RowOf(PerformanceCombo), "performance quality gpu fps framerate smooth effects battery"),
        new("General", "Row_OpenAtLogin", () => RowOf(OpenAtLoginSwitch), "open at login startup boot autostart run when windows starts"),
        new("General", "Section_Appearance", AppearanceTiles, "appearance theme light dark auto mode color night"),

        // Dock & Menu Bar
        new("DockMenuBar", "Label_Size", () => RowOf(SizeSlider), "size icon size small large scale resize"),
        new("DockMenuBar", "Label_Magnification", () => RowOf(MagnificationSlider), "magnification zoom fisheye hover grow magnify enlarge"),
        new("DockMenuBar", "Row_Position", () => RowOf(PositionCombo), "position on screen edge bottom left right side location"),
        new("DockMenuBar", "Row_MinimizeUsing", () => RowOf(MinimizeCombo), "minimize using genie suck scale warp animation effect"),
        new("DockMenuBar", "Label_EffectSpeed", () => RowOf(EffectSpeedSlider), "effect speed slow fast animation duration"),
        new("DockMenuBar", "Toggle_ShowIndicators", () => RowOf(IndicatorsSwitch), "indicators dots running open applications lights"),
        new("DockMenuBar", "Toggle_AnimateOpening", () => RowOf(AnimateOpeningSwitch), "animate opening applications bounce launch attention hop"),
        new("DockMenuBar", "Toggle_MinimizeIntoIcon", () => RowOf(MinimizeIntoIconSwitch), "minimize into application icon tile thumbnail"),
        new("DockMenuBar", "Toggle_AutoHideDock", () => RowOf(AutoHideDockSwitch), "automatically hide show dock autohide auto-hide hiding reveal slide"),
        new("DockMenuBar", "Toggle_HideOnFullscreen", () => RowOf(HideOnFullscreenSwitch), "hide fullscreen full screen full-screen apps games video borderless immersive"),
        new("DockMenuBar", "Toggle_ShowSettingsInDock", () => RowOf(ShowSettingsInDockSwitch), "show dockable settings preferences dock tile pin gear keep in dock"),
        new("DockMenuBar", "Toggle_ShowMenuBar", () => RowOf(ShowMenuBarSwitch), "menu bar top bar clock keyboard battery notifications quick settings"),
        new("DockMenuBar", "Row_ShowTaskbar", () => RowOf(TaskbarCombo), "windows taskbar show hide always auto never"),

        // Liquid Glass (the tuning sliders live behind the one-way "Advanced" reveal)
        new("LiquidGlass", "Row_GlassEffect", () => RowOf(GlassEffectCombo), "glass effect translucent acrylic liquid blur transparency background"),
        new("LiquidGlass", "LiquidGlass_BlurRadius", () => AdvancedRowOf(GlassBlurSlider), "blur frost frosted radius advanced"),
        new("LiquidGlass", "LiquidGlass_Distortion", () => AdvancedRowOf(GlassDistortionSlider), "distortion refraction bend rim advanced"),
        new("LiquidGlass", "LiquidGlass_Opacity", () => AdvancedRowOf(GlassTintSlider), "tint opacity transparency alpha advanced"),
        new("LiquidGlass", "LiquidGlass_Saturation", () => AdvancedRowOf(GlassSaturationSlider), "saturation vibrance color intensity advanced"),
        new("LiquidGlass", "LiquidGlass_Aberration", () => AdvancedRowOf(GlassAberrationSlider), "chromatic aberration fringe rainbow advanced"),
        new("LiquidGlass", "LiquidGlass_RimHighlight", () => AdvancedRowOf(GlassRimSlider), "rim highlight glint sheen specular shine advanced"),
    };

    private static readonly Brush ResultNameBrush = UiBrushes.Frozen(UiBrushes.InkHex);
    private static readonly Brush ResultPanelBrush = UiBrushes.Frozen("#6E6E73");
    private static readonly Brush ResultHoverBrush = UiBrushes.Frozen("#14000000");

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        string query = SearchBox.Text.Trim();
        SearchResults.Children.Clear();
        if (query.Length == 0)
        {
            SearchResults.Visibility = Visibility.Collapsed;
            NavList.Visibility = Visibility.Visible;
            return;
        }

        // Group results under their panel (ordered by each panel's best-ranked match). The panel
        // header always shows — even when the panel name itself didn't match — with the nav icon;
        // clicking it just opens the panel. Panel-only entries (Row == null) merely create a group.
        var panelOrder = new List<string>();
        var byPanel = new Dictionary<string, List<SettingEntry>>();
        foreach (var entry in FindSettings(query))
        {
            if (!byPanel.TryGetValue(entry.Panel, out var rows))
            {
                byPanel[entry.Panel] = rows = new List<SettingEntry>();
                panelOrder.Add(entry.Panel);
            }
            if (entry.Row is not null)
                rows.Add(entry);
        }
        foreach (string panel in panelOrder)
        {
            SearchResults.Children.Add(BuildSearchGroupHeader(panel));
            foreach (var entry in byPanel[panel])
                SearchResults.Children.Add(BuildSearchResultRow(entry));
        }
        SearchResults.Visibility = Visibility.Visible;
        NavList.Visibility = Visibility.Collapsed;
    }

    /// <summary>Matches on the localized name first, then the panel name and English tags.</summary>
    private List<SettingEntry> FindSettings(string query)
    {
        string q = query.ToLowerInvariant();
        var byName = new List<SettingEntry>();
        var byTag = new List<SettingEntry>();
        foreach (var entry in SettingsIndex)
        {
            if (Loc.T(entry.NameKey).ToLowerInvariant().Contains(q))
                byName.Add(entry);
            else if (entry.Tags.Contains(q) || Loc.T(PanelNameKey(entry.Panel)).ToLowerInvariant().Contains(q))
                byTag.Add(entry);
        }
        byName.AddRange(byTag);
        return byName.Take(14).ToList();
    }

    private static string PanelNameKey(string panel) => panel switch
    {
        "DockMenuBar" => "Nav_DockMenuBar",
        "LiquidGlass" => "Nav_LiquidGlass",
        "About" => "Nav_About",
        _ => "Nav_General",
    };

    /// <summary>The clickable panel group header atop each result group: the panel's nav icon +
    /// name. Clicking opens the panel with no row emphasis.</summary>
    private FrameworkElement BuildSearchGroupHeader(string panel)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(PanelBadge(panel));
        content.Children.Add(new TextBlock
        {
            Text = Loc.T(PanelNameKey(panel)),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResultNameBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // InvokableCell = a Border with a UIA peer, so screen readers can read + invoke the header.
        var row = new InvokableCell
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, SearchResults.Children.Count == 0 ? 0 : 6, 0, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = content,
        };
        AutomationProperties.SetName(row, Loc.T(PanelNameKey(panel)));
        row.MouseEnter += (_, _) => row.Background = ResultHoverBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => ShowSection(panel);
        return row;
    }

    /// <summary>A small replica of the panel's sidebar nav icon (same glyph + background).</summary>
    private static Border PanelBadge(string panel)
    {
        Brush background = panel switch
        {
            "DockMenuBar" => UiBrushes.Frozen(UiBrushes.AccentHex),
            "About" => UiBrushes.Frozen(UiBrushes.InkHex),
            // Liquid-glass sheen — mirrors the nav tile's radial gradient.
            "LiquidGlass" => new RadialGradientBrush
            {
                GradientOrigin = new Point(0.3, 0.25),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.95,
                RadiusY = 0.95,
                GradientStops =
                {
                    new GradientStop((Color)ColorConverter.ConvertFromString("#FF6FB1"), 0),
                    new GradientStop((Color)ColorConverter.ConvertFromString("#FF3B30"), 0.35),
                    new GradientStop((Color)ColorConverter.ConvertFromString("#0A84FF"), 0.72),
                    new GradientStop((Color)ColorConverter.ConvertFromString("#FFD60A"), 1),
                },
            },
            _ => UiBrushes.Frozen("#8E8E93"), // General
        };
        string glyph = panel switch
        {
            "DockMenuBar" => "",
            "LiquidGlass" => "",
            "About" => "",
            _ => "",
        };
        return new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(5),
            Background = background,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 10,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private FrameworkElement BuildSearchResultRow(SettingEntry entry)
    {
        // InvokableCell = a Border with a UIA peer, so screen readers can read + invoke the result.
        var row = new InvokableCell
        {
            CornerRadius = new CornerRadius(6),
            // Left padding aligns the name under the group header's label (badge 20 + 6 gap).
            Padding = new Thickness(34, 5, 8, 5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = Loc.T(entry.NameKey),
                FontSize = 13,
                Foreground = ResultNameBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        AutomationProperties.SetName(row, Loc.T(entry.NameKey));
        row.MouseEnter += (_, _) => row.Background = ResultHoverBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => OpenSearchResult(entry);
        return row;
    }

    private void OpenSearchResult(SettingEntry entry)
    {
        ShowSection(entry.Panel);
        if (entry.Row is null)
            return; // a whole panel — navigate only, nothing to emphasize

        // Let the page lay out before scrolling to + pulsing the row.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var row = entry.Row();
            if (row is null)
                return;
            row.BringIntoView();
            PulseSettingRow(row);
        });
    }

    /// <summary>The settings-row container (the row Grid inside a card) that hosts a named control.</summary>
    private static FrameworkElement? RowOf(FrameworkElement control)
    {
        DependencyObject? node = control;
        while (node is FrameworkElement fe)
        {
            if (fe is Grid && fe.Parent is StackPanel or Border)
                return fe;
            node = fe.Parent;
        }
        return null;
    }

    /// <summary>The Appearance tiles' white card (the rounded Border holding the Light/Dark/Auto
    /// strip) — pulsing the card keeps the emphasis on a rounded surface.</summary>
    private FrameworkElement? AppearanceTiles()
        => ((LightSel.Parent as FrameworkElement)?.Parent as FrameworkElement)?.Parent as FrameworkElement;

    /// <summary>Row resolver for the Liquid Glass tuning sliders: they sit behind the one-way
    /// "Advanced" reveal, so expand it first, then locate the row like any other setting.</summary>
    private FrameworkElement? AdvancedRowOf(FrameworkElement control)
    {
        GlassParamsPanel.Visibility = Visibility.Visible;
        GlassAdvancedLink.Visibility = Visibility.Collapsed;
        GlassParamsPanel.UpdateLayout(); // lay the freshly-revealed rows out so BringIntoView lands
        return RowOf(control);
    }

    /// <summary>Pulses a row with the accent tint, then fades back to whatever was there. Grid rows
    /// get a rounded overlay behind their content (extends a little past it, inset a hair from the
    /// neighbouring rows) so the emphasis isn't a square edge-to-edge fill.</summary>
    private static void PulseSettingRow(FrameworkElement row)
    {
        var pulse = new SolidColorBrush(Color.FromArgb(0x46, 0x0A, 0x84, 0xFF));
        var fade = new ColorAnimation
        {
            Duration = TimeSpan.FromMilliseconds(1500),
            BeginTime = TimeSpan.FromMilliseconds(600),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };

        if (row is Grid grid)
        {
            var overlay = new Border
            {
                CornerRadius = new CornerRadius(8),
                // Negative sides = padding around the row content; small top/bottom = margin
                // from the adjacent rows/dividers. Grids don't clip, so the sides render fine.
                Margin = new Thickness(-10, 3, -10, 3),
                Background = pulse,
                IsHitTestVisible = false,
            };
            if (grid.ColumnDefinitions.Count > 1) Grid.SetColumnSpan(overlay, grid.ColumnDefinitions.Count);
            if (grid.RowDefinitions.Count > 1) Grid.SetRowSpan(overlay, grid.RowDefinitions.Count);
            grid.Children.Insert(0, overlay); // index 0 = behind the row's content
            fade.To = Colors.Transparent;
            fade.Completed += (_, _) => grid.Children.Remove(overlay);
            pulse.BeginAnimation(SolidColorBrush.ColorProperty, fade);
            return;
        }

        // Non-Grid targets (e.g. the Appearance card Border) carry their own rounded surface —
        // pulse the background in place and fade back to it.
        Brush? original;
        Action<Brush?> setBackground;
        switch (row)
        {
            case Panel panel: original = panel.Background; setBackground = b => panel.Background = b; break;
            case Border border: original = border.Background; setBackground = b => border.Background = b; break;
            default: return;
        }
        setBackground(pulse);
        fade.To = (original as SolidColorBrush)?.Color ?? Colors.Transparent;
        fade.Completed += (_, _) => setBackground(original);
        pulse.BeginAnimation(SolidColorBrush.ColorProperty, fade);
    }

    /// <summary>Shows the page for <paramref name="id"/> (General / DockMenuBar / LiquidGlass / About)
    /// and highlights its sidebar row with a light-gray fill.</summary>
    private void ShowSection(string id)
    {
        PageGeneral.Visibility = id == "General" ? Visibility.Visible : Visibility.Collapsed;
        PageDockMenuBar.Visibility = id == "DockMenuBar" ? Visibility.Visible : Visibility.Collapsed;
        PageLiquidGlass.Visibility = id == "LiquidGlass" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = id == "About" ? Visibility.Visible : Visibility.Collapsed;

        SetNavSelected(NavGeneral, NavGeneralLabel, id == "General");
        SetNavSelected(NavDockMenuBar, NavDockMenuBarLabel, id == "DockMenuBar");
        SetNavSelected(NavLiquidGlass, NavLiquidGlassLabel, id == "LiquidGlass");
        SetNavSelected(NavAbout, NavAboutLabel, id == "About");
    }

    private static void SetNavSelected(System.Windows.Controls.Border row, TextBlock label, bool selected)
    {
        row.Background = selected ? NavSelectedBrush : Brushes.Transparent;
        label.Foreground = NavTextBrush;
    }

    private static readonly Brush NavTextBrush = UiBrushes.Frozen(UiBrushes.InkHex);
    private static readonly Brush NavSelectedBrush = UiBrushes.Frozen("#1C000000"); // light gray over the sidebar

    // ResizeMode=CanResize adds a maximize box; remove it so the window can't jump to a corner — its
    // size is already capped to its content and the viewport.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = (HWND)new WindowInteropHelper(this).Handle;
        var style = (WINDOW_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, (nint)(uint)(style & ~WINDOW_STYLE.WS_MAXIMIZEBOX));
    }

    // --- Language ---

    private static int IndexOfLanguage(string code)
    {
        var langs = Loc.Languages;
        for (int i = 0; i < langs.Count; i++)
            if (langs[i].Code == code)
                return i;
        return -1;
    }

    // Applies the chosen language live (Loc raises PropertyChanged for bindings + LanguageChanged for
    // the dock's code-built menus) and persists the culture code.
    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        int i = LanguageCombo.SelectedIndex;
        if (i < 0 || i >= Loc.Languages.Count)
            return;
        string code = Loc.Languages[i].Code;
        _vm.Settings.Language = code;
        _vm.Save();
        Loc.Instance.SetLanguage(code);
    }

    // --- Appearance (theme) ---

    // Clicking anywhere on an illustration selects that theme's radio.
    private void AppearanceTile_Click(object sender, MouseButtonEventArgs e)
    {
        switch ((string)((FrameworkElement)sender).Tag)
        {
            case "Light": LightRadio.IsChecked = true; break;
            case "Dark": DarkRadio.IsChecked = true; break;
            default: AutoRadio.IsChecked = true; break;
        }
    }

    private void AppearanceRadio_Checked(object sender, RoutedEventArgs e)
    {
        UpdateAppearanceRings();
        if (_initializing)
            return;
        var theme = (string)((RadioButton)sender).Tag switch
        {
            "Light" => DockTheme.Light,
            "Dark" => DockTheme.Dark,
            _ => DockTheme.System, // "Auto"
        };
        _setTheme(theme); // applies + saves on the dock
    }

    private void UpdateAppearanceRings()
    {
        LightSel.BorderBrush = LightRadio.IsChecked == true ? RingBrush : Brushes.Transparent;
        DarkSel.BorderBrush = DarkRadio.IsChecked == true ? RingBrush : Brushes.Transparent;
        AutoSel.BorderBrush = AutoRadio.IsChecked == true ? RingBrush : Brushes.Transparent;
    }

    // Registry Run-key entry name for launching Dockable itself at login.
    private const string StartupEntryName = "Dockable";

    // Adds/removes Dockable from the Windows startup sequence (HKCU Run key).
    private void OpenAtLoginSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (OpenAtLoginSwitch.IsChecked == true)
        {
            string exe = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(exe))
                StartupManager.Enable(StartupEntryName, exe);
        }
        else
        {
            StartupManager.Disable(StartupEntryName);
        }
    }

    // Opens the Windows "Startup Apps" settings page in a new window.
    private void StartupApps_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:startupapps")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort; the settings deep-link may be unavailable on some SKUs.
        }
    }

    // Click fires only on user interaction (not the constructor's IsChecked set), so no guard needed.
    private void IndicatorsSwitch_Click(object sender, RoutedEventArgs e)
        => _vm.SetShowRunningIndicators(IndicatorsSwitch.IsChecked == true);

    private void AutoHideDockSwitch_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.AutoHideDock = AutoHideDockSwitch.IsChecked == true;
        _vm.Save();
        // The dock owns the behavior (slide out/in + releasing the AppBar reservation).
        Application.Current.Windows.OfType<DockWindow>().FirstOrDefault()?.ApplyAutoHide();
    }

    private void HideOnFullscreenSwitch_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.HideOnFullscreen = HideOnFullscreenSwitch.IsChecked == true;
        _vm.Save();
        // Turning it off restores a dock/menu bar currently hidden for a full-screen app; turning it
        // on takes effect on the next fullscreen check (nothing is fullscreen while we're focused).
        Application.Current.Windows.OfType<DockWindow>().FirstOrDefault()?.ApplyHideOnFullscreen();
        Application.Current.Windows.OfType<MenuBarWindow>().FirstOrDefault()?.ApplyHideOnFullscreen();
    }

    // Always / Auto (native auto-hide) / Never. The dock applies and persists it (and restores the
    // taskbar to its pre-launch state on close).
    private void TaskbarCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        _setTaskbarVisibility((TaskbarVisibility)TaskbarCombo.SelectedIndex);
    }

    private void AnimateOpeningSwitch_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.AnimateOpeningApps = AnimateOpeningSwitch.IsChecked == true;
        _vm.Save();
    }

    private void MinimizeIntoIconSwitch_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.MinimizeIntoIcon = MinimizeIntoIconSwitch.IsChecked == true;
        _vm.Save();
    }

    // The dock owns the menu bar window's lifetime, so route through its callback (it persists the
    // setting and creates/closes the window).
    private void ShowMenuBarSwitch_Click(object sender, RoutedEventArgs e)
        => _setShowMenuBar(ShowMenuBarSwitch.IsChecked == true);

    // Show/hide the built-in Dock Preferences tile (pin + mirrored setting via the VM); the dock
    // refresh applies the tile change immediately instead of waiting on the 1 s tick.
    private void ShowSettingsInDockSwitch_Click(object sender, RoutedEventArgs e)
    {
        _vm.SetShowSettingsInDock(ShowSettingsInDockSwitch.IsChecked == true);
        Application.Current.Windows.OfType<DockWindow>().FirstOrDefault()?.RefreshTaskbarApps();
    }

    // --- Position on screen ---

    private void PositionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        _setEdge((DockEdge)PositionCombo.SelectedIndex); // 0 Bottom, 1 Left, 2 Right, 3 Top
    }

    // --- Glass effect ---

    private void GlassEffectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateGlassParamsEnabled();
        if (_initializing)
            return;
        _setGlassEffect((GlassEffect)GlassEffectCombo.SelectedIndex); // 0 Simple, 1 Acrylic, 2 LiquidGlass
    }

    // The Liquid Glass tuning UI is gated twice: the sliders sit behind a one-way "Advanced" reveal,
    // and the whole affordance (divider + link + sliders) only exists while Liquid Glass is selected.
    private void UpdateGlassParamsEnabled()
    {
        bool liquid = GlassEffectCombo.SelectedIndex == (int)GlassEffect.LiquidGlass;
        GlassParamsPanel.IsEnabled = liquid;
        if (!liquid)
            GlassParamsPanel.Visibility = Visibility.Collapsed; // leaving Liquid Glass re-collapses Advanced
        GlassAdvancedDivider.Visibility = liquid ? Visibility.Visible : Visibility.Collapsed;
        // The reveal link shows only while the sliders are still collapsed; once expanded there is no
        // collapse affordance (switch effects or reopen Preferences to reset).
        GlassAdvancedLink.Visibility = liquid && GlassParamsPanel.Visibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // One-way reveal: expands the Advanced tuning sliders and retires the link.
    private void GlassAdvanced_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        GlassParamsPanel.Visibility = Visibility.Visible;
        GlassAdvancedLink.Visibility = Visibility.Collapsed;
    }

    // --- Liquid Glass tuning ---

    /// <summary>Shared handler for the six tuning sliders (they differ only in the settings field
    /// they write): stores the value, persists, and pushes it into the live shader.</summary>
    private void GlassSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        var s = _vm.Settings;
        if (ReferenceEquals(sender, GlassBlurSlider)) s.GlassBlurRadius = e.NewValue;
        else if (ReferenceEquals(sender, GlassDistortionSlider)) s.GlassDistortion = e.NewValue;
        else if (ReferenceEquals(sender, GlassTintSlider)) s.GlassTintOpacity = e.NewValue;
        else if (ReferenceEquals(sender, GlassSaturationSlider)) s.GlassSaturation = e.NewValue;
        else if (ReferenceEquals(sender, GlassAberrationSlider)) s.GlassAberration = e.NewValue;
        else if (ReferenceEquals(sender, GlassRimSlider)) s.GlassRimHighlight = e.NewValue;
        else return;
        ApplyGlassAndSave();
    }

    /// <summary>Reflects the six Liquid Glass settings onto their sliders (clamped to each range).</summary>
    private void SyncGlassSlidersFromSettings()
    {
        var s = _vm.Settings;
        GlassBlurSlider.Value = Math.Clamp(s.GlassBlurRadius, GlassBlurSlider.Minimum, GlassBlurSlider.Maximum);
        GlassDistortionSlider.Value = Math.Clamp(s.GlassDistortion, GlassDistortionSlider.Minimum, GlassDistortionSlider.Maximum);
        GlassTintSlider.Value = Math.Clamp(s.GlassTintOpacity, GlassTintSlider.Minimum, GlassTintSlider.Maximum);
        GlassSaturationSlider.Value = Math.Clamp(s.GlassSaturation, GlassSaturationSlider.Minimum, GlassSaturationSlider.Maximum);
        GlassAberrationSlider.Value = Math.Clamp(s.GlassAberration, GlassAberrationSlider.Minimum, GlassAberrationSlider.Maximum);
        GlassRimSlider.Value = Math.Clamp(s.GlassRimHighlight, GlassRimSlider.Minimum, GlassRimSlider.Maximum);
    }

    // Restores the six Liquid Glass parameters to their defaults (from a fresh DockSettings).
    private void GlassReset_Click(object sender, MouseButtonEventArgs e)
    {
        var d = DockSettings.CreateDefault();
        var s = _vm.Settings;
        s.GlassBlurRadius = d.GlassBlurRadius;
        s.GlassDistortion = d.GlassDistortion;
        s.GlassTintOpacity = d.GlassTintOpacity;
        s.GlassSaturation = d.GlassSaturation;
        s.GlassAberration = d.GlassAberration;
        s.GlassRimHighlight = d.GlassRimHighlight;

        _initializing = true; // reflect onto the sliders without re-triggering the handler
        SyncGlassSlidersFromSettings();
        _initializing = false;

        ApplyGlassAndSave();
    }

    private void ApplyGlassAndSave()
    {
        _applyGlass();
        _vm.Save();
    }

    // --- Minimize effect ---

    private void MinimizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        _vm.Settings.MinimizeEffect = (MinimizeEffect)MinimizeCombo.SelectedIndex; // 0 Suck, 1 Scale, 2 Genie
        _vm.Save();
    }

    // --- Performance mode ---

    private void PerformanceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        _setPerformanceMode((PerformanceMode)PerformanceCombo.SelectedIndex); // 0 Auto, 1 Quality, 2 Performance
    }

    // --- Effect speed ---
    // The slider snaps to 5 positions: Slow, ·, Regular, ·, Fast. Speed is the animation's
    // SpeedMultiplier; the scale now tops out at 1x (the base speed) — Slow is very slow (0.01).
    private static readonly double[] SpeedSteps = { 0.01, 0.25, 0.5, 0.75, 1.0 };

    private static double StepToSpeed(int step) => SpeedSteps[Math.Clamp(step, 0, SpeedSteps.Length - 1)];

    private static int SpeedToStep(double speed)
    {
        int best = 0;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < SpeedSteps.Length; i++)
        {
            double diff = Math.Abs(SpeedSteps[i] - speed);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        return best;
    }

    private void EffectSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;
        _vm.Settings.EffectSpeed = StepToSpeed((int)Math.Round(e.NewValue));
        _vm.Save();
    }

    // --- Size / Magnification ---

    /// <summary>The slider step (0 = Off) that represents the current magnified size.</summary>
    private static double MagnificationToStep(DockSettings s)
    {
        // A magnified size at or below the base size means magnification is effectively off.
        if (!s.MagnificationEnabled || s.MaxIconSize <= s.IconSize)
            return 0;
        double t = (s.MaxIconSize - MagSmall) / (MagLarge - MagSmall); // 0..1 across Small..Large
        int step = (int)Math.Round(t * (MagSteps - 1)) + 1;
        return Math.Clamp(step, 1, MagSteps);
    }

    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;
        _vm.Settings.IconSize = Math.Round(e.NewValue);
        ApplyAndSave();
    }

    /// <summary>Reflects an externally-changed Size (e.g. a separator drag) onto the slider without
    /// re-applying (the <c>_initializing</c> guard suppresses the resulting ValueChanged).</summary>
    public void SyncSizeFromSettings()
    {
        _initializing = true;
        SizeSlider.Value = Math.Clamp(_vm.Settings.IconSize, SizeMin, SizeMax);
        _initializing = false;
    }

    /// <summary>Re-reads the rows the dock's empty-space context menu can change while this window
    /// is open (hiding, magnification, position, minimize effect), so the two stay in step.</summary>
    public void SyncFromSettings()
    {
        _initializing = true;
        var s = _vm.Settings;
        MagnificationSlider.Value = MagnificationToStep(s);
        PositionCombo.SelectedIndex = (int)s.Edge;
        MinimizeCombo.SelectedIndex = (int)s.MinimizeEffect;
        AutoHideDockSwitch.IsChecked = s.AutoHideDock;
        ShowSettingsInDockSwitch.IsChecked = s.ShowSettingsInDock; // the tile's "Keep in Dock" menu
        _initializing = false;
    }

    private void MagnificationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;

        int step = (int)Math.Round(e.NewValue);
        if (step <= 0)
        {
            // "Off" position. (The layout engine also treats MaxIconSize <= IconSize as off.)
            _vm.Settings.MagnificationEnabled = false;
        }
        else
        {
            _vm.Settings.MagnificationEnabled = true;
            _vm.Settings.MaxIconSize = MagSmall + (step - 1) * (MagLarge - MagSmall) / (MagSteps - 1);
        }
        ApplyAndSave();
    }

    private void ApplyAndSave()
    {
        _vm.RecomputeLayout(); // relayout the dock live (resizes + repositions the window)
        _vm.Save();
    }

    // --- About page links ---

    // A "Built with" chip (Border with its site URL in Tag).
    private void OpenLink_Click(object sender, MouseButtonEventArgs e)
        => OpenUrl((string)((FrameworkElement)sender).Tag);

    // The inspiration / author hyperlinks.
    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort; a missing/blocked browser shouldn't crash the window.
        }
    }

}
