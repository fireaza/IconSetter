using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace IconSetter.Services
{
    public class AppSettings
    {
        public string LastRootFolder { get; set; } = "";
        public bool SingleFolderOnly { get; set; } = false;
        public bool ConvertNonIco { get; set; } = true;
        public bool EnrichIco { get; set; } = false;
        public bool KeepIcoBackup { get; set; } = true;
        public int IconModeIndex { get; set; } = 0;
        public bool WindowMaximized { get; set; } = false;
        public bool ShowAllFolders { get; set; } = true;
        public bool DarkModePreview { get; set; } = false;
        public bool AlwaysShowUpToDate { get; set; } = false;
        public List<string> RecentFolders { get; set; } = new();
        public bool HideIcoAfterApply { get; set; } = true;
        public bool AlwaysShowMultipleIcons { get; set; } = false;

        private static string PortablePath =>
            Path.Combine(AppContext.BaseDirectory, "IconSetter.settings.json");

        private static string RoamingPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "IconSetter", "settings.json");

        public static AppSettings Load()
        {
            foreach (var path in new[] { PortablePath, RoamingPath })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        string json = File.ReadAllText(path);
                        var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                        if (loaded != null) return loaded;
                    }
                }
                catch
                {
                    // ignore and try the next location / fall through to defaults
                }
            }
            return new AppSettings();
        }

        public void Save()
        {
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

            // Prefer sitting right next to the exe (keeps the app fully portable/no-install).
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
                // Settings are a nicety, not a requirement - fail silently.
            }
        }
    }
}
