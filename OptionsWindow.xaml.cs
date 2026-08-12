using System;
using System.Collections.ObjectModel;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace IconSetter
{
    /// <summary>
    /// Small settings popup, opened from the header's "⚙ Options" button. Holds the conversion
    /// settings and dark mode - the things that aren't part of picking a folder to scan, and
    /// aren't things most people change on every run. There's no Cancel: whatever's set here
    /// applies as soon as the window closes, however it's closed (Done button or the title bar
    /// X) - MainWindow just reads these properties back once ShowDialog() returns.
    /// </summary>
    public partial class OptionsWindow : Window
    {
        public bool ConvertNonIco
        {
            get => chkConvertNonIco.IsChecked == true;
            set => chkConvertNonIco.IsChecked = value;
        }

        public bool EnrichIco
        {
            get => chkEnrichIco.IsChecked == true;
            set => chkEnrichIco.IsChecked = value;
        }

        public bool KeepIcoBackup
        {
            get => chkKeepBackupIco.IsChecked == true;
            set => chkKeepBackupIco.IsChecked = value;
        }

        public int IconModeIndex
        {
            get => cmbIconMode.SelectedIndex;
            set
            {
                if (value >= 0 && value < cmbIconMode.Items.Count)
                    cmbIconMode.SelectedIndex = value;
            }
        }

        public bool DarkModePreview
        {
            get => chkDarkMode.IsChecked == true;
            set => chkDarkMode.IsChecked = value;
        }

        public bool AlwaysShowUpToDate
        {
            get => chkAlwaysShowUpToDate.IsChecked == true;
            set => chkAlwaysShowUpToDate.IsChecked = value;
        }

        public bool AlwaysShowMultipleIcons
        {
            get => chkAlwaysShowMultipleIcons.IsChecked == true;
            set => chkAlwaysShowMultipleIcons.IsChecked = value;
        }

        public bool HideIcoAfterApply
        {
            get => chkHideIcoAfterApply.IsChecked == true;
            set => chkHideIcoAfterApply.IsChecked = value;
        }

        public bool AlwaysSkipSmallImages
        {
            get => chkAlwaysSkipSmallImages.IsChecked == true;
            set => chkAlwaysSkipSmallImages.IsChecked = value;
        }

        /// <summary>MainWindow populates this with the current exclusion list before showing the
        /// dialog, and reads it back afterward - live-bound to the ListBox so Add/Remove reflect
        /// immediately, same pattern as the checkboxes above but for a list instead of a bool.</summary>
        public ObservableCollection<string> ExcludedFolders { get; } = new();

        public OptionsWindow()
        {
            InitializeComponent();
            lstExcludedFolders.ItemsSource = ExcludedFolders;
        }

        private void btnAddExcludedFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new WinForms.FolderBrowserDialog();
            if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;

            string path = dlg.SelectedPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            bool alreadyListed = false;
            foreach (var existing in ExcludedFolders)
            {
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) { alreadyListed = true; break; }
            }
            if (!alreadyListed) ExcludedFolders.Add(path);
        }

        private void btnRemoveExcludedFolder_Click(object sender, RoutedEventArgs e)
        {
            if (lstExcludedFolders.SelectedItem is string selected)
                ExcludedFolders.Remove(selected);
        }

        /// <summary>Set to true if the user confirmed clearing the scan cache. MainWindow checks
        /// this after the dialog closes and clears its own live ScanCache instance accordingly -
        /// this dialog can't safely touch the cache file directly, since MainWindow already holds
        /// its own loaded copy in memory and would just overwrite a directly-deleted file with that
        /// stale copy the next time it saves (e.g. on close).</summary>
        public bool ScanDataCleared { get; private set; }

        private void btnClearScanData_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "This deletes the saved scan cache, so future scans start fresh instead of reusing remembered results. " +
                "It doesn't touch any folders or icons - only this app's own saved data. Continue?",
                "Clear scan data", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            ScanDataCleared = true;
            MessageBox.Show("Scan data cleared.", "Icon Setter", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void btnDone_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
