using Microsoft.Win32;

namespace Dockable.Interop;

/// <summary>Reads the current Windows app light/dark theme preference.</summary>
public static class SystemTheme
{
    /// <summary>True if Windows is using the light app theme (defaults to light if unreadable).</summary>
    public static bool IsLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v ? v != 0 : true;
        }
        catch
        {
            return true;
        }
    }
}
