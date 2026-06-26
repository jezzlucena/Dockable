# Dockable

A macOS-style **Dock for Windows 11**. It sits at the bottom of the screen, mirrors your taskbar
(pinned + running apps), magnifies icons macOS-style on hover, opens the Start menu, optionally hides
the Windows taskbar, and replaces the window minimize/restore with a **genie** (or **scale**) warp
into the dock.

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
- **Hold a pinned shortcut steady for ~0.5s** while dragging → a **"Remove"** tag appears; release to
  unpin it. Running-but-unpinned apps and minimized-window tiles never show "Remove" and **animate
  back** to their slot on release.
- **Drag a separator up/down to resize** the dock (it shows a vertical-resize cursor) — this is the
  same **Size** setting as in Dock Preferences and they stay in sync.

### Minimize / restore
- Minimizing a window (title-bar button, Win+Down, …) is intercepted and replaced with an animation
  into a **dock thumbnail tile**; clicking the tile reverses it and restores the window. A window
  minimized into the dock is also restored if you click its app-group icon.
- Two effects, chosen in **Dock Preferences → Minimize windows using**:
  - **Genie** — a macOS-style 3D mesh warp.
  - **Scale** — the window scales down to (and back from) its tile.

### Window behavior
- **Docking modes** (tray → *Auto-hide*):
  - *Auto-hide* (default): the dock tucks to the screen edge and slides in on hover.
  - *Always visible*: reserves screen space via a Win32 AppBar (only the resting-bar height — the
    magnified icons bleed over windows beneath without reserving extra space).
- **Hide the Windows taskbar** (tray → *Hide taskbar*, on by default) via the taskbar's **native
  auto-hide**, which is self-restoring — even a hard kill leaves a usable, reveal-on-edge taskbar.
- Per-monitor-v2 DPI awareness; stays out of the Alt+Tab switcher.
- **Single instance** — a second launch bows out to the running dock.
- System tray icon (left-click toggles the dock; right-click for the menu, theme, preferences, exit).
- Settings persist to `%APPDATA%\Dockable\settings.json`.

### Dock Preferences
A light, macOS-style settings window (right-click a separator, or tray → *Dock Preferences…*):
- **Appearance** — Light / Dark / Auto tiles (each previews the dock in that mode).
- **Size** and **Magnification** sliders (live).
- **Minimize windows using** — Genie / Scale (live).
- Position on screen, Minimize-into-icon, and the other toggles are present but not yet wired.

### Deliberately not (yet) included
- **Acrylic / desktop blur + saturation** behind the bar. True Win11 acrylic is incompatible with the
  transparent layered window the overflow magnification needs (and can't take the rounded-bar shape),
  so it's deferred; a real backdrop blur would need a separate non-layered backdrop window.

## Tech stack
- **C# 12 + WPF** on **.NET 9** (`net9.0-windows10.0.22621.0`), x64, unpackaged.
- **CsWin32** for source-generated Win32 interop (shell icons, AppBar, window styles, hooks).
- **CommunityToolkit.Mvvm** for view-models; **H.NotifyIcon.Wpf** for the tray icon.
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

## Project layout

```
src/Dockable/
  App.xaml(.cs)            Entry point; single-instance, crash logging, taskbar restore
  DockWindow.xaml(.cs)     The dock: layout, positioning, tray menu, magnification loop, drag,
                           themes, hover label, minimize orchestration
  SettingsWindow.xaml(.cs) "Dock Preferences" window
  app.manifest             Per-monitor-v2 DPI awareness
  NativeMethods.txt        CsWin32 API list
  Models/                  DockItem, DockItemKind, DockSettings (+ DockTheme / MinimizeEffect enums)
  ViewModels/              DockViewModel, DockItemViewModel, DockLayoutEngine (fisheye + drag layout)
  Services/                SettingsStore (atomic JSON persistence)
  Shell/                   ShortcutService (launch + shell icon extraction)
  Interop/                 Start menu, monitors/DPI, AppBar, taskbar auto-hide, taskbar mirror,
                           pin matching, minimize hook, window control, system theme, Recycle Bin
  Genie/                   Window capture + thumbnail cache; GenieAnimator / ScaleAnimator
                           (IMinimizeAnimator); pre-warmed reusable overlays
  Converters/              XAML value converters
```

## Notes & known limits
- Pins are dock-owned and never written back to the Windows taskbar (Windows blocks programmatic
  taskbar pinning/reordering).
- Secondary-monitor placement is approximate; UWP/Store pins may not match a running window; elevated
  apps' paths are unreadable unless Dockable is elevated.
