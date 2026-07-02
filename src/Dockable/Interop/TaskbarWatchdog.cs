using System.Diagnostics;
using System.IO;
using System.Text;

namespace Dockable.Interop;

/// <summary>
/// Spawns a tiny out-of-process watchdog that waits for THIS process to exit and then restores the
/// taskbar to its pre-launch state. In-process exit/crash handlers can't run when the dock is
/// force-killed (Task Manager "End task", <c>taskkill /F</c>, <c>Stop-Process</c>) — only a separate
/// process survives that. A hidden <c>powershell.exe</c> is used rather than a second Dockable.exe:
/// its image name differs, so a kill-by-name of Dockable can't take it down with the app, and the
/// portable single-file build needs no extra shipped binary. The watchdog quits by itself right
/// after restoring — or without touching anything, if a new dock instance is already running and
/// owns the taskbar by then. Clean exits and managed crashes still restore in-process; the watchdog
/// re-asserting the same state afterwards is harmless (idempotent).
/// </summary>
internal static class TaskbarWatchdog
{
    /// <summary>Starts the watchdog for the current process. Best-effort — never throws.</summary>
    /// <param name="originalAutoHide">The taskbar's pre-launch auto-hide state to restore.</param>
    public static void Start(bool originalAutoHide)
    {
        try
        {
            string exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "Dockable";
            string script = Script
                .Replace("__PID__", Environment.ProcessId.ToString())
                .Replace("__EXENAME__", exeName)
                .Replace("__AUTOHIDE__", originalAutoHide ? "$true" : "$false");

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden "
                    + "-EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // The watchdog is a safety net on top of the in-process restore; never let it block startup.
        }
    }

    // PowerShell 5.1-safe; the embedded C# must stay C# 5 (Add-Type uses the Framework compiler:
    // no interpolation, no out-var). ABM_SETSTATE = 10, ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2,
    // SW_SHOW = 5 — mirrors Interop/Taskbar.SetAutoHide + EnsureTrayWindowsShown.
    private const string Script = """
        # Dockable taskbar watchdog: waits for the dock (pid __PID__) to exit, then restores the
        # Windows taskbar to its pre-launch state in case the dock was killed without running its
        # own exit handlers. Exits immediately afterwards.
        try { Wait-Process -Id __PID__ -ErrorAction Stop } catch { }
        Start-Sleep -Milliseconds 750
        if (Get-Process -Name '__EXENAME__' -ErrorAction SilentlyContinue) { exit } # a new dock owns the taskbar now

        Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;

        public static class TaskbarRestore
        {
            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int left; public int top; public int right; public int bottom; }

            [StructLayout(LayoutKind.Sequential)]
            public struct APPBARDATA
            {
                public uint cbSize;
                public IntPtr hWnd;
                public uint uCallbackMessage;
                public uint uEdge;
                public RECT rc;
                public IntPtr lParam;
            }

            [DllImport("shell32.dll")]
            public static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            public static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);

            [DllImport("user32.dll")]
            public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            public static void Restore(bool autoHide)
            {
                // Undo a possible SW_HIDE (TaskbarVisibility.Never) on every monitor's taskbar.
                IntPtr primary = FindWindowW("Shell_TrayWnd", null);
                if (primary != IntPtr.Zero)
                    ShowWindow(primary, 5);
                IntPtr secondary = IntPtr.Zero;
                while ((secondary = FindWindowExW(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                    ShowWindow(secondary, 5);

                // Re-assert the pre-launch auto-hide state.
                APPBARDATA data = new APPBARDATA();
                data.cbSize = (uint)Marshal.SizeOf(typeof(APPBARDATA));
                data.hWnd = primary;
                data.lParam = (IntPtr)(autoHide ? 1 : 2);
                SHAppBarMessage(10, ref data);
            }
        }
        '@
        [TaskbarRestore]::Restore(__AUTOHIDE__)
        """;
}
