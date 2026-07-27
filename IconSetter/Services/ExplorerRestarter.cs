using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;

namespace IconSetter.Services
{
    /// <summary>
    /// Kills and relaunches explorer.exe, and tries to reopen the folder windows it had open
    /// beforehand.
    ///
    /// This exists as the "guaranteed, but disruptive" fallback to Explorer icon updates. Every
    /// icon refresh trick this app has (SHChangeNotify per-item and global, the shell custom-icon
    /// API) still goes through Explorer's own live notification queue, which testing showed
    /// processes on an unpredictable schedule - sometimes instant, sometimes minutes, sometimes
    /// not at all, with no code-level pattern behind which. Killing the process sidesteps that
    /// queue entirely: there's no stale cached icon left to eventually refresh, because the
    /// process holding it no longer exists. The new explorer.exe has to read everything fresh.
    /// </summary>
    public static class ExplorerRestarter
    {
        /// <summary>
        /// Uses the Shell.Application COM automation object to enumerate currently open Explorer
        /// folder windows and their paths. This is a long-standing but unofficial technique (no
        /// formal .NET wrapper exists for it) - late-bound via reflection rather than `dynamic` so
        /// this doesn't need a project reference to Microsoft.CSharp.
        ///
        /// Known limitation: on Windows 11's tabbed Explorer, this reports one entry per top-level
        /// window with only that window's *currently active* tab's path - other tabs in the same
        /// window aren't individually enumerable through this API, so a window with several tabs
        /// open will come back as a single window/path, and reopening it produces one plain window
        /// at that path rather than restoring every tab. The Windows() collection's enumeration
        /// order also doesn't correspond to the windows' on-screen/taskbar order, so the order
        /// they're reopened in won't necessarily match what the user had before, either.
        /// </summary>
        public static List<string> CaptureOpenExplorerFolderPaths()
        {
            var paths = new List<string>();
            try
            {
                Type? shellAppType = Type.GetTypeFromProgID("Shell.Application");
                if (shellAppType == null) return paths;

                object? shellApp = Activator.CreateInstance(shellAppType);
                if (shellApp == null) return paths;

                object? windows = shellAppType.InvokeMember("Windows",
                    BindingFlags.InvokeMethod, null, shellApp, null);
                if (windows == null) return paths;

                Type windowsType = windows.GetType();
                object? countObj = windowsType.InvokeMember("Count",
                    BindingFlags.GetProperty, null, windows, null);
                int count = countObj is int i ? i : 0;

                for (int idx = 0; idx < count; idx++)
                {
                    object? window;
                    try
                    {
                        window = windowsType.InvokeMember("Item",
                            BindingFlags.InvokeMethod, null, windows, new object[] { idx });
                    }
                    catch { continue; }
                    if (window == null) continue;

                    Type windowType = window.GetType();

                    // Only Explorer folder windows - Shell.Application.Windows() can also include
                    // Internet Explorer windows (if present) and other shell views; those have
                    // LocationURL values that aren't file:// paths, so the IsFile check below
                    // filters them out regardless, but checking Name first avoids invoking
                    // properties on window types that may not support them.
                    string? name = null;
                    try { name = windowType.InvokeMember("Name", BindingFlags.GetProperty, null, window, null) as string; }
                    catch { }
                    if (!string.Equals(name, "File Explorer", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, "Windows Explorer", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? locationUrl = null;
                    try { locationUrl = windowType.InvokeMember("LocationURL", BindingFlags.GetProperty, null, window, null) as string; }
                    catch { }
                    if (string.IsNullOrEmpty(locationUrl)) continue;

                    try
                    {
                        var uri = new Uri(locationUrl);
                        if (uri.IsFile) paths.Add(uri.LocalPath);
                    }
                    catch { }
                }
            }
            catch { /* Best-effort - if this fails, Explorer still restarts, we just won't reopen anything. */ }

            return paths;
        }

        /// <summary>Kills every explorer.exe process. It does not restart on its own afterward -
        /// callers must call <see cref="Relaunch"/>.</summary>
        public static void KillExplorer()
        {
            foreach (var proc in Process.GetProcessesByName("explorer"))
            {
                try { proc.Kill(); } catch { }
            }
        }

        /// <summary>Starts a fresh explorer.exe (this brings back the taskbar and desktop).</summary>
        public static void Relaunch()
        {
            try { Process.Start("explorer.exe"); } catch { }
        }

        /// <summary>Opens one Explorer window per captured path. Call after <see cref="Relaunch"/>
        /// has had a moment to finish starting up.</summary>
        public static void ReopenFolderWindows(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                try { Process.Start("explorer.exe", $"\"{path}\""); } catch { }
            }
        }

        /// <summary>Full sequence: capture open folder paths, kill Explorer, relaunch it, then
        /// reopen those folders. Returns the number of windows it attempted to reopen.</summary>
        public static async Task<int> RestartAndReopenAsync()
        {
            var paths = CaptureOpenExplorerFolderPaths();

            KillExplorer();
            await Task.Delay(500);
            Relaunch();

            // Give the new explorer.exe process a moment to finish initializing (taskbar, desktop)
            // before asking it to open more windows on top of that.
            await Task.Delay(1500);
            ReopenFolderWindows(paths);

            return paths.Count;
        }
    }
}
