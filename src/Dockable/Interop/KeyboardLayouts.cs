using System.Globalization;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Dockable.Interop;

/// <summary>
/// Reads (Phase A) and switches (Phase B) the keyboard layout. The "current" layout is the one of the
/// foreground window's thread — Windows tracks the active input language per thread — so the menu bar
/// reflects whatever the user is typing into.
/// </summary>
public static class KeyboardLayouts
{
    // WM_INPUTLANGCHANGEREQUEST: ask a window to switch its thread's active keyboard layout. Not in the
    // Win32 metadata, so defined here.
    private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

    /// <summary>The active layout's two-letter language code (e.g. "EN", "PT"), or empty if unknown.</summary>
    public static string CurrentTwoLetter() => TwoLetterFor(CurrentLayoutHandle());

    /// <summary>The HKL (as a native handle) active on the foreground window's thread.</summary>
    internal static unsafe nint CurrentLayoutHandle()
    {
        HWND fg = PInvoke.GetForegroundWindow();
        uint tid = fg.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(fg, null);
        return (nint)PInvoke.GetKeyboardLayout(tid);
    }

    /// <summary>All installed keyboard layouts as (HKL handle, two-letter label) pairs, for the switcher.</summary>
    public static unsafe IReadOnlyList<(nint Hkl, string Label)> Installed()
    {
        int count = PInvoke.GetKeyboardLayoutList(0, null);
        if (count <= 0)
            return Array.Empty<(nint, string)>();

        var handles = new HKL[count];
        fixed (HKL* p = handles)
            count = PInvoke.GetKeyboardLayoutList(count, p);

        var result = new List<(nint, string)>(count);
        for (int i = 0; i < count; i++)
        {
            nint h = (nint)handles[i];
            result.Add((h, TwoLetterFor(h)));
        }
        return result;
    }

    /// <summary>Switches the foreground window's thread to the given layout (best-effort).</summary>
    public static void Switch(nint hkl)
    {
        HWND fg = PInvoke.GetForegroundWindow();
        if (fg.IsNull)
            return;
        // Post (don't send) so we don't block on the target's message loop; it changes that app's input
        // language, which is what the user sees in the menu bar.
        PInvoke.PostMessage(fg, WM_INPUTLANGCHANGEREQUEST, default, (LPARAM)hkl);
    }

    private static string TwoLetterFor(nint hkl)
    {
        // The low word of an HKL is the language identifier (LANGID).
        int langId = (int)(hkl & 0xFFFF);
        if (langId == 0)
            return string.Empty;
        try
        {
            return new CultureInfo(langId).TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch (CultureNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>Full display name for the switcher menu: the layout's language plus — when the OS
    /// knows it — the keyboard layout name (e.g. "English (United Kingdom) — United Kingdom",
    /// "English (United States) — United States-International"), so multiple layouts of the same
    /// language are distinguishable. Falls back to whichever half resolves; empty if neither does.</summary>
    public static string DisplayNameFor(nint hkl)
    {
        string language = LanguageNameFor(hkl);
        string layout = LayoutNameFor(hkl);
        if (language.Length == 0)
            return layout;
        if (layout.Length == 0)
            return language;
        return $"{language} — {layout}";
    }

    /// <summary>The layout language's localized display name (e.g. "English (United States)").</summary>
    private static string LanguageNameFor(nint hkl)
    {
        int langId = (int)(hkl & 0xFFFF);
        if (langId == 0)
            return string.Empty;
        try
        {
            return new CultureInfo(langId).DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return string.Empty;
        }
    }

    private const string LayoutsKey = @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts";

    /// <summary>The keyboard layout's own name from the OS registry ("US", "United Kingdom",
    /// "United States-International"), preferring the localized indirect display name.</summary>
    private static string LayoutNameFor(nint hkl)
    {
        string? klid = KlidFor(hkl);
        if (klid is null)
            return string.Empty;
        using var key = Registry.LocalMachine.OpenSubKey($@"{LayoutsKey}\{klid}");
        if (key is null)
            return string.Empty;
        // Prefer the OS-localized indirect name ("@%SystemRoot%\system32\input.dll,-5010"); fall back
        // to the legacy English-only "Layout Text".
        if (key.GetValue("Layout Display Name") is string indirect && indirect.StartsWith('@'))
        {
            string resolved = ResolveIndirectString(indirect);
            if (resolved.Length > 0)
                return resolved;
        }
        return key.GetValue("Layout Text") as string ?? string.Empty;
    }

    /// <summary>The registry KLID key name ("00000409") for an HKL. The high word identifies the
    /// layout: a KLID low word directly; for "device handles" (top nibble 0xF) a Layout Id to match
    /// against the installed layouts' "Layout Id" values (e.g. US-International, Dvorak); for IMEs
    /// (top nibble 0xE) the full 32-bit value is the KLID.</summary>
    private static string? KlidFor(nint hkl)
    {
        uint device = (uint)((ulong)hkl >> 16) & 0xFFFF;
        if (device == 0)
            return null;
        if ((device & 0xF000) == 0xF000)
        {
            uint layoutId = device & 0x0FFF;
            using var root = Registry.LocalMachine.OpenSubKey(LayoutsKey);
            if (root is null)
                return null;
            foreach (string name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k?.GetValue("Layout Id") is string id
                    && uint.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)
                    && v == layoutId)
                    return name;
            }
            return null;
        }
        if ((device & 0xF000) == 0xE000)
            return ((uint)((ulong)hkl & 0xFFFFFFFF)).ToString("X8");
        return device.ToString("X8");
    }

    /// <summary>Resolves a registry indirect string ("@dll,-id") to its localized text.</summary>
    private static string ResolveIndirectString(string indirect)
    {
        Span<char> buf = stackalloc char[512];
        if (PInvoke.SHLoadIndirectString(indirect, buf).Failed)
            return string.Empty;
        int len = buf.IndexOf('\0');
        return new string(buf[..(len < 0 ? buf.Length : len)]);
    }
}
