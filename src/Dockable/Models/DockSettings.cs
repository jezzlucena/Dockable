namespace Dockable.Models;

/// <summary>Which screen edge the dock is anchored to.</summary>
public enum DockEdge
{
    Bottom,
    Left,
    Right,
    Top,
}

/// <summary>How the dock bar's background is rendered.</summary>
public enum GlassEffect
{
    /// <summary>Plain translucent bar (the pre-acrylic look); no backdrop window. Lightest.</summary>
    Simple,

    /// <summary>Live system acrylic blur of the desktop behind the bar.</summary>
    Acrylic,

    /// <summary>Custom blur + saturation (+ edge refraction); falls back to Acrylic if unsupported.</summary>
    LiquidGlass,
}

/// <summary>Which color theme the dock paints with.</summary>
public enum DockTheme
{
    /// <summary>Follow the current Windows light/dark setting.</summary>
    System,
    Light,
    Dark,
}

/// <summary>How the Windows taskbar is shown while Dockable runs.</summary>
public enum TaskbarVisibility
{
    /// <summary>Always visible (auto-hide off).</summary>
    Always,

    /// <summary>Native auto-hide: slides away, reveals on edge hover (default).</summary>
    Auto,

    /// <summary>Hidden entirely — won't even reveal on hover.</summary>
    Never,
}

/// <summary>How aggressively the dock trades visual fidelity for animation smoothness.</summary>
public enum PerformanceMode
{
    /// <summary>Pick automatically from the GPU's WPF render tier: full effects on tier-2 (full
    /// hardware) GPUs, reduced effects on partial/no-acceleration GPUs.</summary>
    Auto,

    /// <summary>Force full-fidelity effects (dual icon shadows, fine genie mesh, no glass downgrade,
    /// full framerate) regardless of the detected GPU tier.</summary>
    Quality,

    /// <summary>Force reduced effects everywhere (single icon shadow, coarse genie mesh, Liquid Glass
    /// rendered as Acrylic, capped framerate) — smoothest on weak hardware.</summary>
    Performance,
}

/// <summary>How a window animates as it minimizes into / restores from the dock.</summary>
public enum MinimizeEffect
{
    /// <summary>A hard, fast mesh funnel to a point — like paper sucked into a vacuum.</summary>
    Suck,

    /// <summary>Simple scale-down to the dock tile (and reverse on restore).</summary>
    Scale,

    /// <summary>A mesh warp with a bulging neck — like smoke flowing into / out of a bottle.</summary>
    Genie,
}

/// <summary>
/// Root persisted configuration for Dockable. Serialized to
/// %APPDATA%\Dockable\settings.json by <see cref="Services.SettingsStore"/>.
/// </summary>
public sealed class DockSettings
{
    public DockEdge Edge { get; set; } = DockEdge.Bottom;

    /// <summary>
    /// UI language culture code (e.g. "en", "pt-BR", "es", "uk", "zh-Hans"). Null = not yet chosen;
    /// on first run it's resolved from the Windows display language (falling back to English) and
    /// then persisted.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>Color theme: follow the OS (System) or force Light/Dark.</summary>
    public DockTheme Theme { get; set; } = DockTheme.System;

    /// <summary>Dock bar background style (Translucent / Acrylic / Liquid Glass). Defaults to Liquid Glass.</summary>
    public GlassEffect GlassEffect { get; set; } = GlassEffect.LiquidGlass;

    // --- Liquid Glass tuning (power-user parameters; only affect the LiquidGlass effect). Defaults
    // match the values previously hardcoded in DockWindow so the out-of-box look is unchanged. ---

    /// <summary>Gaussian blur sigma (device px) for the frosted-glass blur. 0 = sharp.</summary>
    public double GlassBlurRadius { get; set; } = 1.4;

    /// <summary>Rim refraction strength — how far (device px) the sample is pulled inward at the edge.</summary>
    public double GlassDistortion { get; set; } = 34.0;

    /// <summary>Multiplier on the bar's tint (background) alpha. 1.0 = the base tint; higher = more opaque.</summary>
    public double GlassTintOpacity { get; set; } = 1.0;

    /// <summary>Colour saturation/vibrance of the backdrop behind the glass (1.0 = unchanged).</summary>
    public double GlassSaturation { get; set; } = 1.8;

    /// <summary>Chromatic aberration strength at the rim (0 = none).</summary>
    public double GlassAberration { get; set; } = 0.5;

    /// <summary>Peak rim-specular (sheen) intensity on hover (0 = no glint).</summary>
    public double GlassRimHighlight { get; set; } = 0.5;

    /// <summary>
    /// How aggressively to trade visual fidelity for animation smoothness. Auto (default) resolves from
    /// the GPU's WPF render tier — full effects on capable GPUs, reduced on weak ones — while Quality /
    /// Performance force the respective end regardless of hardware.
    /// </summary>
    public PerformanceMode PerformanceMode { get; set; } = PerformanceMode.Auto;

    /// <summary>Window minimize/restore animation style.</summary>
    public MinimizeEffect MinimizeEffect { get; set; } = MinimizeEffect.Genie;

    /// <summary>Speed multiplier for the minimize/restore effect. The Preferences slider snaps to
    /// Slow (0.01) … Default (0.5) … Fast (1.0); 0.5 is the middle "Default" tick.</summary>
    public double EffectSpeed { get; set; } = 0.5;

    /// <summary>Base (un-magnified) icon size in DIPs.</summary>
    public double IconSize { get; set; } = 48;

    /// <summary>Maximum magnified icon size in DIPs (Phase 2).</summary>
    public double MaxIconSize { get; set; } = 96;

    /// <summary>Cursor influence radius for magnification, in DIPs (Phase 2).</summary>
    public double MagnificationRadius { get; set; } = 160;

    /// <summary>Whether the macOS-style fisheye magnification is enabled (Phase 2).</summary>
    public bool MagnificationEnabled { get; set; } = true;

    /// <summary>
    /// How the Windows taskbar is shown while Dockable runs: Always (visible), Auto (native auto-hide,
    /// reveal on hover), or Never (hidden entirely — the default; the dock replaces it). The pre-launch
    /// state is restored on exit/crash/kill (in-process handlers + the out-of-process watchdog).
    /// </summary>
    public TaskbarVisibility TaskbarVisibility { get; set; } = TaskbarVisibility.Never;

    /// <summary>
    /// Show the macOS-style menu bar: a thin strip docked at the top of the primary monitor with the
    /// focused window's title, keyboard layout, a quick-settings shortcut, the system tray, and a clock.
    /// On by default (it reserves a strip at the top of the screen; turn off from Dock Preferences).
    /// </summary>
    public bool ShowMenuBar { get; set; } = true;

    /// <summary>
    /// Automatically hide and show the Dock (macOS-style). Off by default. NOTE: only the setting
    /// exists so far — the hide/reveal behavior itself is not implemented yet.
    /// </summary>
    public bool AutoHideDock { get; set; }

    /// <summary>
    /// Hide the dock (and menu bar) entirely — window hidden, not rendered — while a full-screen or
    /// borderless-fullscreen app/game owns their monitor. On by default; when off they stay visible
    /// over full-screen content.
    /// </summary>
    public bool HideOnFullscreen { get; set; } = true;

    /// <summary>Show the running-indicator dot under apps that have open windows.</summary>
    public bool ShowRunningIndicators { get; set; } = true;

    /// <summary>Bounce an app's dock icon when it gains a new window (e.g. on launch).</summary>
    public bool AnimateOpeningApps { get; set; } = true;

    /// <summary>
    /// Minimize windows into their app's dock icon (pinned or running) instead of a separate
    /// thumbnail tile. Falls back to a thumbnail tile when the window has no app icon in the dock.
    /// </summary>
    public bool MinimizeIntoIcon { get; set; } = false;

    /// <summary>The user's pinned items, in display order. The Start item is added implicitly.</summary>
    public List<DockItem> Items { get; set; } = new();

    /// <summary>
    /// Dock-owned pinned apps (launch paths: .lnk or .exe), in display order. Null means
    /// "not yet seeded" — on first run it's populated from the current taskbar pin order,
    /// after which the dock owns it (reorder / pin / unpin don't touch the Windows taskbar).
    /// </summary>
    public List<string>? PinnedApps { get; set; }

    /// <summary>
    /// Files and folders pinned to the dock's right section (between the app shortcuts and the
    /// Recycle Bin, macOS-style), in display order, with each folder's Sort/Display/View options.
    /// </summary>
    public List<PinnedPath> PinnedPaths { get; set; } = new();

    /// <summary>Prompt to replicate newly-pinned taskbar shortcuts onto the dock.</summary>
    public bool AskReplicateTaskbarPins { get; set; } = true;

    /// <summary>Prompt to add Dockable to the Windows startup sequence.</summary>
    public bool AskAddToStartup { get; set; } = true;

    /// <summary>
    /// Whether the built-in "Dock Preferences" pin has been seeded once (to the right of the
    /// taskbar-seeded pins). The flag makes removal stick — once the user unpins it, it's never
    /// re-added.
    /// </summary>
    public bool SeededPreferencesPin { get; set; }

    /// <summary>
    /// Whether the default ~/Downloads folder pin was seeded once (macOS ships with Downloads in
    /// the Dock). The flag makes removal stick — once the user unpins it, it's never re-added.
    /// </summary>
    public bool SeededDownloadsPin { get; set; }

    /// <summary>
    /// Taskbar pins we've already seen/offered (resolved targets), so only newly-added pins prompt.
    /// Null = not yet initialized (the first check establishes the baseline silently).
    /// </summary>
    public List<string>? KnownTaskbarPins { get; set; }

    /// <summary>
    /// Friendly display names for pinned launch paths, captured when a pin is created (a dropped app's
    /// open name, or a shortcut's name) — because a resolved target (e.g. <c>chrome.exe</c>) or an
    /// AppsFolder AUMID doesn't carry the human name the .lnk / running app had.
    /// </summary>
    public Dictionary<string, string>? PinNames { get; set; }

    public static DockSettings CreateDefault() => new();
}
