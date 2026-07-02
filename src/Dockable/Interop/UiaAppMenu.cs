using System.Windows.Automation;

namespace Dockable.Interop;

/// <summary>
/// Tier-2 fallback for apps with no Win32 HMENU (WPF, WinUI, Electron/VS Code, Qt, Java): find the
/// window's UI Automation MenuBar and mirror its top-level item names. Invoking expands the app's own
/// menu at ITS location — UIA can't re-anchor the popup under our bar. Both calls can be slow on apps
/// with huge accessibility trees, so run them off the UI thread; the found MenuBar element is cached
/// per window (including "has none") to avoid rescanning on every focus change.
/// </summary>
internal static class UiaAppMenu
{
    private static readonly object Gate = new();
    private static readonly Dictionary<IntPtr, AutomationElement?> Cache = new(); // null = known no-menu

    /// <summary>Reads the top-level items of <paramref name="hwnd"/>'s UIA menu bar, or null when the
    /// window exposes none. Call on a background thread.</summary>
    public static List<AppMenuEntry>? TryRead(IntPtr hwnd)
    {
        try
        {
            AutomationElement? menuBar = FindMenuBar(hwnd);
            if (menuBar is null)
                return null;

            var items = MenuItemsOf(menuBar);
            var entries = new List<AppMenuEntry>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                string name = (items[i].Current.Name ?? string.Empty).Trim();
                if (name.Length > 0)
                    entries.Add(new AppMenuEntry(name, i, AppMenuSource.Uia));
            }
            return entries.Count > 0 ? entries : null;
        }
        catch (ElementNotAvailableException)
        {
            lock (Gate)
                Cache.Remove(hwnd);
            return null;
        }
        catch
        {
            return null; // UIA is best-effort; a broken provider must never break the bar
        }
    }

    /// <summary>Expands (or invokes) the top-level menu item at <paramref name="index"/> — matched by
    /// <paramref name="label"/> if the menu changed since it was read. Call on a background thread;
    /// the target window should already be foreground so its menu tracks correctly.</summary>
    public static void Invoke(IntPtr hwnd, int index, string label)
    {
        try
        {
            AutomationElement? menuBar = FindMenuBar(hwnd);
            if (menuBar is null)
                return;

            var items = MenuItemsOf(menuBar);
            AutomationElement? item = index >= 0 && index < items.Count ? items[index] : null;
            if (item is null || !LabelMatches(item, label))
            {
                item = null;
                foreach (AutomationElement candidate in items)
                {
                    if (LabelMatches(candidate, label))
                    {
                        item = candidate;
                        break;
                    }
                }
            }
            if (item is null)
                return;

            if (item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object expand))
                ((ExpandCollapsePattern)expand).Expand();
            else if (item.TryGetCurrentPattern(InvokePattern.Pattern, out object invoke))
                ((InvokePattern)invoke).Invoke();
        }
        catch (ElementNotAvailableException)
        {
            lock (Gate)
                Cache.Remove(hwnd);
        }
        catch
        {
            // Best-effort — the app may have closed the menu, changed it, or gone away.
        }
    }

    private static AutomationElement? FindMenuBar(IntPtr hwnd)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(hwnd, out AutomationElement? cached))
                return cached;
        }

        // A window can expose more than one MenuBar: the non-client provider adds the title-bar
        // system menu ("System Menu Bar" → a lone "System" item) alongside any real app menu — skip
        // it, since clicking the app's display name on the bar already opens that menu.
        AutomationElement? menuBar = null;
        var bars = AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuBar));
        foreach (AutomationElement bar in bars)
        {
            if (!IsSystemMenuBar(bar))
            {
                menuBar = bar;
                break;
            }
        }

        lock (Gate)
        {
            if (Cache.Count > 64)
                Cache.Clear(); // HWNDs get destroyed and reused; keep the map tiny instead of tracking lifetimes
            Cache[hwnd] = menuBar;
        }
        return menuBar;
    }

    private static bool IsSystemMenuBar(AutomationElement bar)
    {
        try
        {
            // Locale-independent test: the system menu bar is parented inside the TitleBar element
            // (app menu bars never are — even custom-title-bar apps like VS Code expose theirs as
            // document content, not a UIA TitleBar).
            var parent = TreeWalker.ControlViewWalker.GetParent(bar);
            if (parent is not null && parent.Current.ControlType == ControlType.TitleBar)
                return true;
            // Belt-and-braces for providers that parent it elsewhere (name check is English-only).
            if (string.Equals(bar.Current.Name, "System Menu Bar", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // If we can't tell, keep the bar — worst case is a redundant "System" label.
        }
        return false;
    }

    private static AutomationElementCollection MenuItemsOf(AutomationElement menuBar)
        => menuBar.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));

    private static bool LabelMatches(AutomationElement item, string label)
        => string.Equals((item.Current.Name ?? string.Empty).Trim(), label, StringComparison.Ordinal);
}
