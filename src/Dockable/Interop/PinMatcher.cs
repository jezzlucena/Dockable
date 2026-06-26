using System.IO;

namespace Dockable.Interop;

/// <summary>
/// Decides whether a running window belongs to a pinned app, using several strategies because
/// no single signal is reliable: exact exe path (Chrome, Brave), window==pin AppUserModelID
/// (apps that set one), the exe living in a subfolder of the pin's app folder (Steam's
/// <c>steamwebhelper.exe</c> under the Steam dir), and a special case for the File Explorer
/// shell pin (no exe target; AUMID <c>Microsoft.Windows.Explorer</c> → <c>explorer.exe</c>).
/// </summary>
public readonly struct PinMatcher
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;
    private static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>Stable key for reusing the app's tile view-model across refreshes.</summary>
    public string Key { get; }

    private readonly string? _targetExe;
    private readonly string? _targetDir;
    private readonly string? _aumid;
    private readonly bool _explorerLike;

    private PinMatcher(string key, string? targetExe, string? targetDir, string? aumid, bool explorerLike)
    {
        Key = key;
        _targetExe = targetExe;
        _targetDir = targetDir;
        _aumid = aumid;
        _explorerLike = explorerLike;
    }

    public static PinMatcher For(string pinPath)
    {
        bool isLink = pinPath.EndsWith(".lnk", Ci);
        string? targetExe = isLink ? TaskbarApps.ResolveLinkTarget(pinPath) : pinPath;
        string? aumid = isLink ? TaskbarApps.GetLinkAumid(pinPath) : null;
        string? targetDir = string.IsNullOrEmpty(targetExe) ? null : Path.GetDirectoryName(targetExe);
        bool explorerLike = string.Equals(aumid, "Microsoft.Windows.Explorer", Ci);

        string key = !string.IsNullOrEmpty(targetExe) ? targetExe!.ToLowerInvariant()
            : !string.IsNullOrEmpty(aumid) ? aumid!.ToLowerInvariant()
            : pinPath.ToLowerInvariant();

        return new PinMatcher(key, targetExe, targetDir, aumid, explorerLike);
    }

    public bool Matches(TaskbarApps.RunningWindow window)
    {
        if (!string.IsNullOrEmpty(_targetExe) && string.Equals(window.ExePath, _targetExe, Ci))
            return true;

        if (!string.IsNullOrEmpty(_aumid) && !string.IsNullOrEmpty(window.Aumid)
            && string.Equals(window.Aumid, _aumid, Ci))
            return true;

        if (_explorerLike && string.Equals(Path.GetFileName(window.ExePath), "explorer.exe", Ci))
            return true;

        // Window exe in a strict subfolder of the pin's app folder (e.g. Steam helpers). Skip
        // shared system folders, and require a deeper path so same-folder apps don't collide.
        if (!string.IsNullOrEmpty(_targetDir) && !_targetDir!.StartsWith(WindowsDir, Ci))
        {
            string windowDir = Path.GetDirectoryName(window.ExePath) ?? string.Empty;
            if (windowDir.StartsWith(_targetDir + Path.DirectorySeparatorChar, Ci))
                return true;
        }

        return false;
    }
}
