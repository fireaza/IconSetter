using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace IconSetter.Services
{
    /// <summary>Everything BuildAllFolderResults computes for one folder, plus the cheap-to-check
    /// filesystem signals that determine whether that computation is still valid. Three signals
    /// are stored rather than just the folder's own timestamp, because each catches a different
    /// kind of change that the others miss:
    /// - FolderMTimeTicks: the folder's own LastWriteTimeUtc. On NTFS this updates when an entry is
    ///   added, removed, or renamed inside it - catches an icon/image file being added or deleted.
    /// - DesktopIniMTimeTicks: desktop.ini's own LastWriteTimeUtc (null if it doesn't exist). This
    ///   app rewrites desktop.ini's *content* in place for re-customizations rather than deleting
    ///   and recreating it, which does NOT reliably touch the parent folder's own mtime - so the
    ///   folder signal alone would miss an icon being set/changed/removed via this app.
    /// - SelectedIconMTimeTicks: the mtime of whichever .ico DetermineIconState was evaluated
    ///   against. Catches that exact file's content being replaced without renaming it (same
    ///   signal DetermineIconState itself already relies on for its own Up to date/Changed check).
    /// All three must still match for a cache entry to be considered valid - if any one is off,
    /// the folder gets rescanned properly rather than risking a stale, incorrect result.</summary>
    public class FolderScanCacheEntry
    {
        public long FolderMTimeTicks { get; set; }
        public long? DesktopIniMTimeTicks { get; set; }
        public long? SelectedIconMTimeTicks { get; set; }
        public int IconModeIndexUsed { get; set; }

        public List<string> IconFiles { get; set; } = new();
        public string? SelectedIcon { get; set; }
        public bool HasSubfolders { get; set; }
        public bool HasUnconvertedImage { get; set; }
        public string? CustomName { get; set; }
        public string IconState { get; set; } = "New";
    }

    public class ScanCache
    {
        public Dictionary<string, FolderScanCacheEntry> Entries { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        private static string PortablePath =>
            Path.Combine(AppContext.BaseDirectory, "IconSetter.scancache.json");

        private static string RoamingPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "IconSetter", "scancache.json");

        public static ScanCache Load()
        {
            foreach (var path in new[] { PortablePath, RoamingPath })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        string json = File.ReadAllText(path);
                        var loaded = JsonSerializer.Deserialize<ScanCache>(json);
                        if (loaded != null)
                        {
                            loaded.Entries ??= new Dictionary<string, FolderScanCacheEntry>(StringComparer.OrdinalIgnoreCase);
                            return loaded;
                        }
                    }
                }
                catch
                {
                    // A corrupt or unreadable cache is a performance nicety lost, not a real
                    // problem - fall through to a fresh, empty cache rather than surfacing an
                    // error.
                }
            }
            return new ScanCache();
        }

        public void Save()
        {
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false });

            try
            {
                File.WriteAllText(PortablePath, json);
                return;
            }
            catch
            {
                // Likely running from a read-only location (Program Files, a mounted ISO, etc.)
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RoamingPath)!);
                File.WriteAllText(RoamingPath, json);
            }
            catch
            {
                // The cache is a performance nicety, not a requirement - fail silently.
            }
        }

        /// <summary>Deletes the cache file(s) from disk in addition to clearing Entries in memory -
        /// used by the "Clear scan data" button.</summary>
        public void ClearAndDelete()
        {
            Entries.Clear();
            foreach (var path in new[] { PortablePath, RoamingPath })
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
    }
}
