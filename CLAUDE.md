# CLAUDE.md — Dockable

A macOS-style **Dock for Windows 11**: a bottom bar that mirrors the taskbar (pinned + running
apps), magnifies icons on hover, opens the Start menu, auto-hides the real taskbar, supports
Light/Dark/Auto theming + a translucent/acrylic/liquid-glass bar + a Dock Preferences window,
replaces the window minimize/restore with a "suck", "scale", or "genie" warp into the dock, and is
**fully localized** (English, pt-BR, es, uk, zh-Hans). This file is the standing guide for any Claude
Code session here — read it first; it captures everything not obvious from the code.

---

## ⛔ Working agreement — do NOT visually test

- **The user does ALL visual/interaction testing.** Never take screenshots, launch the app "to see
  it", move the cursor, or add on-screen diagnostics for visual checks.
- **Your verification stops at: the code compiles** (`dotnet build` succeeds, 0 errors).
- Only run the app to debug a **non-visual** crash the user reports (read
  `%APPDATA%\Dockable\crash.log`), then stop it.
- It IS fine to inspect data for development (registry values, .lnk targets, window properties via
  one-off PowerShell) — that's not "visual testing".
- After changes: confirm a clean build, summarize what changed, ask the user to test. Tune blind
  constants (animation timings, sizes) only when the user reports how it feels.

---

## Environment

- **`dotnet` is NOT on PATH.** Always invoke it by full path:
  `& "C:\Program Files\dotnet\dotnet.exe"`. The Bash tool can't see dotnet; use the PowerShell tool.
- Only the **.NET 9 SDK (9.0.315)** is installed (no .NET 10). Target framework is
  `net9.0-windows10.0.22621.0`, **x64**, WPF, unpackaged.
- **Stop the running app before building** — the running `Dockable.exe` locks the output, so the
  build fails at the copy step (compilation still succeeds; look for `: error CS`, not MSB copy
  errors). **For Debug builds it's fine to kill the app yourself first** (standing permission from the
  user) so the copy step doesn't fail — just run the stop command before building.
  Stop command: `Get-Process Dockable -ErrorAction SilentlyContinue | Stop-Process -Force`.
  **After a successful Debug build, RESTART the app — but only if it was actually running before
  you killed it** (don't launch a dock the user didn't have open). Capture the state before the
  kill and relaunch detached after the build:
  ```powershell
  $wasRunning = [bool](Get-Process Dockable -ErrorAction SilentlyContinue)
  Get-Process Dockable -ErrorAction SilentlyContinue | Stop-Process -Force
  & "C:\Program Files\dotnet\dotnet.exe" build "src\Dockable\Dockable.csproj" -c Debug
  if ($wasRunning) { Start-Process "src\Dockable\bin\Debug\net9.0-windows10.0.22621.0\Dockable.exe" }
  ```
  Only one dock runs (single-instance Mutex). Force-killing is safe: a hidden `powershell.exe`
  **taskbar watchdog** (`Interop/TaskbarWatchdog`, spawned at startup) notices the kill, restores the
  taskbar's pre-launch state, and exits on its own — killing `Dockable` by name doesn't touch it
  (different image name), and a stray watchdog from a previous run skips the restore if a new dock is
  already running. Don't bother killing the watchdog; it self-terminates seconds after the app dies.
- **PowerShell is 5.1.** No `out var` / `var` in `Add-Type` C# (use explicit types). A script that
  combines `Remove-Item` with a `C:\Program...` literal is sandbox-blocked — split the steps or use
  `Clear-Content` / `Join-Path`.
- `Date.Now`-style nondeterminism is fine in app code (only workflow scripts forbid it).

## Build

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build "src\Dockable\Dockable.csproj" -c Debug
```

The user runs the built exe directly:
`src\Dockable\bin\Debug\net9.0-windows10.0.22621.0\Dockable.exe`.

### Release / Steam publish

The Steam build is a **self-contained x64** publish (runtime + WPF bundled; no install needed),
driven by the `SteamRelease` profile (`src/Dockable/Properties/PublishProfiles/SteamRelease.pubxml`):
self-contained + ReadyToRun, **not** single-file, **not** trimmed (WPF isn't trim-safe).

```powershell
pwsh -File steam\build-steam.ps1   # → src\Dockable\bin\Publish\Steam\win-x64\Dockable.exe
```

SteamPipe scripts + upload walkthrough live in `steam/` (`app_build.vdf`, `depot_build.vdf`,
`README.md`). No Steamworks SDK is integrated yet (Steam launches the plain exe). Bump
`<Version>` in `Dockable.csproj` per release.

### Portable single-file publish

A truly portable **one-file** `Dockable.exe` (~84 MB) for hand-out outside Steam, via the `Portable`
profile (`Properties/PublishProfiles/Portable.pubxml`): self-contained + **single-file** with
`IncludeNativeLibrariesForSelfExtract` (native libs embedded + self-extracted) + compression +
ReadyToRun; **not** trimmed (WPF isn't trim-safe). All assets are embedded so nothing sits beside the
exe — note the UI **sounds are WPF `Resource`s** (loaded via a `pack://` stream in `Sounds.cs`), not
`Content` copied to the output, precisely so the single-file build has no loose `.wav`s. Anything new
that must ship has to be an embedded resource (or it breaks the single-file guarantee).

```powershell
pwsh -File scripts\build-portable.ps1   # → src\Dockable\bin\Publish\Portable\win-x64\Dockable.exe
```

### CsWin32 P/Invoke workflow

Win32 interop is generated by **CsWin32** from `src/Dockable/NativeMethods.txt` (one API/type per
line). To add a Win32 call: add its name there, build, then call it as
`Windows.Win32.PInvoke.<Name>` (free functions) or via the generated type in its namespace.

To discover the exact generated signature (they vary — `in`/`out`/pointer/`Span`, friendly
overloads, namespaces), build once with the files emitted and read them:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build "src\Dockable\Dockable.csproj" -c Debug `
  -p:EmitCompilerGeneratedFiles=true --no-incremental
# then read: src\Dockable\obj\Debug\net9.0-windows10.0.22621.0\generated\Microsoft.Windows.CsWin32\...\*.g.cs
```

- CsWin32 types are `internal` — anything exposing them in a public signature must also be internal
  (e.g. `WindowFilter`).
- Some newer flags aren't in the metadata; cast the literal (e.g.
  `(PRINT_WINDOW_FLAGS)2` for `PW_RENDERFULLCONTENT`). `PKEY_AppUserModel_ID` isn't generated —
  construct the `PROPERTYKEY` (fmtid `{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}`, pid 5).
- A few APIs aren't in Win32 metadata at all and are hand-written `DllImport`/WinRT activation
  (the acrylic backdrop hosts a `Windows.UI.Composition` tree; the liquid-glass shader is compiled at
  runtime via `d3dcompiler_47`). Prefer CsWin32; note in a comment when you can't.

## Packages (`Dockable.csproj`)

- `CommunityToolkit.Mvvm` 8.4.0 — `[ObservableProperty]`, `[NotifyPropertyChangedFor]`.
- `H.NotifyIcon.Wpf` 2.3.0 — tray icon. **Quirk:** its `IconSource` only accepts URI-backed images;
  use `GeneratedIconSource` (a `BitmapSource` subclass) for an in-process glyph.
- `Microsoft.Windows.CsWin32` 0.3.183 — source-generated interop (PrivateAssets=all).
- `SharpVectors.Wpf` 1.8.5 — SVG → WPF drawing. Used by `Shell/SvgIcon` to render real icons for
  .svg/.svgz files (the shell has no SVG thumbnailer — it returns the generic association icon);
  hooked into `ShortcutService.LoadIcon`'s single funnel, so pinned SVG files, stack members, and
  fan rows all show the artwork. Rasterized square/centered at the requested size on the loader's
  worker thread (visual + RenderTargetBitmap created and frozen on that same thread).

---

## Project structure

```
src/Dockable/
  App.xaml(.cs)          Entry point. Single-instance (named Mutex); startup wrapped in try/catch
                         that logs + MessageBoxes + exits (so failures aren't silent zombies);
                         crash logging (%APPDATA%\Dockable\crash.log); DispatcherUnhandledException
                         kept non-fatal; exit/crash restore the taskbar (auto-hide off).
  DockWindow.xaml(.cs)   The dock. Owns: positioning + AppBar reservation, tray menu, context menus,
                         magnification render loop, custom drag-reorder, taskbar-app refresh timer +
                         pinned-folder watcher, minimize/restore orchestration, glass-backdrop +
                         taskbar native auto-hide control, theme application (ApplyTheme/SetTheme),
                         and About/Preferences window launch.
  SettingsWindow.xaml(.cs) "Dock Preferences" window (separator right-click or tray). Light,
                         macOS-style. Sections: System (Language + Performance + Open-at-login),
                         Appearance (Light/Dark/Auto tiles), Dock (Size/Magnification, Position,
                         Glass Effect, Minimize effect, Effect Speed, toggles incl. Auto-hide),
                         Taskbar. Most rows are wired live (Position only implements Bottom).
                         **Settings search**: a sidebar search box filters `SettingsIndex` (entries =
                         panel tag + Loc name key + a row-resolver + English synonym tags; names match
                         in the UI language, tags in English); results replace the nav list; clicking
                         navigates + scrolls to and PULSES the row (accent tint fading back — whole
                         panels navigate with no pulse). Rows are found by walking up from the named
                         control (`RowOf`); new settings should be added to the index.
  MenuBarWindow.xaml(.cs) Optional macOS-style menu bar: a thin top AppBar (primary monitor) showing the
                         focused window's title, a clickable keyboard-layout switcher, Quick-Settings
                         (Win+A) + Notifications (Win+N) buttons, and a clock. Its own AppBarManager
                         (WM_USER+2) + AcrylicBackdrop + ApplyTheme; no magnification/clipping (window ==
                         bar). Owned by App (created/closed per Settings.ShowMenuBar). See feature area below.
  ConfirmDialog.cs       Code-built Yes/No prompt with optional "Do not ask again".
  InputDialog.cs         Code-built single-line text prompt (OK/Cancel) — e.g. Rename.
  AppIcon.cs             The app's own icon loaded once: Large (256px png, windows/Alt-Tab) and
                         Tray (the multi-size Dockable.ico — its 16/32px frames stay crisp in the tray).
  Sounds.cs              Short UI WAV effects (empty-trash / drag-to-trash / remove) via SoundPlayer,
                         loaded from embedded `pack://` resources (so the single-file build has no loose .wav).
  UiBrushes.cs           Shared frozen-brush-from-hex helper + the recurring palette hex constants
                         (AccentHex/InkHex/SurfaceHex) — use it instead of re-rolling a private Brush(hex).
  MenuBuilder.cs         AddItem/AddCheckable for the code-built menus' PLAIN items (header + click).
                         Items with dynamic headers (Quit/Force Quit), Icons, or sender-aware handlers
                         stay hand-built at their call sites — don't force them through the helper.
  DialogChrome.cs        Shared scaffolding of the code-built dialogs (frameless shell, message block,
                         rounded surface card, Cancel/OK button row); per-dialog content stays local.
  app.manifest           Per-monitor-v2 DPI awareness; asInvoker.
  NativeMethods.txt      CsWin32 API list.

  Themes/
    ModernMenu.xaml      Windows 11-style context-menu styles (implicit, app-wide; merged in App.xaml).
  Accessibility/
    DockItemElement.cs   The dock item template's root (a Grid subclass): its UIA peer names each item
                         (DisplayName + running/minimized state) and exposes Invoke → DockWindow.ActivateItem.
    A11y.cs              InvokableRow/InvokableCell — StackPanel/Border subclasses with a UIA Button peer
                         whose Invoke replays MouseLeftButtonUp; used by the code-built click targets
                         (fan/grid rows, settings search rows, menu-bar pills).
  Localization/
    LocData.cs           Per-language string tables (en, pt-BR, es, uk, zh-Hans) + the picker list.
    Loc.cs               Runtime service: indexer + T(key); SetLanguage (live) raises Item[]/event;
                         Initialize resolves saved-or-OS culture → English fallback.
    LocExtension.cs      {loc:Loc Key=…} XAML markup extension → binds to Loc.Instance[Key].
  Models/
    DockItemKind.cs      enum: StartMenu, Shortcut, Separator, MinimizedWindow, TaskbarApp,
                         RecycleBin, PinnedFolder, PinnedFile.
    DockItem.cs          Persisted/transient item shape + factory methods.
    DockSettings.cs      Root settings + DockEdge/GlassEffect/DockTheme/MinimizeEffect enums
                         (see Settings schema below).
    PinnedPath.cs        A pinned file/folder + its FolderSortBy/FolderDisplayAs/FolderViewContentAs.
  ViewModels/
    MenuBarViewModel.cs  Menu bar state: shared DockSettings (theme/glass) + live Title/KeyboardLabel/TimeText.
    DockViewModel.cs     Owns Items (ObservableCollection), Settings, geometry props; composes
                         sections (Start + apps + separator + minimized + pinned files/folders +
                         Recycle Bin) and reconciles in place; taskbar-app refresh + matching;
                         dock-owned pin + pinned-path mutations.
    DockItemViewModel.cs Per-item: Icon, X/Y/RenderSize/CurrentScale (layout), IsRunning, IsPinned,
                         IsDragging, Hwnd, AppKey, LaunchPath, Windows.
    DockLayoutEngine.cs  Fisheye magnification + live drag layout + window/bar geometry.
  Services/
    SettingsStore.cs     Atomic JSON load/save of DockSettings (%APPDATA%\Dockable\settings.json).
  Shell/
    ShortcutService.cs   Launch(path) via shell; RevealInExplorer; LoadIconAsync
                         (IShellItemImageFactory → 256px, alpha-correct, off UI thread, E_PENDING retry;
                         pixels read via GetDIBits with an explicit top-down target — see Known decisions).
    FolderContents.cs    A pinned folder's sorted top-level listing (+ shell "Kind" names via SHGetFileInfo).
    StackIcon.cs         Composites a folder's top items into the Stack tile bitmap.
    SvgIcon.cs           Renders .svg/.svgz to icons via SharpVectors (hooked into LoadIconAsync).
  Interop/
    SynthesizedInput.cs  Shared SendInput chord helper (press in order, release in reverse) behind the
                         four OS-gesture openers below.
    WinEventHook.cs      Owns one SetWinEventHook registration: delegate lifetime, double-start guard,
                         stop/RESTART support, optional pid scoping, and the universal
                         idObject==0 && idChild==0 "window itself" filter. All the WinEvent watchers
                         (MinimizeHook, ForegroundWatcher, TitleWatcher ×2, TaskbarHideWatcher,
                         Genie/WindowThumbnailCache) compose instances of it.
    StartMenu.cs         Open Start via a synthesized Win keypress (SynthesizedInput).
    QuickSettings.cs     Open the OS Quick Settings flyout (network/sound) via synthesized Win+A.
    Notifications.cs     Open the OS Notification Center / calendar flyout via synthesized Win+N.
    TrayOverflow.cs      Open the system-tray overflow ("show hidden icons") flyout via synthesized
                         Win+B (focus the tray, reveal the taskbar) then Enter (activate the chevron).
                         Call off the UI thread (it sleeps 200 ms between the chords).
    SystemActions.cs     Menu-bar Windows-logo menu power/session actions: Sleep (SetSuspendState),
                         Lock (LockWorkStation), Restart/ShutDown/LogOut (shell out to shutdown.exe).
    TitleWatcher.cs      SetWinEventHook(EVENT_SYSTEM_FOREGROUND + EVENT_OBJECT_NAMECHANGE) → TitleChanged
                         (menu bar's live focused-window title).
    KeyboardLayouts.cs   Current layout (GetKeyboardLayout) + installed list (GetKeyboardLayoutList) +
                         switch the foreground app (PostMessage WM_INPUTLANGCHANGEREQUEST).
    AppMenu.cs           AppMenuEntry model (+ AppMenuSource enum) for the menu bar's mirrored app menus.
    Win32AppMenu.cs      Tier-1 global menus: read a window's classic HMENU bar cross-process
                         (GetMenu/GetMenuString), host its dropdown from our bar (TrackPopupMenuEx +
                         WM_INITMENUPOPUP relay), post the pick back (WM_COMMAND / WM_MENUCOMMAND).
    UiaAppMenu.cs        Tier-2 fallback: UI Automation MenuBar scan (WPF/Electron/Qt); labels mirror,
                         but invoking expands the app's OWN menu in place. Cached per HWND (incl.
                         negative); reads/invokes run off the UI thread (huge UIA trees are slow).
    Monitors.cs          Per-monitor bounds/workarea (px) + DPI for a window.
    AppBarManager.cs     SHAppBarMessage register/reserve (always-visible docking).
    Taskbar.cs           Toggle the taskbar's NATIVE auto-hide (SHAppBarMessage ABM_SETSTATE);
                         also SW_SHOWs the tray windows to undo any legacy force-hide.
    TaskbarWatchdog.cs   Out-of-process restore safety net: spawns a hidden powershell.exe (different
                         image name — survives kill-by-name; no extra binary to ship, so the portable
                         single-file build is unaffected) that Wait-Process-es on the dock's PID, then
                         restores the pre-launch taskbar state via SHAppBarMessage/ShowWindow (Add-Type
                         C#, kept C# 5 / PS 5.1-safe) and exits. Skips the restore if a new dock
                         instance is already running (quick restart race).
    TaskbarApps.cs       Read taskbar pins (registry order + .lnk targets/AUMIDs) + enumerate
                         taskbar-eligible app windows. Per-window exe path + AUMID are CACHED by HWND
                         (IdentityCache: pid-checked against handle recycling, empty AUMIDs retried ≤3×
                         for late-setting apps, dead HWNDs evicted each enumeration) — resolving them
                         fresh was the 1 s refresh's main cost. Titles stay live/uncached; the public
                         GetWindowExePath/GetWindowAumid keep uncached semantics for event-driven callers.
    PinMatcher.cs        Multi-strategy "does this window belong to this pin?". Built matchers are
                         cached per pin path (their inputs are already permanently memoized).
    WindowFilter.cs      Shared "is this a normal app window" test (internal; takes HWND).
    MinimizeHook.cs      SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART..MINIMIZEEND) → WindowMinimizing /
                         WindowUnminimized events (the latter for external taskbar/Alt+Tab restores).
    MinimizeInterceptHook.cs  Low-level WH_MOUSE_LL + WH_KEYBOARD_LL hooks that PRE-EMPT a minimize
                         gesture so the warp's frame 0 paints before the OS minimizes (no flash):
                         min-button click (NCHITTEST==HTMINBUTTON, else DWMWA_CAPTION_BUTTON_BOUNDS
                         left-third; arm-on-down, act-on-up, swallow both) → MinimizeRequested; Win+Down
                         → MinimizeRequested; Win+M → MinimizeAllRequested.
    ForegroundWatcher.cs SetWinEventHook(EVENT_SYSTEM_FOREGROUND) → ForegroundChanged event.
    Fullscreen.cs        Shared test: is the foreground window covering a given window's monitor
                         (exclusive or borderless-fullscreen)? Used by the dock + menu bar to hide.
    WindowControl.cs     Per-window transitions suppression, minimize/restore (incl. no-activate +
                         no-foreground variants), activate, restore-rect.
    SystemTheme.cs       Reads the Windows light/dark app theme (registry AppsUseLightTheme); also
                         IsDarkEffective(DockTheme) + IsImmersiveColorChange(lParam), shared by the
                         dock's and menu bar's ApplyTheme/WndProc so the two never drift.
    StartupManager.cs    HKCU Run-key "run at login" entries (IsEnabled/Enable/Disable).
    RecycleBin.cs        IsEmpty / Empty (with OS prompt) / SendToRecycleBin (SHFileOperation
                         FO_DELETE + FOF_ALLOWUNDO); state-aware empty/full icon via the shell.
    KnownFolders.cs      SHGetKnownFolderPath (the Downloads seed — the folder can be relocated).
    AcrylicBackdrop.cs   Separate non-layered click-through backdrop window hosting a
                         Windows.UI.Composition acrylic blur, clipped to the bar's rounded rect.
    ShaderCompiler.cs    Compiles HLSL → ps bytecode at runtime via d3dcompiler_47 (no fxc).
  Genie/
    WindowCapture.cs     BitBlt(CAPTUREBLT) screen-grab of a window → BitmapSource.
    WindowThumbnailCache.cs  Caches a recent capture per visible window; also proactively suppresses
                         the foreground window's OS transitions.
    OverlayAnimatorBase.cs  Shared engine for both animators: the pre-warmed click-through overlay
                         window lifecycle, the render loop (frame-rate cap that NEVER skips the final
                         frame), FinishCurrent (finalize an in-flight play so its onCompleted isn't
                         lost), and the _playSeq-guarded restore hold. Subclasses supply
                         BuildOverlayContent/SetContent/PreparePlay/ApplyFrame(rawT)/BaseDurationMs.
                         ApplyFrame receives the RAW warp t — easing is the subclass's business
                         (the genie eases per-vertex; Scale SmoothSteps the whole frame).
    GenieAnimator.cs     WPF-3D mesh-warp subclass; Style = Suck or Genie curve; RefreshQuality
                         tears the overlay down so the next play rebuilds the mesh.
    ScaleAnimator.cs     Subclass that scales the capture down to the tile.
    IMinimizeAnimator.cs Common Play/Prewarm interface; DockWindow picks the animator per setting.
    RefractionEffect.cs  WPF ShaderEffect refracting the captured backdrop (liquid-glass rim distortion).
  Converters/
    BoolToVisibilityConverter.cs  + FallbackVisibilityConverter (multi-binding).
```

`Dockable.sln` is at the repo root.

---

## Core architecture & conventions

- **Single window, WPF, MVVM-ish.** `DockWindow` is borderless, `AllowsTransparency=true`, topmost,
  `ShowInTaskbar=false`. The dock bar is a translucent rounded `Border`; icons are absolutely
  positioned in a `Canvas` (ItemsControl with a Canvas ItemsPanel; container style binds
  `Canvas.Left/Top/Width/Height` to the item VM's `X/Y/RenderSize`).
- **Window sizing**: binding `Window.Width/Height` to the VM is unreliable in WPF — the window
  **mirrors `DockViewModel.WindowWidth/Height` from code-behind** (`ApplyWindowSize`, via
  `PropertyChanged`). Don't reintroduce the XAML bindings.
- **Items are composed from sections** in `DockViewModel`: `Start + taskbar apps + separator (only
  if any minimized) + minimized tiles`, then `ReconcileItems` updates the ObservableCollection
  in place (move/insert/remove minimally) so the 1 s refresh doesn't churn the UI or reset
  magnification. Reused tile VMs are keyed (`_appByKey`).
- **Magnification** (`DockLayoutEngine.Update`, run each frame from `DockWindow`'s
  `CompositionTarget.Rendering` loop that self-detaches when idle): per-icon scale from cursor
  distance via a raised-cosine falloff, smoothed; cumulative centered cell layout with neighbour
  displacement; bottom-anchored growth. Each cell advances by `baseSize*scale`; the **icon renders
  at `IconFill` (0.84) of its cell**, centered, so icons sit smaller within the bar. Thin bar; icons
  overflow above it. `Recompute()` sets window/bar geometry on item/settings changes.
  - **Hover is geometry-driven, not WPF `MouseLeave`.** `MouseEnter` (reliable on the opaque bar) kicks
    the loop off, but each frame recomputes `_hovering` from the real cursor (`GetCursorPos` →
    `PointFromScreen`, tested against the window footprint). On a transparent layered window the cursor
    crossing a fully-transparent overflow pixel spuriously fires `MouseLeave`, which would drop
    magnification and make the dock + hover labels jitter — geometry is stable. (Skipped during
    drag/resize, which pin `_hovering` themselves.)
- **Conventions**: C# 12, nullable enabled, file-scoped namespaces, ImplicitUsings. Short XML docs on
  public members; inline comments only where intent is non-obvious (esp. *why* a Win32 call behaves a
  way). Keep per-frame work off WPF layout passes. Shell icon extraction must be off the UI thread
  and the bitmap `Freeze()`d; `IShellItemImageFactory.GetImage` returns **E_PENDING (0x8000000A)** for
  uncached items — retry briefly.
- **Settings**: persisted at `%APPDATA%\Dockable\settings.json`. **Never revert the user's
  settings.json**; if you seed test data, restore it. (`Items` is legacy/unused; `PinnedApps` is the
  live pin list.)

### Settings schema (`DockSettings`)

| Field | Default | Meaning |
|---|---|---|
| `Edge` | `Bottom` | Dock edge (`DockEdge`; only Bottom implemented). |
| `Language` | null | UI culture code (`en`/`pt-BR`/`es`/`uk`/`zh-Hans`). null = resolve from OS → en, then persist. |
| `Theme` | `System` | `DockTheme` (System/Light/Dark; "System" shown as "Auto"). |
| `GlassEffect` | `LiquidGlass` | `GlassEffect` bar background: Simple (translucent) / Acrylic / LiquidGlass. |
| `MinimizeEffect` | `Genie` | `MinimizeEffect`: Suck / Scale / Genie. |
| `EffectSpeed` | 1.0 | Minimize/restore speed multiplier (>1 faster, <1 slower). |
| `IconSize` | 48 | Base icon cell size (DIP). |
| `MaxIconSize` | 96 | Max magnified size. |
| `MagnificationRadius` | 160 | Cursor influence radius (DIP). |
| `MagnificationEnabled` | true | Fisheye magnification on/off. |
| `TaskbarVisibility` | `Never` | `TaskbarVisibility`: Always (visible) / Auto (native auto-hide, reveal on hover) / Never (fully hidden — default; the dock replaces the taskbar). Pre-launch state restored on exit/crash/kill (watchdog). |
| `ShowMenuBar` | true | Show the macOS-style top menu bar (reserves a strip at the top of the primary monitor). |
| `AutoHideDock` | false | "Automatically hide and show the Dock" (Preferences + the dock menu's "Turn Hiding On/Off"). Implemented: the dock slides off its edge when idle (`HideProgress` DP animates, `PositionDock` applies the offset), reveals when the cursor presses a 2px edge sliver (`_autoHideTimer` 120 ms watcher; "activity" = hover, drags, flyout, any menu via `Mouse.Captured`, in-flight warps), and the **AppBar strip stays UNRESERVED the whole time it's on** (`ReserveAppBarSpace` unregisters and bails). |
| `HideOnFullscreen` | true | "Hide on fullscreen apps and games" (Preferences → Dock, under Auto-hide): fully hide the dock + menu bar (windows `Hide()`n, AppBar strips released — not just slid off-screen) while a full-screen/borderless-fullscreen app owns their monitor. Off = they stay visible over full-screen content. Gated in both `UpdateFullscreenState`s; the toggle's `ApplyHideOnFullscreen()` restores a hidden window immediately (bypasses the own-process foreground guard). |
| `ShowRunningIndicators` | true | Show the running-dot under apps with open windows. |
| `AnimateOpeningApps` | true | Bounce an app's icon when it gains a new window (ONE hop; the bounce is an icon-only RenderTransform — `BounceX/Y` set by the engine per edge — so the running dot stays put). Also gates the **attention bounce**: 3 hops when a window flashes its taskbar button (shell hook: `RegisterShellHookWindow` → `HSHELL_FLASH` 0x8006 in WndProc; repeat flashes don't restart a playing bounce). |
| `MinimizeIntoIcon` | false | Minimize into the app's dock icon instead of a separate tile. |
| `Items` | [] | Legacy (old drag-drop pins); unused. |
| `PinnedApps` | null | Dock-owned ordered pin list (launch paths). null = seed from taskbar once. |
| `AskReplicateTaskbarPins` | true | Prompt to replicate newly-pinned taskbar shortcuts onto the dock. |
| `AskAddToStartup` | true | Prompt (once) to add Dockable to Windows startup. |
| `SeededPreferencesPin` | false | Whether the built-in Dock Preferences pin was seeded once (so removal sticks). |
| `SeededDownloadsPin` | false | Whether the default ~/Downloads folder pin was seeded once (so removal sticks). |
| `PinnedPaths` | [] | Files/folders pinned to the right section (`PinnedPath`: path + per-folder SortBy/DisplayAs/ViewContentAs). |
| `KnownTaskbarPins` | null | Taskbar pins already seen/offered, so only new ones prompt. |
| `PinNames` | null | Friendly display names captured per pinned launch path. |

---

## Feature areas (detail)

### Taskbar mirror + dock-owned pins
- The dock shows **Start + taskbar apps (pinned + running) + minimized tiles**. App data from
  `Interop/TaskbarApps`; refreshed every 1 s (running state) plus a `FileSystemWatcher` on the
  pinned folder. Reconciled in place; `IsRunning` shows a dot (offset below the icon).
- **Running (unpinned) apps are ordered by open order, not Z-order.** `EnumerateAppWindows` returns
  windows in Z-order (changes on focus), so each app VM is stamped with a monotonic `SeenOrder` when
  first created (`DockViewModel._appSeq`), and the unpinned group is sorted by it — stable across
  focus changes; reopening an app gives it a higher `SeenOrder` (further right).
- **Pins are dock-owned** (`DockSettings.PinnedApps`), **seeded once** from the real taskbar order on
  first run, then owned by the dock. Drag a pin to reorder (`MovePin`); drag-and-hold-steady to remove
  (`UnpinApp`, see Live drag); drop an external Explorer file to pin (`PinApp`, via `OnDrop`);
  right-click → Unpin (`UnpinApp`). **The Windows taskbar is never modified** — Windows blocks
  programmatic taskbar pin/reorder (verb removed since Win10; Explorer owns the `Taskband\Favorites`
  blob), so we deliberately went dock-owned (user's choice).
- **Seeding reads pin order** from the `HKCU\…\Explorer\Taskband` → `Favorites` REG_BINARY:
  format is `[1 flag byte 0x00][DWORD pidl-size][pidl]` repeated; each PIDL resolved via
  `SHGetPathFromIDList` to its `.lnk`. Authoritative and includes pins under
  `User Pinned\ImplicitAppShortcuts\…` (e.g. Steam) that the `TaskBar` folder misses.
- **Matching running windows to pins** (`Interop/PinMatcher`) uses several strategies because no
  single one works: (1) exact exe path (Chrome/Brave); (2) window AUMID == pin AUMID — window AUMID
  via `SHGetPropertyStoreForWindow`+`PKEY_AppUserModel_ID`, pin AUMID via Shell.Application
  `System.AppUserModel.ID`; (3) window exe in a strict **subfolder** of the pin's app dir (excluding
  `%WINDIR%`) — Steam's `steamwebhelper.exe` under the Steam dir vs the `steam.exe` pin; (4) Explorer
  special-case — pin AUMID `Microsoft.Windows.Explorer` → match `explorer.exe` (Explorer windows
  report no AUMID and the pin has no exe target). Each window is claimed by the first matching pin.
- **Live drag** is a CUSTOM mouse-capture drag (NOT OS `DoDragDrop`, which is modal/can't animate):
  draggable items are taskbar apps + minimized tiles. `MaybeStartDrag` (movement) or a 500ms
  long-press (`OnDragSteadyElapsed`) → `StartDrag` → `BeginItemDrag` + `CaptureMouse`. While dragging,
  the **in-canvas tile is hidden** (`IsDragging` trigger sets Opacity 0) and a **free-roaming `DragGhost`
  popup** shows the icon following the cursor anywhere on screen (positioned via
  `HorizontalOffset/VerticalOffset` relative to `RootCanvas`); the engine still reserves a gap at
  `DragInsertIndex` so the others part. Magnification is suppressed during drag.
- **Hold-to-remove**: holding a dragged **pinned shortcut** steady for 500ms (`_dragSteadyTimer`,
  reset by motion past `SteadyEpsilon`) arms a red "Remove" tag in the ghost; releasing then unpins
  (`UnpinApp`). Only pinned shortcuts arm — the timer is gated by `IsRemovable`. On `OnDockMouseUp`:
  armed+pinned → `UnpinApp`; pinned dropped over the dock → `MovePin`; everything else (unpinned apps,
  minimized tiles, or dropped away) → no mutation → the tile **settles back** to its slot. `EndGhost`
  fades the ghost; `OnLostMouseCapture` cleans up an interrupted drag. External Explorer file drops
  still use OS `DragDrop` (`OnDragOver`/`OnDrop`).
- **Separator drag = resize** (`_separatorResize`): pressing a **separator** (cursor is `SizeNS` via a
  Grid style trigger) and dragging up/down changes `Settings.IconSize` (clamped to `[SizeMin,SizeMax]`
  = 12..64, same as the Dock Preferences slider). Uses `PointToScreen(...).Y` (screen px, immune to
  the bottom-anchored window moving up as it grows) → DIP via `GetDpi`; `RecomputeLayout` resizes
  live, `SettingsWindow.SyncSizeFromSettings()` keeps the open slider in sync, and `Save()` persists
  on release. Magnification + hover label are suppressed during the resize.
- **Drop a pinned shortcut on the Recycle Bin to remove it**: during the custom drag, hovering the
  Recycle Bin arms the "Remove" tag, and releasing there unpins (alongside the existing hold-steady
  hold-to-remove). Handled in `OnDockMouseUp` via `IsOverRecycleBin` (gated by `IsRemovable`).
- **Built-in "Dock Preferences" pseudo-app** (`DockItem.PreferencesLaunchPath` = `dockable://preferences`):
  a tile backed by the dock's own `SettingsWindow` rather than an external process. Seeded once as a
  pin to the right of the taskbar-seeded pins (`SeededPreferencesPin`), removable like any pin.
  `DockViewModel.UpdatePreferencesApp` injects it (pinned, or unpinned-while-open) into
  `RefreshTaskbarApps`; icon is `AppIcon.Preferences` (the bundled `Assets\settings.png`);
  `IsRunning` tracks `DockViewModel.PreferencesOpen` (set by the dock on open/close + a refresh).
  Click → `OpenDockPreferences` (open/focus) when closed; right-click → a dedicated `BuildPreferencesMenu`
  (Keep in Dock toggle + Quit, which closes *only* the Preferences window). The window is `Topmost`.
- **Preferences minimizes into the dock** like any window (thumbnail tile or its own icon per
  `MinimizeIntoIcon`). The global minimize hook skips the own process, so `OpenDockPreferences`
  installs an `HwndSource` hook on the window and intercepts `SC_MINIMIZE`: it captures the window
  while still visible, minimizes instantly (transitions suppressed), then calls the shared
  `MinimizeToDock(hwnd, capture)` (the refactored core of `OnWindowMinimizing`). Because the tile
  tracks the real HWND (`DockViewModel.PreferencesHwnd` → its `Windows`), `FindAppForWindow`,
  into-icon landing, and click-to-restore all reuse the standard machinery; `ActivateOrLaunch`
  only routes to `OpenDockPreferences` when the window isn't open (`Windows.Count == 0`).
- Rough edges: UWP/Store pins may not match (no exe target); elevated apps' exe path is unreadable
  unless Dockable is elevated; a minimized app shows both a running dot AND a genie tile.

### Docking behavior + multi-monitor/DPI
- **Default: always visible** — `ApplyBehavior` registers an AppBar (`AppBarManager`) to reserve a
  strip at the configured `Edge` and positions the dock there. `PositionDock` centers on the
  monitor; when the taskbar is hidden the dock anchors to the **full monitor bottom** (not the
  work-area bottom).
- **Auto-hide (`AutoHideDock`, off by default)** — the macOS-style hide/reveal returned (it was
  removed once; don't trust older notes): when on, the dock slides off-screen along its edge after
  ~600 ms of no interaction and slides back when the cursor presses a **2-physical-px sliver** at
  that screen edge. Mechanics: `HideProgress` DP (0→1) animated by `SlideDock`, applied as an
  offset inside `PositionDock`; `_autoHideTimer` (120 ms) is both the idle watcher (activity =
  hover, drags, flyouts, ANY menu via `Mouse.Captured`, in-flight warps) and the edge watcher
  (GetCursorPos-based, so it works mid-OLE-drag). **The AppBar strip stays unreserved the whole
  time auto-hide is on** — even while revealed (`ReserveAppBarSpace` unregisters and bails); the
  dock overlays maximized windows like macOS. Toggled from Preferences or the dock menu's "Turn
  Hiding On/Off" → both call `DockWindow.ApplyAutoHide()` (internal).
- **The window's main-axis size is PINNED to the full monitor edge** (a fixed strip;
  `DockLayoutEngine.SetFixedMainExtent`, fed by `PositionDock` from `Monitors.ForWindow` on every
  reposition — monitor/DPI/edge changes are the only resizes). History: the size used to track the
  max-magnified content and step (then glide) on tile add/remove — but ANY per-frame window
  resize+recenter desyncs the native rect from the layered bitmap for a frame (visible jitter, even
  eased). With the strip pinned, tiles growing in/out animate purely in canvas coordinates, which
  render atomically. Transparent strip pixels are click-through (per-pixel layered hit-testing), so
  the wide window doesn't eat input — but **the cursor hover test can't be "inside the window"
  anymore**: the engine publishes `DockViewModel.HoverExtentMain` (the max-magnified content extent,
  centered) and `OnRendering` tests that footprint. The drag-gap width `_gapExtent` still eases; the
  unpinned glide path survives only for the pre-first-`PositionDock` window. Related invariants:
  `Recompute()` must NEVER reset `CurrentScale`s or step with a forced no-hover — it runs mid-hover
  (departed tile finalizing, 1 s refresh), and doing so blinked the magnification off/on under the
  cursor (width jitter); its nominal step reuses the engine's last-seen cursor state
  (`_lastMouseMain`/`_lastHovering`). `Recompute()` returns true while anything still needs frames
  and `DockViewModel.RecomputeLayout` raises `AnimationRequested` (→ `HookRendering`) so appear/gap
  eases finish even with no hover/bounce/drag. At **startup**, `StartTaskbarMirror` calls
  `ViewModel.SnapWindowSize()` + `SyncAcrylic()` after the first population so the dock and the
  glass capture rect are full-size before first paint.
- **AppBar reserves exactly the resting (un-magnified) dock**, not the taller window that holds
  magnified/overflowing icons. `ReserveAppBarSpace` reserves from the docked edge to the bar's **far**
  edge (`WindowHeight - BarTop` for Bottom, i.e. including the bar's small margin from the screen edge —
  *not* just `BarHeight`), so a maximized window abuts the dock with **no gap and no overlap** ("avoid
  the dock only and always, no margin on top"). It's **re-reserved whenever the dock is resized**: live
  on the Preferences Size slider (via `ApplyWindowSize`, which runs on every `WindowWidth/Height`
  change) and on the **drop** of a separator drag (`EndSeparatorResize`; deferred during the drag so
  maximized windows don't reflow each frame). The window stays full-size (so magnification can render
  above the bar) but is **clipped via `SetWindowRgn` down to the resting bar while idle**
  (`ApplyIdleRegion`) so the overflow area is click-through to windows underneath. Hovering clears
  the clip (`ClearWindowRegion`, on `OnMouseEnter`); it's re-applied when the render loop settles.
- Per-monitor-v2 DPI aware. Positioning is exact on the **primary** monitor; secondary-monitor
  placement is approximate (a known TODO).

### Minimize / restore (Phase 3)
- Minimizing any normal window is replaced with a custom effect into a dock **thumbnail tile**
  (`DockItemKind.MinimizedWindow`, appended after a separator, not persisted). Clicking the tile
  reverses the effect and restores; a window minimized into the dock is also restored by clicking its
  app-group icon (`ActivateOrLaunch` handles iconic windows).
- **Two interception paths**:
  - **Pre-emptive (preferred, no flash) — `Interop/MinimizeInterceptHook`.** There's no pre-minimize OS
    event, so low-level hooks catch the *gesture* and paint frame 0 **before** the OS minimizes:
    `WH_MOUSE_LL` on the **minimize button** (`WM_NCHITTEST==HTMINBUTTON`, with a
    `DWMWA_CAPTION_BUTTON_BOUNDS` left-third fallback for custom title bars like File Explorer / Windows
    Terminal that report HTCAPTION/HTCLIENT) and `WH_KEYBOARD_LL` for **Win+Down** (minimizes even a
    maximized window — a single press, not the OS two-stage) and **Win+M**. The mouse hook **arms on
    button-down and acts on button-up, swallowing both** — swallowing only the up would starve a native
    caption button's modal press loop and leave the mouse captured (dock goes unresponsive). Releasing
    off the button cancels (drag-off). Hover/highlight still passes (only button events are swallowed).
    Custom title bars that report neither fall through to the reactive path. Callbacks come on the UI
    thread; the dock defers the real work via `Dispatcher.BeginInvoke` so the hook returns fast.
  - **Reactive fallback — `Interop/MinimizeHook`, `EVENT_SYSTEM_MINIMIZESTART`.** Fires *after* the OS
    minimized (taskbar/menu/programmatic minimizes), so it may show a brief OS animation before the warp.
- **Intercepted (`windowStillVisible`) flow** (`InterceptedMinimize`→`MinimizeOneAnimated`): ensure the
  target is foreground (so the capture isn't occluded — raise + `ForegroundSettleMs` wait if it wasn't),
  take a **fresh** `WindowCapture.Capture`, `ShowAtSource` (frame 0), wait for that overlay frame to
  actually render (`AfterRendered`, 2 `CompositionTarget.Rendering` ticks) **then** minimize the real
  window behind it via `MinimizeAndFocusNext` — `SW_MINIMIZE` alone often hands activation to the
  topmost dock, so we pick the next real app window first and `SetForegroundWindow` it (Win+Down's
  next-window focus, matching the OS). `MinimizeToDock`/`MinimizeOneAnimated` take `onDone` +
  `focusNext`; `MinimizeOneAnimated` also takes `raiseIfNeeded` (raise+settle a non-foreground window
  before capture, vs. capture it as-is).
- **Win+M = sequential, no focus changes** (`OnMinimizeAllRequested`→`MinimizeListSequential`): enumerate
  windows in **Z-order (top first)** once, then minimize each in turn (`raiseIfNeeded:false`,
  `focusNext:false`). Walking top-down means each window is already the top-most non-minimized one, so it
  captures cleanly without raising, and we deliberately **don't** focus-next — Win+M ends on the desktop,
  and a `SetForegroundWindow` from our (non-foreground) process would be rejected into a **taskbar-button
  flash** rather than a focus. (An earlier foreground-cascade that focused each next window caused exactly
  that flash.)
- **Concurrent minimizes** (rapid clicks, the cascade, etc.) share the single overlay; each animator's
  `FinishCurrent()` runs at the start of a new play to **finalize the in-flight one** (invoke its pending
  `onCompleted` so the previous window snaps to its tile and is freed from `_busy`) — otherwise the
  stomped animation's callback was lost and its tile stayed stuck in `_busy` (unresponsive).
- **External restore sync:** `MinimizeHook` also raises `WindowUnminimized` on `EVENT_SYSTEM_MINIMIZEEND`;
  when a tracked window is restored by the taskbar/Alt+Tab/the app itself, `OnWindowUnminimized` drops the
  now-stale tile/tracking (no reverse warp — transitions are suppressed so the OS restore is instant,
  which is what those gestures should look like). Guarded by `_busy` so our own click-to-restore is unaffected.
- **Adopt pre-existing minimized windows at startup** (`SyncPreMinimizedWindows`, after the first
  refresh): windows already minimized before launch get the per-setting representation — a tile, or
  (in `MinimizeIntoIcon` mode) owned by their app icon. They can't be captured, so the **app icon stands
  in** for the missing thumbnail. (Startup-only; windows that *start* minimized later aren't adopted.)
- **Restore-all on exit:** `RestoreAllMinimized` (first thing in `OnClosed`) un-minimizes every
  dock-minimized window (`RestoreNoForeground`) and re-enables the transitions we suppressed, so the
  user isn't left with windows stranded behind a gone dock. (A hard force-kill skips it.)
- **Three effects**, chosen by `DockSettings.MinimizeEffect` (`Suck`/`Scale`/`Genie`, set via the
  settings window's "Minimize windows using" combo). `DockWindow.MinimizeAnimator` maps them to two
  pre-warmed `IMinimizeAnimator`s: **Scale** → `ScaleAnimator` (capture scales down/translates to the
  tile); **Suck** and **Genie** → `GenieAnimator` (a 3D mesh warp) with its `Style` set to the
  hard-funnel (Suck) or bulging-neck (Genie) curve. `EffectSpeed` is applied as the animator's
  `SpeedMultiplier`. Both overlays are pre-warmed at startup. Optionally, `MinimizeIntoIcon` makes a
  window minimize into its app's dock icon instead of a separate thumbnail tile (falls back to a tile
  when the app has no dock icon).
- **Capture timing is the crux:** capturing at minimize is too late (window already minimized →
  black sliver). `Genie/WindowThumbnailCache` proactively keeps a recent full capture of each window
  **while it's visible** (`EVENT_SYSTEM_FOREGROUND` + ~1.2 s refresh; capture debounced ~180 ms so
  the window settles on top first, else occluders get grabbed). Capture is a `BitBlt(CAPTUREBLT)`
  screen-grab (`Genie/WindowCapture`) — NOT `PrintWindow`, which returns a black client area for
  composited apps. The grab uses the window's `DWMWA_EXTENDED_FRAME_BOUNDS` (not `GetWindowRect`) so
  the drop-shadow / invisible-border margin is excluded, and bakes an antialiased rounded-corner alpha
  mask (≈8 DIP, DPI-scaled) into a `Bgra32` bitmap so the warp follows the window's actual shape. Both
  animators render on transparent overlays (`AmbientLight` + `DiffuseMaterial` / `Image`), so the
  transparent corners composite through. The **restore** warp must size the bitmap to the *same*
  captured (extended-frame) rect — `DockWindow` stashes it in `_minimizedSourcePx` at minimize and
  reuses it instead of `GetWindowPlacement`'s `rcNormalPosition` (which is larger by the invisible
  border, and would make the window look enlarged just before it lands).
- **OS animation suppression:** the cache disables `DWMWA_TRANSITIONS_FORCEDISABLED` on the current
  foreground window (re-enabling the previous) so minimize is instant and the genie doesn't race the
  OS shrink. Side effect: the focused window's maximize/snap are also instant while focused.
- `Genie/GenieAnimator` is a **pre-warmed, reused** overlay (one click-through, topmost,
  transparent WPF window resized to the active monitor per play; shown via Visibility) — building a
  fresh WPF+3D window per minimize cost 10s–100s ms and was the visible "blink". It maps the capture
  onto an animated `MeshGeometry3D` grid (Viewport3D + orthographic camera) warping into the dock;
  `reverse:true` for restore. Only one genie at a time (shared overlay).
- **The genie's neck width is the SETTLED tile width** (`TileWidthOf`): a just-added tile is still
  growing its slot in (AppearScale ≈ 0), so its live RenderWidth reads ~2 DIP at play time — trust
  it and the warp necks to a dot. TileWidthOf divides RenderWidth by AppearScale mid-grow to
  project the final width (Suck still pinches to a point by design).
- Orchestration in `DockWindow` (`InterceptedMinimize`/`MinimizeOneAnimated`/`MinimizeToDock` for
  minimize; `OnWindowMinimizing` reactive fallback; `RestoreMinimized`/`RestoreWindowAnimated` for
  restore); `_busy` set guards re-entrancy. Rough edges: minimizes that fall through to the reactive
  path (taskbar/menu/programmatic, or custom title bars the button-hit-test misses) can still flash the
  OS animation briefly; DRM/protected windows capture black; pre-minimized/elevated windows have no
  thumbnail (app icon stands in); stale tiles if an app closes while minimized (needs a destroy hook);
  multi-monitor genie target approximate.

### macOS-style menu bar (top AppBar) — on by default (opt-out)
- Enabled via `DockSettings.ShowMenuBar` (Dock Preferences toggle or tray "Show menu bar"). **App owns
  the window's lifetime** (`App.SetMenuBarVisible`): the dock's `SetShowMenuBar` persists the setting and
  calls into `App`; the menu bar is created on first show and `Close()`d (which `Unregister()`s its AppBar)
  when toggled off. `ShutdownMode=OnExplicitShutdown`, so adding/closing this window is safe.
- `MenuBarWindow` is a sibling of `DockWindow` but **much simpler**: a flat full-width bar flush to the
  top of the **primary monitor** (window rect == bar rect, so **no `SetWindowRgn` clipping** and no
  magnification). Its own `AppBarManager(_hwnd, WM_USER+2)` reserves the top strip (`MenuBarHeight`, 28 DIP)
  via `ReserveEdge(DockEdge.Top, …)`; `WndProc` handles `ABN_POSCHANGED` (re-reserve) + `WM_SETTINGCHANGE`
  (re-theme when System). **Always acrylic**: reuses `AcrylicBackdrop` (corner radius 0), always shown
  (independent of the dock's Glass Effect setting). **No border.** `ApplyTheme()` paints the bar with the
  dock's own bar colours at **50% transparency** (light `#80FFFFFF`, dark `#80242424`) over the blur, and
  swaps `MenuTextBrush` to contrast per the Appearance theme (dark `#F2F2F2` / light `#1D1D1F`).
- Content: **leading** = a Windows 11 logo (same geometry as the dock's Start tile, tinted with
  `MenuTextBrush`; click → an Apple-menu-style command `ContextMenu` built fresh each open:
  About This PC (`ms-settings:about`) / System Settings (`ms-settings:`) / Microsoft Store / **Recent
  Apps** submenu (open apps grouped by exe/AUMID; pick one → `WindowControl.ActivateAll` raises all its
  windows) / Force Quit \<focused app\> (`Process.Kill`) / Sleep / Restart… / Shut Down… / Lock Screen /
  Log Out \<user\>… — power/session items via `Interop/SystemActions`; the "…" ones confirm via
  `ConfirmDialog(showDoNotAskAgain:false)`) then the focused app's **friendly display name** — e.g.
  "Google Chrome" (not "chrome", not the window title), resolved by
  `DockViewModel.AppDisplayNameForWindow` (exposed as `MenuBarViewModel.AppDisplayName`) — **the same
  funnel that names the dock tiles**, so the bar and dock never disagree (a separate `Shell/ForegroundApp`
  resolver used to exist; it drifted — "Windows Terminal Host", raw "SnippingTool.exe" — and was deleted).
  Tile-first: a window represented by a dock tile returns the tile's label (which benefits from the
  identity cache's AUMID retries and remembered pin names); unrepresented windows derive the way tiles
  do: packaged AUMID → `shell:AppsFolder` name, else remembered pin name →
  `FileVersionInfo.FileDescription` → extension-less stem (never a raw "Foo.exe") → window title.
  The Recent Apps submenu and the startup seed use the same funnel. The bar tracks the last real app (`_appHwnd`, via `Interop/TitleWatcher`,
  skipping our own process — EXCEPT the Dock Preferences window, which is represented like any app
  under its dock-tile name `Window_DockPreferences`, with no mirrored menus: it's WPF/no HMENU, and
  UIA-scanning one's own process is deadlock-prone. The dock/menu-bar windows themselves stay
  skipped); at startup, when launching the dock made US foreground, it seeds from the top-most
  non-minimized app window in Z-order (`SeedFromTopmostAppWindow`) so the name + app menus show
  immediately instead of waiting for the first focus change; a represented window that dies (e.g.
  Preferences closed, focus fell to the dock) is dropped and re-seeded the same way. **Click the name** → the focused window's title-bar menu, reproduced by
  posting the non-client right-click messages (`WM_NCRBUTTONDOWN`/`WM_NCRBUTTONUP` with `HTCAPTION`) to
  the target so its **own** `DefWindowProc` shows the menu in its process (a cross-process
  `GetSystemMenu`+`TrackPopupMenu` doesn't work — the menu is owned by the other process; this also
  honours custom title-bar menus like Chrome's). `SetForegroundWindow(target)` first so it tracks/dismisses
  correctly. **Trailing** cluster (a clock
  `DispatcherTimer`, 1 s): a **tray-overflow chevron** (`TrayOverflow.Open` — synthesizes Win+B then Enter
  to open the "show hidden icons" flyout; reveals the auto-hidden taskbar), **Quick Settings** (`QuickSettings.Open` → Win+A),
  **Notifications** (`Notifications.Open` → Win+N), a clickable **keyboard layout** (`KeyboardLayouts`: shows the foreground
  thread's layout; click → a code-built `ContextMenu` of installed layouts → `Switch` posts
  `WM_INPUTLANGCHANGEREQUEST` to the foreground), and the **clock** (culture-aware, follows `Loc`).
- **Global app menus (two tiers):** after the app name, the bar mirrors the focused window's in-window
  menu ("File", "Edit", …) as clickable labels (`MenuBarViewModel.MenuEntries`, refreshed on foreground
  hwnd change with a `_menuGen` stale-guard). **Tier 1 (Win32/HMENU** — Notepad++, 7-Zip, most classic
  apps): `Interop/Win32AppMenu` reads the bar cross-process (`GetMenu`/`GetMenuString` — menus are shared
  USER objects, no injection) and a click hosts the app's REAL dropdown under the label:
  `WM_INITMENUPOPUP` is sent first (timeout-guarded, so lazily-populated menus are live), then
  `TrackPopupMenuEx(TPM_RETURNCMD)` tracks the foreign submenu from our window (foreground handoff +
  `WM_NULL` after, tray-menu style) and the picked id is posted back as `WM_COMMAND`. `MNS_NOTIFYBYPOS`
  menus track without `TPM_RETURNCMD` and relay `WM_MENUCOMMAND` instead; while a foreign popup is up,
  `MenuBarWindow.WndProc` relays nested-submenu `WM_INITMENUPOPUP`/`WM_UNINITMENUPOPUP` to the target
  (`Win32AppMenu.ForwardMenuMessage`). **Tier 2 (UIA fallback** — WPF/Electron/VS Code/Qt, no HMENU):
  `Interop/UiaAppMenu` finds a `MenuBar` control in the window's UIA tree (background thread; cached per
  HWND including "has none"; the non-client "System Menu Bar" — a lone "System" item, parented in the
  UIA TitleBar — is skipped since clicking the app's display name already opens that menu) and renders
  the same labels, but a click can only Expand/Invoke the app's
  OWN menu at its own location — UIA popups can't be re-anchored under our bar. No menu found →
  nothing rendered (Chrome/Edge/Office/UWP command-bar apps). Known limits: owner-drawn Win32 items
  render blank in a hosted popup (WM_DRAWITEM can't cross processes); elevated apps are UIPI-blocked;
  very long menus can overlap the trailing status cluster on narrow screens.
- **Right-click on empty bar space** shows the same dock-wide menu as the dock's empty space
  (Task Manager / Preferences / About / Quit) — `Bar_RightClick`, reaching the dock via
  `Application.Current.Windows.OfType<DockWindow>()` (`OpenDockPreferences` is internal for this).
- **Active-item pill highlight:** every interactive menu-bar item sits in a "pill" `Border` (fixed
  22px height, CornerRadius 11 = fully round; 8px padding offset by negative margins so the layout
  matches the padless positions exactly). `MenuHighlightBrush` (swapped in `ApplyTheme`: light
  `#17000000` almost-transparent black, dark `#26FFFFFF` almost-transparent white) is painted while
  an item is active: held open for menus we control (logo menu, keyboard layouts —
  `HighlightWhileOpen` clears on `ContextMenu.Closed`; Tier-1 app menus stay lit through the modal
  `TrackPopupMenuEx`), a ~350 ms `FlashPill` for actions whose flyout can't be tracked (OS flyouts,
  the cross-process title-bar menu, UIA menus). The Windows-logo pill is `StartPill` INSIDE the
  full-height `StartButton` hit area (edge-to-edge click target from the previous change).
- **Full-screen hide:** like the dock, the menu bar hides itself + its backdrop while a full-screen or
  borderless-fullscreen app (game/video) owns its monitor (`Interop/Fullscreen` test, re-checked on
  foreground change, the 1 s clock tick, and `ABN_FULLSCREENAPP`); it reappears when that window goes away.
  Two things make this robust (both windows): (1) while hidden, the **reserved AppBar strip is released**
  (`_appBar.Unregister()`, re-reserved on restore) — otherwise the game resizes to the work area to avoid
  our strip, stops covering the monitor, and the detection flip-flops; (2) `UpdateFullscreenState` ignores
  the case where **our own process is foreground** (`Fullscreen.IsForegroundOwnProcess`), so clicking the
  bar/dock over a game doesn't un-hide it.
- **Why there's no live system-tray icon replication:** a read-only spike on **Windows 11 25H2 (build
  26200)** found the classic notification-area path **gone** — `SysPager`/`ToolbarWindow32`/
  `NotifyIconOverflowWindow` don't exist, and the icons live in `explorer.exe` XAML islands that expose
  **0 invokable buttons** to UI Automation under `Shell_TrayWnd`. So cross-process `ToolbarWindow32` reads
  (and a UIA fallback) both yield nothing on current Windows. Per user decision, the tray area is instead
  the reliable, update-proof **Quick Settings + Notifications** flyout shortcuts above. (Probe scripts were
  one-off; not kept in the repo.) Caveat: the OS anchors those flyouts to the bottom-right tray — they
  can't be repositioned under the menu-bar icons.

### Pinned files & folders (macOS right-section stacks) — Grid/List TODO
- **Files and folders pin to the dock's right section** (after the apps separator; the section
  orders minimized thumbnails FIRST, then the pinned files/folders, then the Recycle Bin).
  They can't live among the app shortcuts. Persisted as `DockSettings.PinnedPaths`
  (`Models/PinnedPath`: path + per-folder `SortBy`/`DisplayAs`/`ViewContentAs` enums, macOS defaults
  Date Added / Stack / Automatic); kinds `DockItemKind.PinnedFolder` / `PinnedFile`
  (folder-ness = `Directory.Exists` at VM creation).
- **Pinning**: an Explorer drop routes by type (`DockWindow.IsAppLike`): directories + documents →
  `PinPath` (inserted at the previewed right-section slot); launchable extensions
  (.exe/.com/.bat/.cmd/.scr/.msi/.appref-ms/.url) → the existing `PinApp` app-pin path. The
  **drop-preview gap is section-aware**: `ExternalDragTargetsPathSection` classifies the payload
  once per drag (cached — DragOver fires continuously; any app-like item → left/apps gap, else
  right/paths gap), threaded through `UpdateExternalDrop(main, pathSection)` to the engine, and
  `ComputeDropPathIndex` makes the drop land exactly where the gap showed. Remove via context menu →
  `UnpinPath` (tile shrinks out through the shared `_departing` machinery). **Path tiles are full
  drag candidates**: reorder within the right section (the engine's drag gap goes path-aware —
  `PathsBeforeCursor`/`PathGapSlot`, `DragInsertIndex` then means an index into `PinnedPaths` and
  `MovePinnedPath` commits), hold-steady "Remove" + drop-on-Recycle-Bin unpin (removes the PIN, not
  the file). **Fan/grid rows drag out as real files** (`AttachFlyoutDrag`: OS `DoDragDrop` with
  FileDrop): dropping on the dock pins to the right section as-is (`_flyoutDragPath` flags the
  in-process drag so OnDrop skips the app-vs-path routing), Explorer copies, the dock's bin
  recycles; tail cells don't drag; the flyout retracts automatically (the OS drag steals the
  light-dismiss capture). **Flyouts follow their tile** (`_fanAnchorFollow`: PropertyChanged on the
  tile's X/RenderWidth re-anchors `FanPopup.HorizontalOffset` as magnification drifts icons); the
  grid balloon has a **bottom-center callout triangle** in its tint aiming at the tile. GOTCHA:
  `OnMouseMove` must `ClearWindowRegion()` (flag-guarded) — while a flyout holds capture, the dock
  never gets MouseEnter, so the idle clip couldn't be cleared and magnified icons rendered cut off
  at the bar top.
- **Click opens via shell** (`DockItemViewModel.Activate`): folders in File Explorer, files with
  their default app.
- **Context menus** (`BuildFolderMenu`/`BuildFileMenu`): folders get macOS's full menu — gray
  section headers (disabled `MenuItem`s) with single-select check-marked choices for **Sort by**
  (Name/Date Added/Date Modified/Date Created/Kind), **Display as** (Folder/Stack), **View content
  as** (Fan/Grid/List/Automatic), then Options ▶ (Remove from Dock / **"Show in File Explorer"** —
  user-specified wording, key `Menu_ShowInFileExplorer`, distinct from the app menu's
  `Menu_ShowInExplorer`) and `Open "Name"` (`Menu_OpenNamed`, a `string.Format` template). Files get
  just Options ▶ + Open.
- **Stack tile** (`Display as: Stack`, the default): `Shell/StackIcon.RenderAsync` composes the
  folder's top-10 items' icons into one `RenderTargetBitmap` — a bottom-anchored cascade, top-of-sort
  item in front, each behind it peeking ~6px (of 256) higher (front icon ≈80% of the tile, near
  dock-icon size). Ordering comes from
  `Shell/FolderContents.GetSorted` (files+subfolders, hidden/system skipped): Name = culture-aware
  alphabetical on the display name; **Date Added ≈ creation time** (NTFS has no true macOS
  "date added"; copies re-stamp creation, same-volume moves don't) newest-first; Date Modified /
  Created newest-first; **Kind** = the shell's friendly type name (`SHGetFileInfo` SHGFI_TYPENAME,
  CsWin32) alphabetically, then name within each kind. The stack recomposes on Sort by / Display as
  menu picks and when the folder's **mtime** drifts (cheap probe on the 1 s refresh — catches direct
  child add/remove/rename, NOT a child's content edit, so a Date Modified stack can go stale).
  `.svg`/`.svgz` items render their actual artwork via `Shell/SvgIcon` (SharpVectors) instead of the
  shell's generic icon.
- **Fan** (`View content as: Fan`): clicking the folder opens `FanPopup` (its own hwnd — immune to
  the dock's idle `SetWindowRgn` clip; `StaysOpen=False` dismisses on outside click) instead of
  Explorer; built/animated in code (`OpenFolderFan`). Slot 0 = bottom = top of the stack; entries
  rise from the tile bottom-up with an 18 ms stagger + subtle rightward arc, **always fading in as
  they start** (the 160 ms fade finishes while the 280 ms rise still travels). Each row (icon + label together) is
  **progressively rotated** — 0° at the bottom up to the arc's own tangent
  (`atan(2·FanArcPerSlot·k / FanSpacing)`) at the top, pivoting on the icon center; the rise
  translation applies after the rotation so entries travel straight up. Labels sit to the **LEFT of
  the icons** — rows are right-anchored on the icon column (all rows measured up front; the widest
  label sets the canvas extent + popup offset so icons stay on the arc), rotation pivots on the icon
  at the row's right end, and `FanTopPad` gives headroom for rotated labels swinging up. Labels show
  the **real file name with extension** (folders have none) in a **fully-rounded pill** (fixed
  `FanPillHeight` 26, radius = half) whose background is the theme's `LabelBgBrush` colour at ~80%
  alpha (resolved per open, so theme switches stick). Tail entry = a semi-transparent
  theme-following disc (`LabelBgBrush` @ ~80% fill, `LabelBorderBrush` ring, `LabelTextBrush` ↗) +
  "N more in File Explorer" (`Fan_MoreInExplorer`; `Fan_OpenInExplorer` when everything fit),
  which opens the folder. Click an entry → shell-launch + retract. **Dismissal is animated**
  (`BeginCloseFan`): the opening mirrored — entries sink back into the tile top-rows-first
  (ease-in, from their CURRENT offset so an interrupted opening reverses mid-flight) and **always
  fade out at the end**, vanishing as they land; the popup only really closes (`IsOpen=false`) on a
  timer after the longest retraction. To make that possible light-dismiss is **manual**: `StaysOpen=True` + a
  `CaptureMode.SubTree` mouse capture on `FanCanvas` (taken at Input priority after open); any
  outside mouse-down hits `OnFanOutsideClick` → retract, and a stolen capture
  (`OnFanLostCapture` — dock drag, other popup, Alt-Tab) retracts too. `BeginCloseFan` releases the
  capture immediately (retraction is purely visual); `_fanClosing` guards re-entry and is reset in
  `OnFanClosed`. Side effect of the capture: dock hover/magnification doesn't react while a fan is
  open (accepted).
- **Grid** (`View content as: Grid`): same flyout popup/dismissal machinery (`_fanIsGrid` flags the
  mode) but the content is a **balloon** — `Border` CornerRadius 24, theme-tinted translucent bg,
  holding a `ScrollViewer`+`WrapPanel` of **ALL** items (not top-10) in sort order, 94px icons with
  the name (extension incl., ≤2 lines + ellipsis) beneath, plus an "Open in File Explorer" tail cell
  (transparent circle, 2px `LabelTextBrush` ring, ↗). Columns = `ceil(sqrt(n))` capped at 8 (5 files
  + tail → 3×2); ≥7 rows scroll (6 visible max, also capped to the space above the dock). Opens by
  **scaling out of the folder tile** (ScaleTransform 0.15→1, origin bottom-center + quick fade) and
  closes with the mirror shrink. NOTE: `ShortcutService.ConvertHBitmap` reads pixels via
  **GetDIBits with an explicit top-down target** — never infer row order from the source DIB's
  biHeight sign (some shell paths mislabel it; PNG/ICO thumbnails + folder icons came out upside
  down). The scrollbar drag steals the subtree capture — `OnFanLostCapture`
  ignores capture moving to a `FanCanvas` descendant and `OnFanPreviewMouseUp` retakes it on
  release (otherwise dragging the scrollbar would dismiss the balloon).
- **Automatic** (`View content as`, the default): fan for ≤9 items, grid for 10+ —
  `OpenFolderFlyout` enumerates once then dispatches to `ShowFolderFan`/`ShowFolderGrid`. **List**
  (`OpenFolderListMenu`): the folder opens as a plain `ContextMenu` above the tile — one row per
  item (20px icon + full name; a `TextBlock` header so "_" stays literal instead of becoming an
  access key), then Separator / Options ▶ (the shared `AddFolderConfigSections`: Sort by,
  Display as, View content as — same sections as the right-click menu, minus its Options + Open) /
  Open in File Explorer. WPF auto-scrolls menus taller than the screen. The user's **Downloads folder is seeded as a pin on first run**
  with Sort by **Date Added** + View content as **Fan** (Display as keeps the Stack default);
  `SeededDownloadsPin` flag makes removal stick; resolved via `Interop/KnownFolders` →
  `SHGetKnownFolderPath(FOLDERID_Downloads)` since Downloads can be relocated. While the fan is open the folder's
  tile swaps to the **open-stack indicator** (a cached rendered bitmap: semi-transparent rounded
  square + dark downward chevron, `FanOpenTileIcon`), with the real icon stashed/restored on close
  (`_fanPrevIcon` — a stack recompose landing mid-fan would be stomped by the restore; rare,
  accepted). The toggle-click guard matters:
  StaysOpen=False closes the popup on the tile click's mouse-DOWN, so the mouse-UP checks
  `_fanLastClosed`/400 ms to not instantly reopen. Grid / List / Automatic still open Explorer —
  **user will direct those next**; fan math is Bottom-edge-only (like hover labels).

### Taskbar visibility + restore safety
- **Three states** (`DockSettings.TaskbarVisibility`, default **Never**), set from Dock Preferences →
  Taskbar (a combo: Always / Auto / Never) or the tray "Windows taskbar" submenu, applied by
  `DockWindow.SetTaskbarVisibility` → `Interop/Taskbar.SetVisibility`:
  - **Always** — `SW_SHOW` the tray windows + `ABM_SETSTATE, ABS_ALWAYSONTOP` (auto-hide off, visible).
  - **Auto** — `SW_SHOW` + `ABM_SETSTATE, ABS_AUTOHIDE`: the OS slides it away and reveals on edge hover
    (no custom timer).
  - **Never** — `ABS_AUTOHIDE` first, then `SW_HIDE` the tray windows (+ a 750 ms delayed re-hide:
    Explorer applies ABM_SETSTATE asynchronously and re-shows the tray while doing so, stomping the
    first SW_HIDE). It MUST be auto-hide, not always-on-top: an always-on-top taskbar keeps its
    work-area reservation even while SW_HIDDEN, so the shell stacked the dock's AppBar strip on a
    ghost taskbar-height strip and maximized windows floated ~48 px above the dock (measured; looked
    like "reserving for the magnified dock").
- **Restore on exit/crash/kill**: `Taskbar.CaptureOriginalState()` records the pre-launch auto-hide
  state; `Restore()` (clean exit via `DockWindow.OnClosed` + `App.OnExit`, and managed crash via
  `AppDomain.UnhandledException`) puts it back. **Hard kills** (Task Manager, `taskkill /F`,
  `Stop-Process`) skip all in-process handlers, so `App.OnStartup` also spawns the out-of-process
  `Interop/TaskbarWatchdog` (hidden `powershell.exe`, handed the captured state): it waits on the
  dock's PID and re-asserts that state (SW_SHOW all tray windows + ABM_SETSTATE) when the dock dies
  for ANY reason, then exits by itself. So even **Never** now survives a force-kill. The watchdog's
  restore after a clean exit is an idempotent no-op, and it skips the restore entirely if a new dock
  instance is already running by the time it wakes (quick-restart race, 750 ms grace).
- Note the **conflict to watch**: the dock also lives at the bottom, so revealing the native taskbar
  pops it up over/under the dock at the same edge. Accepted per user request (taskbar on demand).

---

## Known decisions

- **All context menus are Win11-styled via `Themes/ModernMenu.xaml`** (merged in App.xaml —
  implicit ContextMenu/MenuItem/Separator styles restyle every code-built menu app-wide: dock,
  tray, menu bar, folder List view). Rounded 8px surface with an outer Margin for the drop shadow
  (needs the popup's transparency — `HasDropShadow=True`), one MenuItem template for both
  ContextMenu roles (check glyph and the Icon share the leading column; chevron + popup only for
  SubmenuHeader), scroll arrows via the `MenuScrollViewer` ComponentResourceKey. The `PopupMenu*`
  brushes are swapped at **Application scope** from `DockWindow.ApplyTheme` (near-opaque tint —
  real acrylic isn't possible on WPF popups; a deliberate approximation). The two cross-process
  menus (Tier-1 hosted HMENU app menus, the title-bar system menu) can't be styled and stay native.
- **The dock's empty-space / separator menu** (`BuildSeparatorMenu`) is macOS's Dock menu: Task
  Manager | sep | Turn Hiding On/Off (`AutoHideDock`), Turn Magnification On/Off (re-enabling bumps
  a stale `MaxIconSize` to 2× so "on" is never a no-op), Position on Screen ▶ Left/Bottom/Right
  (checkmarked), Minimize Using ▶ Genie/Suck/Scale (checkmarked) | sep | Preferences, About | sep |
  Quit. Picks sync an open Preferences window via `SettingsWindow.SyncFromSettings()`. The menu
  bar's empty-space right-click shows the four-item variant (Task Manager/Prefs/About/Quit).

- **Bar styling follows a Light/Dark theme** (`DockSettings.Theme` = `System`/`Light`/`Dark`,
  default System; **"System" is shown to the user as "Auto"**). Two pickers: the tray "Theme"
  submenu (Light/Dark/Auto) and the settings window's **Appearance** section (Light/Dark/Auto
  illustration tiles; the Auto tile is split half-light/half-dark). The settings window applies via
  the `Action<DockTheme>` callback (`DockWindow.SetTheme`) passed into it. The bar (`DockBackground`) and its
  theme-dependent elements bind to swappable `DynamicResource` brushes
  (`BarBackgroundBrush`/`BarBorderBrush`/`SeparatorBrush`/`RunningDotBrush`/`FallbackBgBrush`/
  `FallbackTextBrush`) plus the named `BarShadow` effect; `DockWindow.ApplyTheme()` sets them.
  `CornerRadius=24` both themes.
  - **Light** (`.macos-dock-light`): bg `#66FFFFFF`, border `#33FFFFFF`, shadow opacity `0.15`;
    dark dependents (separator `#33000000`, running-dot `#B3000000` = rgba(0,0,0,0.7),
    fallback `#1F000000`/`#CC000000`).
  - **Dark** (`.macos-dock-dark`): bg `#66242424`, border `#14FFFFFF`, shadow opacity `0.4`;
    light dependents (separator `#40FFFFFF`, running-dot `#CCFFFFFF` = rgba(255,255,255,0.8),
    fallback `#33FFFFFF`/white).
  - Running-dot (`.app-indicator`) is a 4px circle. It does NOT hop with the launch/attention
    bounce — the bounce is an icon-only RenderTransform (`BounceX/Y`), the container stays put.
  - Separator lines stop **`DockLayoutEngine.SeparatorEndInset` (9 DIP) short of each bar edge**,
    computed in the engine (orientation-agnostic — top/bottom on a horizontal dock, left/right on a
    vertical one). No XAML margins; tune the one constant.
  - **System** mode reads `Interop/SystemTheme.IsLight()` (registry `AppsUseLightTheme`) and
    re-applies on OS theme change via `WM_SETTINGCHANGE`/`ImmersiveColorSet` in `WndProc`.
  - The **settings window stays light** regardless (it models the light macOS Settings screenshot).
- **Icons cast a dual drop-shadow** (`filter: drop-shadow(0 4 8 /.18) drop-shadow(0 10 20 /.12)`).
  WPF allows one effect per element, so the icon `Image` is wrapped in a `Grid`: the outer Grid
  carries the wide soft shadow, the inner `Image` the tighter one. Shadow params are fixed DIP (not
  scaled with magnification). Watch perf: two GPU effects per icon, re-rendered each magnify frame.
- **Glass / backdrop blur is now implemented** as a **separate non-layered backdrop window**
  (`Interop/AcrylicBackdrop`), z-ordered just below the dock's layered icon window and clipped to the
  bar's rounded rect — this is the way around the known conflict (DWM accent-blur fills the whole
  window rect and ignores `SetWindowRgn`, which we rely on for the idle clip; the modern DWM
  system-backdrop needs a non-layered window). `DockSettings.GlassEffect` selects **Simple**
  (translucent, no backdrop window — lightest), **Acrylic** (Composition host-backdrop blur), or
  **LiquidGlass** (a runtime pixel shader — `Genie/RefractionEffect` — that does **rim refraction**
  (`DistortionAmount`, via a displacement map) plus a **frosted-glass blur** (`BlurRadius`, a 3×3
  weighted tap kernel stepped by real device pixels via WPF's `DdxUvDdyUvRegisterIndex`) over the
  captured backdrop; HLSL compiled at runtime by `Interop/ShaderCompiler`; falls back to Acrylic where
  unsupported).
- **Liquid Glass hides the dock from screen capture** (`SetCaptureExclusion` →
  `WDA_EXCLUDEFROMCAPTURE`, set while refraction is on) so the `BackdropCapturer`'s screen BitBlt
  doesn't refract the dock itself. That affinity is global to ALL capture APIs (Snipping Tool too),
  so a **capture-friendly mode** lifts it while the user is capturing: entered on Win+Shift+S /
  PrintScreen (observe-only watch in `MinimizeInterceptHook.ScreenSnipRequested` — must fire BEFORE
  the snip overlay grabs the screen, hence Send priority) or a snipping app (SnippingTool /
  ScreenClippingHost / ScreenSketch) coming foreground; exits via the 1 s tick once no visible,
  non-cloaked snipping-app window remains (the packaged Snipping Tool lingers SUSPENDED with cloaked
  windows — a process-exists test would never exit) + a 3 s minimum hold. While in the mode,
  `SetCaptureExclusion(true)` requests are downgraded (so ApplyGlassEffect re-runs can't re-hide),
  and the fullscreen hide ignores the snip overlay (it covers the whole monitor and would otherwise
  hide the dock with `HideOnFullscreen` on). The capturer itself (`EnterCaptureFriendly`) PROBES
  whether a plain SRCCOPY blit (no CAPTUREBLT) omits layered windows on this Windows build (three
  back-to-back grabs: CAPTUREBLT/SRCCOPY/CAPTUREBLT; the repeat detects a mid-probe backdrop change):
  if yes → glass keeps updating LIVE with SRCCOPY (the layered dock stays out of its own capture);
  if no/inconclusive → it freezes on the last uploaded frame until exit. Freeze is the safe verdict —
  a wrong "live" would feed the glass its own rendering (runaway feedback).
- **Internationalization is in-code** (no `.resx`/satellite assemblies): `Localization/LocData`
  holds per-language string tables, `Loc` is the runtime service, and the `{loc:Loc Key=…}` markup
  extension binds XAML text to `Loc.Instance[Key]` so a language change updates **live**. Code-built
  UI uses `Loc.T(key)`; menus that are built once (the tray menu) rebuild on `Loc.LanguageChanged`.
  Add a new string by adding the key to **every** table in `LocData` (English is the fallback).
  Brand/tech names ("Dockable", C#, WPF, CsWin32, …) and the author's name stay untranslated.
- **Perf invariants from the 2026-07 optimization pass** — keep these when touching the hot paths:
  - **The render loop is allocation-free per frame.** `DockLayoutEngine.Update` refills a reusable
    `_placed` scratch list (it never escapes the call — keep it that way); `UpdateGlassShape`
    early-returns when both refraction effects are null and assigns the two fields directly (no temp
    array). Don't reintroduce per-frame `new`.
  - **DPI is cached** (`DockWindow._dpi` via the `Dpi` accessor, refreshed by `OnDpiChanged`) for the
    per-frame SyncAcrylic/PublishGlassRect/UpdateGlassClip path, and SyncAcrylic threads its
    already-projected bar top-left into `UpdateGlassClip(Point?)`. PublishGlassRect projects a
    DIFFERENT point — don't "unify" it. Non-per-frame code may keep calling
    `VisualTreeHelper.GetDpi` directly.
  - **TaskbarApps.EnumerateAppWindows must stay cheap** — it runs every ~1 s plus on demand. Window
    identity (exe/AUMID) comes from `IdentityCache`; anything newly per-window-per-tick needs the
    same treatment. The dock's UIA/screen-reader names, minimize bookkeeping helpers
    (`IsWindowRepresented`/`DropMinimizedTracking`), and `RestoreQueueNext` are the single owners of
    their invariants — extend them rather than re-inlining copies.
  - **OverlayAnimatorBase parity traps** (if you touch the minimize animators): `_playSeq` bumps in
    Play/ShowAtSource/AnimateTo only, after the first ApplyFrame and before showing; FinishCurrent
    runs in Play + ShowAtSource, never AnimateTo; the frame cap's `progress < 1.0` clause is the
    never-skip-the-final-frame guarantee; CompleteRestoreHold invokes `done` first and checks
    `_playSeq` twice; MonitorHeight is set only by Play/ShowAtSource.

## Status & roadmap

Phases 1–3 + polish implemented:
- Dock shell, Start tile, shortcuts; fisheye **magnification** (Size + Magnification configurable);
  per-monitor DPI.
- **Taskbar mirror**: dock-owned pins (seeded once, multi-strategy window↔pin matching), running dot;
  **running (unpinned) apps ordered by first-seen/open order** (stable across focus changes).
- **Custom live drag**: reorder pins; drag any icon out (free-roaming ghost popup); **hold-to-remove**
  a pinned shortcut (500ms steady → "Remove"); unpinned/minimized snap back.
- **Minimize/restore** into dock tiles, three effects (`Suck`/`Scale`/`Genie`) via `MinimizeEffect`,
  with an `EffectSpeed` multiplier and an optional "minimize into the app's dock icon" mode.
  **Pre-emptively intercepts** the minimize gesture (min-button click, Win+Down, Win+M) via low-level
  hooks so the warp's frame 0 paints before the OS minimizes (no flash); **Win+M minimizes all
  sequentially**; external (taskbar/Alt+Tab) restores clear the stale tile; pre-existing minimized
  windows are adopted on launch; all dock-minimized windows are restored on exit.
- **Recycle Bin** far-right with a state-aware empty/full icon; **dropping files/folders on it sends
  them to the Recycle Bin** (`RecycleBin.SendToRecycleBin` → `SHFileOperation` FO_DELETE + FOF_ALLOWUNDO;
  `OnDrop`/`OnDragOver` route by `IsOverRecycleBin(x)`, else pin). Group-separators between sections.
- **macOS-style bar styling** with **Light/Dark/Auto theme** (follows OS in Auto); **icon drop-shadows**;
  **hover labels** (per-icon, in an `IsHitTestVisible=False` Canvas, fading in/out on `IsMouseOver`);
  **UI sound effects**.
- **Glass Effect** bar background: Simple / Acrylic / LiquidGlass (separate backdrop window + runtime shader).
- **Internationalization**: all UI localized (en / pt-BR / es / uk / zh-Hans), live language switch
  from Dock Preferences → System → Language; first run follows the OS language.
- **About Dockable** window (separator menu + tray): version, stack, Apple-Dock inspiration, author.
- **Dock Preferences** window (separator right-click or tray): System (Language + Open-at-login),
  Appearance tiles, Dock (Size/Magnification, Position, Glass Effect, Minimize effect + Speed,
  toggles), Taskbar — wired live (Position only implements the Bottom edge).
- **Docking**: always-visible AppBar that reserves only the resting bar; the window is clipped to the
  bar when idle so magnification can bleed over. **Native taskbar auto-hide** (self-restoring).
- **Pinned files & folders** (macOS right section): drop to pin, dock-owned order, per-folder
  Sort by / Display as (Stack composite icons) / View content as (**Fan** with reverse retraction,
  **Grid** balloon, **List** menu, **Automatic** by count); drag out of fan/grid onto the dock;
  real **SVG icon rendering** (SharpVectors); Downloads seeded on first run.
- **Auto-hide the dock** (`AutoHideDock`): slide off the edge when idle, reveal on a 2px edge
  sliver, AppBar reservation released throughout.
- **Launch/attention bounce**: one hop on open, 3 hops on a taskbar attention flash (shell hook);
  icon-only transform so the running dot stays put.
- **Windows 11-style context menus** app-wide (`Themes/ModernMenu.xaml`), theme-aware; macOS-style
  Dock menu on empty space (Task Manager, hiding/magnification toggles, position + minimize-effect
  pickers); menu-bar item pill highlights + edge-to-edge logo click target.
- **Single instance** (Mutex); tray icon; settings persisted to `%APPDATA%\Dockable\settings.json`.

Likely next work / open TODOs: implement the non-Bottom **Position on screen** edges; exact
secondary-monitor placement; suppress the running dot for an app whose only window is minimized (vs.
its tile); auto-remove stale minimized tiles on app close (EVENT_OBJECT_DESTROY hook); UWP/Store pin
matching; reclaim work-area space when the taskbar is hidden; tune blind animation/size constants per
user feedback.

Full original plan: `C:\Users\cfiel\.claude\plans\let-s-start-a-new-flickering-puddle.md`.
