# Dockable

A macOS-style **Dock for Windows 11**. It sits at the bottom of the screen, mirrors your taskbar
(pinned + running apps), magnifies icons macOS-style on hover, opens the Start menu, optionally hides
the Windows taskbar, and replaces the window minimize/restore with a **genie**, **scale**, or **suck**
warp into the dock. Fully localized — English, Português (Brasil), Español, Уукраїнська, and 中文.

Created by **[Jezz Lucena](https://github.com/jezzlucena)**.

*Proudly vibe coded.*

## Features

### The dock
- Borderless, topmost, translucent **glass bar** — rounded (24px), hairline border, soft drop shadow.
- **Light / Dark / Auto** theme. *Auto* follows the Windows app theme and switches live when you
  change it. Pick it from the tray menu or **Dock Preferences → Appearance**.
- macOS-style **fisheye magnification**: icons swell and neighbours spread as the cursor passes,
  growing above the bar with smooth easing. Base size and max magnified size are configurable.
- **Hover labels** — a rounded balloon with a downward arrow, centered above the magnified icon.
- **Icons cast a soft layered drop-shadow.**
- **Group separators** between pinned apps, running apps, and minimized windows.
- **UI sound effects** for actions like emptying the Recycle Bin, dragging to the bin, and unpinning.

### Apps & the taskbar mirror
- **Start tile** opens the Windows Start menu.
- Shows the same apps as your taskbar: **pinned shortcuts** + **currently-running apps**, each with a
  **running-indicator** dot. Click an app to focus its window(s) — clicking a group brings *all* its
  windows forward — or launch it if it isn't running.
- Pins are **dock-owned**: seeded once from your taskbar's pin order, then managed in the dock
  (your Windows taskbar is never modified). Updates live (1s poll + a watch on the pinned folder).
- **Running (unpinned) apps keep a stable order** by when they were opened — they don't reshuffle as
  you focus different windows.
- Crisp, alpha-correct icons extracted at up to 256px via the Windows shell.
- **Recycle Bin** pinned at the far right, with a state-aware empty/full icon.

### Drag & drop
- **Drag a pinned icon to reorder** it; drag an external file from Explorer onto the dock to pin it.
- **Drag an icon out** of the dock (it follows the cursor anywhere on screen).
- **Remove a pinned shortcut** by either holding it steady for ~0.5s while dragging (a **"Remove"**
  tag appears) **or dropping it on the Recycle Bin**; release to unpin. Running-but-unpinned apps and
  minimized-window tiles never show "Remove" and **animate back** to their slot on release.
- **Drag a separator up/down to resize** the dock (it shows a vertical-resize cursor) — this is the
  same **Size** setting as in Dock Preferences and they stay in sync.

### Minimize / restore
- Minimizing a window is replaced with an animation into a **dock thumbnail tile**; clicking the tile
  reverses it and restores the window. A window minimized into the dock is also restored if you click
  its app-group icon.
- The minimize gesture is **caught before Windows acts on it** — clicking a window's minimize button,
  **Win+Down**, or **Win+M** — so the captured window is already on screen as the animation's first
  frame and there's **no flash** of the window vanishing first. (Some custom title bars fall back to
  the standard post-minimize path.)
- **Win+M minimizes everything one window at a time**, each warping into the dock; **Win+Down**
  minimizes the focused window (and focuses the next one, like Windows does).
- Three effects, chosen in **Dock Preferences → Minimize windows using**:
  - **Suck** — a hard, fast mesh funnel to a point.
  - **Scale** — the window scales down to (and back from) its tile.
  - **Genie** — a macOS-style 3D mesh warp with a bulging neck.
- Optionally minimize a window **into its app's dock icon** instead of a separate thumbnail tile
  (Dock Preferences → *Minimize windows into application icon*).
- **Stays in sync with the rest of Windows:** restoring a window from the taskbar or Alt+Tab clears its
  dock tile; windows already minimized when the dock launches are adopted into tiles (or their app
  icon); and quitting the dock restores every window it had minimized, so nothing is left stranded.

### Window behavior
- **Always visible** — the dock reserves a strip via a Win32 AppBar sized to **exactly the resting
  (un-magnified) dock**, so maximized windows sit flush against it with no gap and no overlap; the
  magnified icons bleed over windows beneath without reserving extra space. The reservation updates
  when you resize the dock (the Preferences **Size** slider or dragging a separator). The dock clips
  itself to the resting bar when idle so the overflow area stays click-through.
- **Hide the Windows taskbar** (tray → *Hide taskbar*, on by default) via the taskbar's **native
  auto-hide**, which is self-restoring — even a hard kill leaves a usable, reveal-on-edge taskbar.
- Per-monitor-v2 DPI awareness; stays out of the Alt+Tab switcher.
- **Single instance** — a second launch bows out to the running dock.
- System tray icon (left-click toggles the dock; right-click for theme, preferences, about, and exit).
- Settings persist to `%APPDATA%\Dockable\settings.json`.

### Languages & About
- **Internationalized** — every user-facing string (preferences, menus, tray, dialogs, the About
  window) is localized into **English, Português (Brasil), Español, Уукраїнська, and 中文**.
- Pick the language in **Dock Preferences → System → Language**. Changes apply **live** — the open
  preferences window, context menus, and the tray menu all re-localize without a restart. On first
  run the language follows the Windows display language, falling back to English.
- **About Dockable** (right-click a separator or empty dock area → *About Dockable*, also in the
  tray menu): app version, the stack, and a link to the author.

### Dock Preferences
A light, macOS-style settings window (right-click a separator, tray → *Dock Preferences…*, or the
built-in **Dock Preferences** dock tile). It stays above other windows, and while open it occupies a
**running-app slot** on the dock — click to refocus, or right-click to **Quit** it (without quitting
the dock) or toggle **Keep in Dock**. It also **minimizes into the dock** like any window (thumbnail
or its own icon, per *Minimize windows into application icon*). Its pinned shortcut is added on first
run (to the right of your taskbar pins) and can be removed like any other pin.

The window itself:
- **System** — **Language** picker and **Open at login** (+ a shortcut to Windows *Startup Apps*).
- **Appearance** — Light / Dark / Auto tiles (each previews the dock in that mode).
- **Dock** — **Size** and **Magnification** sliders; **Position on screen**; **Glass Effect**
  (Translucent / Acrylic / Liquid Glass); **Minimize windows using** (Suck / Scale / Genie);
  **Effect Speed**; and toggles for running indicators, open-animation, and minimize-into-icon.
- **Taskbar** — automatically hide the Windows taskbar.
- (Position on screen currently only implements the bottom edge; the rest are live.)

### Glass / backdrop
- **Glass Effect** (Dock Preferences → Dock): **Translucent** (the lightest, no backdrop window),
  **Acrylic** (live system blur of the desktop behind the bar), or **Liquid Glass** (custom blur +
  saturation with edge refraction, falling back to Acrylic where unsupported). Rendered in a separate
  backdrop window behind the bar so it doesn't conflict with the transparent layered window the
  overflow magnification needs.

## Tech stack
- **C# 12 + WPF** on **.NET 9** (`net9.0-windows10.0.22621.0`), x64, unpackaged.
- **CsWin32** for source-generated Win32 interop (shell icons, AppBar, window styles, hooks).
- **CommunityToolkit.Mvvm** for view-models; **H.NotifyIcon.Wpf** for the tray icon.
- **In-code localization** (no `.resx`/satellite assemblies): a small `Loc` service + a
  `{loc:Loc Key=…}` XAML markup extension that updates bound text live on a language change.
- Per-monitor-v2 DPI awareness declared in `app.manifest`.

## Build & run

Requires the **.NET 9 SDK**. From the repo root:

```sh
dotnet build src/Dockable/Dockable.csproj -c Debug
dotnet run   --project src/Dockable/Dockable.csproj
```

Or run the built exe directly:
`src/Dockable/bin/Debug/net9.0-windows10.0.22621.0/Dockable.exe`.

The dock appears centered at the bottom of the primary display. (If a build fails only at the file-copy
step, a previous `Dockable.exe` is still running — close it and rebuild.)

### Distribution (Steam)

Dockable ships on Steam as a **self-contained x64 build** (the .NET 9 runtime and WPF are bundled, so
players install nothing). Produce it with:

```powershell
pwsh -File steam\build-steam.ps1
```

This publishes to `src\Dockable\bin\Publish\Steam\win-x64\` (launch target `Dockable.exe`). SteamPipe
upload scripts and a full walkthrough are in [`steam/`](steam/README.md).

## Project layout

```
src/Dockable/
  App.xaml(.cs)            Entry point; single-instance, crash logging, taskbar restore, language init
  DockWindow.xaml(.cs)     The dock: layout, positioning, tray menu, magnification loop, drag,
                           themes, hover label, minimize orchestration
  SettingsWindow.xaml(.cs) "Dock Preferences" window
  AboutWindow.xaml(.cs)    "About Dockable" window (icon, version, stack, author credit)
  ConfirmDialog.cs / InputDialog.cs  Small code-built Yes/No and text-prompt dialogs
  Sounds.cs                Short UI sound effects (WAV) under Assets/Sounds
  app.manifest             Per-monitor-v2 DPI awareness
  NativeMethods.txt        CsWin32 API list
  Localization/            Loc (runtime service), LocData (per-language string tables),
                           LocExtension ({loc:Loc Key=…} markup extension)
  Models/                  DockItem, DockItemKind, DockSettings (+ DockTheme / GlassEffect /
                           MinimizeEffect enums)
  ViewModels/              DockViewModel, DockItemViewModel, DockLayoutEngine (fisheye + drag layout)
  Services/                SettingsStore (atomic JSON persistence)
  Shell/                   ShortcutService (launch + shell icon extraction)
  Interop/                 Start menu, monitors/DPI, AppBar, taskbar auto-hide, taskbar mirror,
                           pin matching, minimize hooks (WinEvent + low-level gesture interception),
                           window control, system theme, Recycle Bin
  Genie/                   Window capture + thumbnail cache; Genie / Scale animators
                           (IMinimizeAnimator); acrylic/liquid-glass backdrop; pre-warmed overlays
  Converters/              XAML value converters
```

## Notes & known limits
- Pins are dock-owned and never written back to the Windows taskbar (Windows blocks programmatic
  taskbar pinning/reordering).
- Secondary-monitor placement is approximate; UWP/Store pins may not match a running window; elevated
  apps' paths are unreadable unless Dockable is elevated.
