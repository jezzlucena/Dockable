namespace Dockable.Interop;

/// <summary>Which mechanism produced an <see cref="AppMenuEntry"/> — and therefore how it is invoked.</summary>
public enum AppMenuSource
{
    /// <summary>A classic Win32 menu bar (HMENU): we host the real dropdown under our own label.</summary>
    Win32,

    /// <summary>A UI Automation MenuBar (WPF/WinUI/Electron/Qt): invoking expands the app's own menu
    /// at its own location — UIA can't re-anchor the popup under our bar.</summary>
    Uia,
}

/// <summary>One top-level item ("File", "Edit", …) of the focused window's in-window menu bar,
/// mirrored onto the macOS-style menu bar.</summary>
/// <param name="Label">Display text with mnemonic markers ('&amp;') stripped.</param>
/// <param name="Index">The item's position in the source menu (used to invoke it later).</param>
/// <param name="Source">Which tier read it (and how to invoke it).</param>
public sealed record AppMenuEntry(string Label, int Index, AppMenuSource Source);
