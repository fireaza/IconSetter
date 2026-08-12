using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;
using IconSetter.Models;
using IconSetter.Services;

namespace IconSetter
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<FolderResult> FolderResults { get; } = new();
        public ObservableCollection<FolderResult> ResultLog { get; } = new();

        private readonly Random rnd = new();
        private ICollectionView? folderResultsView;
        private bool _cancelRequested;
        private bool _skipAllSmallImagesThisSession; // in-memory only - resets on app restart, not saved to settings
        private TaskCompletionSource<bool>? _smallImagePrompt;
        private AppSettings _settings = new();
        private ScanCache _scanCache = new();
        private bool _explorerMode;
        private string _explorerRoot = "";
        private string _explorerCurrent = "";
        private bool _darkMode;
        private bool _alwaysShowUpToDate;
        private bool _hideIcoAfterApply = true;
        private bool _alwaysSkipSmallImages;
        private List<string> _excludedFolders = new();
        private bool _alwaysShowMultipleIcons;
        private List<string> _recentFolders = new();

        // Moved out of MainWindow's own XAML and into OptionsWindow - these are the source of
        // truth now, read/written by btnOptions_Click rather than by named controls.
        private bool _convertNonIco = true;
        private bool _enrichIco;
        private bool _keepIcoBackup = true;
        private int _iconModeIndex;

        // Which category filter chip (if any) is currently narrowing the gallery. Null means no
        // filter - the normal New/Changed/up-to-date rules in ShouldDisplayInGallery apply instead.
        private string? _activeCategoryFilter;
        private bool _updatingCategoryChips;

        /// <summary>Incremented at the start of every RefreshFlatViewAsync/PopulateExplorerLevelAsync
        /// call. Both methods yield partway through for large folders, so if a second call starts
        /// before an earlier one finishes (e.g. rapid filter toggling, or the scan pipeline's own
        /// call landing while a leftover call from before it is still paused at a yield), the two
        /// would otherwise interleave their writes to the same FolderResults collection - showing a
        /// mix of both filter states, or the empty-state message rendered over stale results from
        /// the other call. Each call captures its own generation number and checks it after every
        /// yield; a mismatch means a newer call has since started, so it abandons its own work
        /// immediately rather than continuing to write.</summary>
        private int _galleryRefreshGeneration;

        private bool _filterMultipleIconsOnly;

        /// <summary>Text color for gallery folder names - light grey in dark mode, the normal
        /// theme color otherwise. Bound from the DataTemplate via RelativeSource=Window since the
        /// template's own DataContext is the FolderResult item, not the window.</summary>
        public System.Windows.Media.Brush GalleryTextBrush => _darkMode
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xE8, 0xE8))
            : (System.Windows.Media.Brush)FindResource("TextPrimaryBrush");

        public System.Windows.Media.Brush GallerySecondaryTextBrush => _darkMode
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0x9A, 0x9A))
            : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private void btnOptions_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OptionsWindow
            {
                Owner = this,
                ConvertNonIco = _convertNonIco,
                EnrichIco = _enrichIco,
                KeepIcoBackup = _keepIcoBackup,
                IconModeIndex = _iconModeIndex,
                DarkModePreview = _darkMode,
                AlwaysShowUpToDate = _alwaysShowUpToDate,
                HideIcoAfterApply = _hideIcoAfterApply,
                AlwaysShowMultipleIcons = _alwaysShowMultipleIcons,
                AlwaysSkipSmallImages = _alwaysSkipSmallImages
            };
            foreach (var f in _excludedFolders) dlg.ExcludedFolders.Add(f);
            dlg.ShowDialog();

            _convertNonIco = dlg.ConvertNonIco;
            _enrichIco = dlg.EnrichIco;
            _keepIcoBackup = dlg.KeepIcoBackup;
            _iconModeIndex = dlg.IconModeIndex;
            _alwaysShowUpToDate = dlg.AlwaysShowUpToDate;
            _hideIcoAfterApply = dlg.HideIcoAfterApply;
            _alwaysSkipSmallImages = dlg.AlwaysSkipSmallImages;
            _excludedFolders = dlg.ExcludedFolders.ToList();
            bool alwaysShowMultipleIconsBefore = _alwaysShowMultipleIcons;
            _alwaysShowMultipleIcons = dlg.AlwaysShowMultipleIcons;
            if (_alwaysShowMultipleIcons != alwaysShowMultipleIconsBefore)
                chipMultipleIcons.IsChecked = _alwaysShowMultipleIcons; // fires ChkMultipleIconsFilter_Changed, refreshing the gallery right away

            if (dlg.DarkModePreview != _darkMode)
            {
                _darkMode = dlg.DarkModePreview;

                // Explorer's own dark theme uses roughly this near-black grey for content areas.
                galleryBackground.Background = _darkMode
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20))
                    : System.Windows.Media.Brushes.Transparent;

                OnPropertyChanged(nameof(GalleryTextBrush));
                OnPropertyChanged(nameof(GallerySecondaryTextBrush));
            }

            if (dlg.ScanDataCleared)
            {
                _scanCache.ClearAndDelete();
                ResetScanDisplay();
            }
        }

        /// <summary>Resets the gallery, chip counts, and "N folder(s) detected" text back to their
        /// pre-scan state, matching what a fresh launch looks like before anything's been scanned.
        /// Used by "Clear scan data" - without this, clearing the cache is invisible: the chip
        /// counts and gallery aren't derived from the cache at all (they're always computed fresh
        /// from disk), so nothing on screen would visibly change even though the clear genuinely
        /// worked, leaving no confirmation it did anything.</summary>
        private void ResetScanDisplay()
        {
            _allFolders.Clear();
            FolderResults.Clear();
            _galleryRefreshGeneration++; // abandon any in-flight refresh so it doesn't repopulate over this reset

            chipWithIco.Content = "Has .ico (0)";
            chipWithNonIco.Content = "Has \"icon\" image (0)";
            chipWithBoth.Content = "Has both (0)";
            chipWithoutIcon.Content = "No .ico (0)";
            chipMultipleIcons.Content = "Has multiple .ico (0)";
            txtPreviewCount.Text = "";

            UpdateGalleryEmptyState();
        }

        private void btnHelp_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new HelpWindow { Owner = this };
            dlg.ShowDialog();
        }

        /// <summary>The full scanned set, keyed by folder path. This is what Apply always acts on -
        /// what's currently displayed in the gallery (FolderResults) is just a view onto this,
        /// either the whole thing (Show all folders) or one directory level (Explorer browsing).</summary>
        private readonly Dictionary<string, FolderResult> _allFolders = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(FolderResult Result, FolderIconSnapshot Snapshot)> _lastApplied = new();

        /// <summary>Captures exactly what a folder's icon state looked like right before Apply
        /// touches it, so Undo can put it back byte-for-byte - whether that means restoring the
        /// desktop.ini a different icon-setting tool wrote, restoring the previous custom icon
        /// this app had set, or simply having no desktop.ini at all (a brand-new folder).</summary>
        private sealed class FolderIconSnapshot
        {
            public bool HadDesktopIni { get; init; }
            public byte[]? DesktopIniRawBytes { get; init; }
            public FileAttributes DesktopIniAttributes { get; init; }
            public FileAttributes FolderAttributesBefore { get; init; }
        }

        private static FolderIconSnapshot CaptureFolderIconSnapshot(string folder)
        {
            string desktopIni = Path.Combine(folder, "desktop.ini");
            bool had = File.Exists(desktopIni);
            return new FolderIconSnapshot
            {
                HadDesktopIni = had,
                DesktopIniRawBytes = had ? File.ReadAllBytes(desktopIni) : null,
                DesktopIniAttributes = had ? File.GetAttributes(desktopIni) : default,
                FolderAttributesBefore = File.GetAttributes(folder)
            };
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            iconGallery.ItemsSource = FolderResults;
            lvResults.ItemsSource = ResultLog;

            Loaded += Window_Loaded;
            Closing += MainWindow_Closing;

            ResultLog.CollectionChanged += (s, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add)
                {
                    lvResults.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (lvResults.Items.Count > 0)
                            lvResults.ScrollIntoView(lvResults.Items[^1]);
                    }));
                }
            };
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            folderResultsView = CollectionViewSource.GetDefaultView(FolderResults);

            _settings = AppSettings.Load();
            _scanCache = ScanCache.Load();
            txtRoot.Text = _settings.LastRootFolder;
            chkSingleFolder.IsChecked = _settings.SingleFolderOnly;
            _convertNonIco = _settings.ConvertNonIco;
            _enrichIco = _settings.EnrichIco;
            _keepIcoBackup = _settings.KeepIcoBackup;
            _iconModeIndex = (_settings.IconModeIndex >= 0 && _settings.IconModeIndex < 4) ? _settings.IconModeIndex : 0;

            if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

            rbShowAllFolders.IsChecked = _settings.ShowAllFolders;
            rbExplorerStyle.IsChecked = !_settings.ShowAllFolders;
            _explorerMode = !_settings.ShowAllFolders;
            _darkMode = _settings.DarkModePreview;
            galleryBackground.Background = _darkMode
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20))
                : System.Windows.Media.Brushes.Transparent;

            _alwaysShowUpToDate = _settings.AlwaysShowUpToDate;
            chkShowUpToDate.IsChecked = _alwaysShowUpToDate;
            _hideIcoAfterApply = _settings.HideIcoAfterApply;
            _alwaysSkipSmallImages = _settings.AlwaysSkipSmallImages;
            _excludedFolders = _settings.ExcludedFolders ?? new List<string>();
            _alwaysShowMultipleIcons = _settings.AlwaysShowMultipleIcons;
            chipMultipleIcons.IsChecked = _alwaysShowMultipleIcons;

            _recentFolders = _settings.RecentFolders ?? new List<string>();
            RebuildRecentFoldersMenu();

            UpdateGalleryEmptyState();
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            _settings.LastRootFolder = txtRoot.Text;
            _settings.SingleFolderOnly = chkSingleFolder.IsChecked == true;
            _settings.ConvertNonIco = _convertNonIco;
            _settings.EnrichIco = _enrichIco;
            _settings.KeepIcoBackup = _keepIcoBackup;
            _settings.IconModeIndex = _iconModeIndex;
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            _settings.ShowAllFolders = rbShowAllFolders.IsChecked == true;
            _settings.DarkModePreview = _darkMode;
            _settings.AlwaysShowUpToDate = _alwaysShowUpToDate;
            _settings.HideIcoAfterApply = _hideIcoAfterApply;
            _settings.AlwaysSkipSmallImages = _alwaysSkipSmallImages;
            _settings.ExcludedFolders = _excludedFolders;
            _settings.AlwaysShowMultipleIcons = _alwaysShowMultipleIcons;
            _settings.RecentFolders = _recentFolders;
            _settings.Save();
            _scanCache.Save();
        }

        // ===================== Drag & drop =====================

        private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                ? System.Windows.DragDropEffects.Copy
                : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;

            var paths = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            string? folder = paths.FirstOrDefault(p => Directory.Exists(p));
            if (folder == null) return;

            txtRoot.Text = folder;
            await ScanAndBuildGalleryAsync();
        }

        // ===================== Folder scanning =====================

        /// <summary>One folder's icon*.* files, listed once and classified in memory - shared
        /// across every step of a scan that needs to know "what icon-related files are here" so
        /// none of them have to separately re-list the same directory. Built fresh whenever the
        /// underlying files could have changed (see SnapshotFolders and where it's called from
        /// ScanAndBuildGalleryAsync) - never reused across a filesystem-modifying step like
        /// conversion, since that would mean acting on stale information about what's on disk.</summary>
        private sealed class FolderFileSnapshot
        {
            public List<string> IcoFiles { get; init; } = new();
            public List<string> UnconvertedImages { get; init; } = new();
            public bool HasUnconvertedImage => UnconvertedImages.Count > 0;
        }

        /// <summary>Lists each folder's icon*.* files exactly once and classifies the results by
        /// extension, instead of the previous approach of separately calling Directory.GetFiles
        /// with "icon*.ico" and "icon*.*" patterns (and doing that 2-4 times per folder across the
        /// different steps of a scan). Pure I/O, safe to run on a background thread.</summary>
        private static Dictionary<string, FolderFileSnapshot> SnapshotFolders(List<string> folders)
        {
            var result = new Dictionary<string, FolderFileSnapshot>(folders.Count, StringComparer.OrdinalIgnoreCase);

            foreach (var folder in folders)
            {
                var icoFiles = new List<string>();
                var images = new List<string>();
                try
                {
                    foreach (var f in Directory.GetFiles(folder, "icon*.*"))
                    {
                        if (f.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                            icoFiles.Add(f);
                        else if (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                            images.Add(f);
                    }
                }
                catch { /* permission errors etc. - leave this folder's snapshot empty, same tolerance the individual calls this replaces already had */ }

                result[folder] = new FolderFileSnapshot { IcoFiles = icoFiles, UnconvertedImages = images };
            }

            return result;
        }

        private List<string> CollectFolders(string root, bool singleFolderOnly)
        {
            var folders = new List<string> { root };
            try
            {
                if (!singleFolderOnly)
                    folders.AddRange(Directory.GetDirectories(root, "*", SearchOption.AllDirectories));
            }
            catch { /* permission errors on some subfolders shouldn't kill the whole scan */ }

            if (_excludedFolders.Count > 0)
                folders = folders.Where(f => !IsExcludedFolder(f)).ToList();

            return folders;
        }

        /// <summary>True if this folder is itself excluded, or sits anywhere underneath an
        /// excluded folder - excluding a folder excludes everything inside it too.</summary>
        private bool IsExcludedFolder(string folder)
        {
            string normalized = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var excluded in _excludedFolders)
            {
                string excludedNormalized = excluded.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(normalized, excludedNormalized, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (normalized.StartsWith(excludedNormalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private async void btnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new WinForms.FolderBrowserDialog();
            if (dlg.ShowDialog() == WinForms.DialogResult.OK)
            {
                txtRoot.Text = dlg.SelectedPath;
                await ScanAndBuildGalleryAsync();
            }
        }

        private async void txtRoot_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            if (!string.IsNullOrWhiteSpace(txtRoot.Text) && Directory.Exists(txtRoot.Text))
            {
                string currentRoot = txtRoot.Text;
                bool singleFolderOnly = chkSingleFolder.IsChecked == true;
                var folders = await Task.Run(() => CollectFolders(currentRoot, singleFolderOnly));
                await UpdateFolderStatsAsync(folders);
            }
            await ScanAndBuildGalleryAsync();
        }

        private async void btnScan_Click(object sender, RoutedEventArgs e) => await ScanAndBuildGalleryAsync();

        private async Task ScanAndBuildGalleryAsync()
        {
            FolderResults.Clear();
            txtGalleryEmpty.Visibility = Visibility.Collapsed; // clear the "Pick a folder…" placeholder immediately - it shouldn't linger behind the loading overlay for the whole scan
            string root = txtRoot.Text;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                WpfMessageBox.Show("Please select a valid folder.");
                return;
            }

            AddToRecentFolders(root);
            _cancelRequested = false;

            // Walking a large folder tree and inspecting every folder's files is real disk I/O
            // and CPU work - potentially thousands of Directory.GetFiles calls for a big tree -
            // so each heavy step below runs on a background thread via Task.Run rather than
            // blocking the UI thread. Left synchronous, a large enough tree freezes the whole
            // window and can trigger a Windows "Not Responding" prompt, even though the app is
            // still working correctly and eventually recovers.
            ShowScanningOverlay("Scanning folders…");
            List<string> folders;
            Dictionary<string, FolderFileSnapshot> snapshot;
            try
            {
                bool singleFolderOnly = chkSingleFolder.IsChecked == true;
                folders = await Task.Run(() => CollectFolders(root, singleFolderOnly));
                if (_cancelRequested) return;

                // One shared listing of each folder's icon*.* files, used by the stats pass right
                // below and by conversion further down - instead of each step separately
                // re-listing the same folders (previously 2-4 Directory.GetFiles calls per folder
                // across the different steps of a scan).
                snapshot = await Task.Run(() => SnapshotFolders(folders));
                if (_cancelRequested) return;

                // Also covered by this same overlay - ComputeFolderStats does its own per-folder
                // Directory.GetFiles calls (twice per folder, for a large tree), and previously ran
                // with the overlay already hidden, leaving several seconds where the gallery just
                // looked empty with nothing indicating anything was still happening.
                await UpdateFolderStatsAsync(folders, snapshot);
            }
            finally
            {
                HideScanningOverlay();
            }
            if (_cancelRequested) return;

            // Whether there's actually anything for the conversion step to do - computed here
            // (from the snapshot already in hand) so it's known below regardless of whether
            // ConvertImagesAsync itself gets called at all, or which of the two cancels it might.
            bool anyImagesToConvert = _convertNonIco &&
                folders.Any(f => snapshot.TryGetValue(f, out var s) && s.HasUnconvertedImage);

            if (_convertNonIco)
            {
                bool completed = await ConvertImagesAsync(folders, root, snapshot);
                if (!completed) return; // user cancelled
            }

            if (_enrichIco)
            {
                ShowScanningOverlay("Adding missing icon sizes…");
                try
                {
                    await Task.Run(() => EnrichExistingIcons(folders));
                }
                finally
                {
                    HideScanningOverlay();
                }
                if (_cancelRequested) return;
            }

            ShowScanningOverlay("Building folder list…");
            try
            {
                // Conversion/enrichment can change what .ico files exist on disk, so this needs a
                // fresh read - reusing the earlier snapshot would mean BuildAllFolderResults never
                // sees newly-converted icons. If neither actually ran (or ran but found nothing to
                // do), the earlier snapshot is still accurate, so re-reading the same folders again
                // would just be wasted I/O - this is the common case for re-scanning a folder tree
                // that's already fully converted, since "convert non-.ico images" defaults to on.
                if (anyImagesToConvert || _enrichIco)
                {
                    snapshot = await Task.Run(() => SnapshotFolders(folders));
                    if (_cancelRequested) return;
                }

                await Task.Run(() => BuildAllFolderResults(folders, snapshot));
                if (_cancelRequested) return;

                // Same reasoning as the first UpdateFolderStatsAsync call above - keeping this
                // under the same overlay avoids another silent multi-second gap between the
                // overlay disappearing and the gallery actually being repopulated.
                await UpdateFolderStatsAsync(folders, snapshot); // refresh chip counts - conversion above can change them
            }
            finally
            {
                HideScanningOverlay();
            }
            if (_cancelRequested) return;

            if (_explorerMode)
            {
                _explorerRoot = root.TrimEnd(Path.DirectorySeparatorChar);
                await PopulateExplorerLevelAsync(_explorerRoot);
            }
            else
            {
                await RefreshFlatViewAsync();
            }
        }

        /// <summary>Shows the loading overlay for a background phase that has no natural per-item
        /// progress to report (unlike image conversion) - just a spinner and a status line. The
        /// warning-dialog controls are hidden since they only apply during image conversion.</summary>
        private void ShowScanningOverlay(string message)
        {
            loadingPanel.Visibility = Visibility.Visible;
            loadingText.Text = message;
            loadingProgress.IsIndeterminate = true;
            loadingSpinner.Visibility = Visibility.Visible;
            loadingCurrentIcon.Visibility = Visibility.Collapsed;
            warningContainer.Visibility = Visibility.Collapsed;
            btnContinueToPreview.Visibility = Visibility.Collapsed;
            btnSkipImage.Visibility = Visibility.Collapsed;
            btnSkipAllSmallImages.Visibility = Visibility.Collapsed;
        }

        private void HideScanningOverlay()
        {
            loadingPanel.Visibility = Visibility.Collapsed;
            loadingProgress.IsIndeterminate = false;
            loadingProgress.Value = 0;
        }

        /// <summary>Adds a successfully-scanned root to the recent-folders list, most-recent-first,
        /// deduplicated (moving an existing entry back to the top rather than adding a second
        /// copy), capped at 8 entries.</summary>
        private void AddToRecentFolders(string root)
        {
            string normalized = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _recentFolders.RemoveAll(f => string.Equals(f, normalized, StringComparison.OrdinalIgnoreCase));
            _recentFolders.Insert(0, normalized);
            if (_recentFolders.Count > 8) _recentFolders.RemoveRange(8, _recentFolders.Count - 8);
            RebuildRecentFoldersMenu();
        }

        private void RebuildRecentFoldersMenu()
        {
            cmRecentFolders.Items.Clear();

            if (_recentFolders.Count == 0)
            {
                cmRecentFolders.Items.Add(new System.Windows.Controls.MenuItem { Header = "(no recent folders yet)", IsEnabled = false });
                return;
            }

            foreach (var folder in _recentFolders)
            {
                var item = new System.Windows.Controls.MenuItem { Header = folder, Tag = folder };
                item.Click += RecentFolder_Click;
                cmRecentFolders.Items.Add(item);
            }

            cmRecentFolders.Items.Add(new System.Windows.Controls.Separator());
            var clearItem = new System.Windows.Controls.MenuItem { Header = "Clear recent folders" };
            clearItem.Click += ClearRecentFolders_Click;
            cmRecentFolders.Items.Add(clearItem);
        }

        private void btnRecentFolders_Click(object sender, RoutedEventArgs e)
        {
            cmRecentFolders.PlacementTarget = btnRecentFolders;
            cmRecentFolders.IsOpen = true;
        }

        private async void RecentFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.MenuItem item || item.Tag is not string folder) return;

            if (Directory.Exists(folder))
            {
                txtRoot.Text = folder;
                await ScanAndBuildGalleryAsync();
            }
            else
            {
                WpfMessageBox.Show($"\"{folder}\" no longer exists.", "Icon Setter", MessageBoxButton.OK, MessageBoxImage.Warning);
                _recentFolders.Remove(folder);
                RebuildRecentFoldersMenu();
            }
        }

        private void ClearRecentFolders_Click(object sender, RoutedEventArgs e)
        {
            _recentFolders.Clear();
            RebuildRecentFoldersMenu();
        }

        private async Task<bool> ConvertImagesAsync(List<string> folders, string root, Dictionary<string, FolderFileSnapshot> snapshot)
        {
            bool anyImages = folders.Any(f => snapshot.TryGetValue(f, out var s) && s.HasUnconvertedImage);

            if (!anyImages) return true; // nothing to convert - skip the overlay entirely

            _cancelRequested = false;
            loadingPanel.Visibility = Visibility.Visible;
            loadingText.Text = "Converting images…";
            loadingProgress.Value = 0;
            loadingProgress.IsIndeterminate = false;
            loadingSpinner.Visibility = Visibility.Visible;
            loadingCurrentIcon.Visibility = Visibility.Collapsed;

            string rootPath = root.TrimEnd(Path.DirectorySeparatorChar);
            string rootName = Path.GetFileName(rootPath);

            int total = Math.Max(folders.Count, 1);
            int processed = 0;

            foreach (var folder in folders)
            {
                if (_cancelRequested) break;

                var imageFiles = snapshot.TryGetValue(folder, out var folderFiles)
                    ? folderFiles.UnconvertedImages
                    : new List<string>();

                foreach (var img in imageFiles)
                {
                    if (_cancelRequested) break;

                    try
                    {
                        int imgWidth, imgHeight;
                        using (var testBmp = new System.Drawing.Bitmap(img))
                        {
                            imgWidth = testBmp.Width;
                            imgHeight = testBmp.Height;
                        }

                        if (imgWidth < 256 || imgHeight < 256)
                        {
                            if (_alwaysSkipSmallImages || _skipAllSmallImagesThisSession) continue;

                            // Show the actual problem image (not whatever was last converted) and
                            // pause here until the user decides, one image at a time - rather than
                            // silently collecting every small image and asking about all of them
                            // in one batch at the end.
                            ShowCurrentIcon(img);
                            string displayPath = img.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)
                                ? rootName + img.Substring(rootPath.Length)
                                : img;
                            bool convertAnyway = await PromptSmallImageAsync($"{displayPath} ({imgWidth}×{imgHeight})");

                            // The button handlers already reset the preview back to the plain
                            // spinner, but setting a property doesn't force it to actually get
                            // painted - without yielding back to the UI thread here, that reset
                            // state can go completely unseen if what runs next is synchronous
                            // (e.g. this was the last image, and BuildAllFolderResults runs right
                            // after this loop with no further awaits in between), making Skip look
                            // like it did nothing right up until the whole overlay disappears.
                            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);

                            if (_cancelRequested) break;
                            if (!convertAnyway) continue; // skip just this image, move to the next
                        }

                        string baseTarget = Path.Combine(
                            Path.GetDirectoryName(img)!,
                            Path.GetFileNameWithoutExtension(img) + ".ico");
                        string icoPath = GetNextIconPath(baseTarget);

                        ShowCurrentIcon(img);
                        IconConverter.ConvertToIco(img, icoPath);

                        // Always removed (to the Recycle Bin, so it's recoverable) - otherwise the
                        // source image is still there next time the scan runs and a duplicate .ico
                        // gets created every time.
                        if (!RecycleBinHelper.Delete(img))
                        {
                            ResultLog.Add(new FolderResult
                            {
                                FolderPath = folder,
                                SelectedIcon = img,
                                Status = "Error deleting source image"
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        ResultLog.Add(new FolderResult
                        {
                            FolderPath = folder,
                            SelectedIcon = img,
                            Status = "Error converting: " + ex.Message
                        });
                    }
                }

                processed++;
                loadingProgress.Value = (double)processed / total * 100.0;
                await Task.Delay(10);
            }

            loadingPanel.Visibility = Visibility.Collapsed;

            if (_cancelRequested)
            {
                ResetToOptions();
                return false;
            }

            return true;
        }

        /// <summary>Shows the too-small-image warning for one specific image and waits for the
        /// user to choose. Returns true for "Convert anyway", false for "Skip this image" (or if
        /// the general Cancel button was used instead, in which case _cancelRequested is also set
        /// and the caller checks that separately).</summary>
        private Task<bool> PromptSmallImageAsync(string displayText)
        {
            _smallImagePrompt = new TaskCompletionSource<bool>();

            loadingWarning.Text = displayText;
            warningContainer.Visibility = Visibility.Visible;
            btnContinueToPreview.Visibility = Visibility.Visible;
            btnSkipImage.Visibility = Visibility.Visible;
            btnSkipAllSmallImages.Visibility = Visibility.Visible;

            return _smallImagePrompt.Task;
        }

        private void EnrichExistingIcons(List<string> folders)
        {
            foreach (var folder in folders)
            {
                foreach (var ico in Directory.GetFiles(folder, "icon*.ico"))
                {
                    try
                    {
                        IconConverter.EnrichIco(ico, _keepIcoBackup);
                    }
                    catch
                    {
                        // Non-fatal - the icon still works, it just won't have every size.
                    }
                }
            }
        }

        private void ShowCurrentIcon(string path)
        {
            try
            {
                System.Windows.Media.Imaging.BitmapSource? source;
                if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    source = IconConverter.LoadLargestIconFrame(path);
                }
                else
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(path);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    source = bmp;
                }
                loadingCurrentIcon.Source = source;
                loadingSpinner.Visibility = Visibility.Collapsed;
                loadingCurrentIcon.Visibility = Visibility.Visible;
            }
            catch
            {
                loadingCurrentIcon.Source = null;
                loadingCurrentIcon.Visibility = Visibility.Collapsed;
                loadingSpinner.Visibility = Visibility.Visible;
            }
        }

        // ===================== Preview building =====================

        private void BuildAllFolderResults(List<string> folders, Dictionary<string, FolderFileSnapshot> snapshot)
        {
            _allFolders.Clear();
            int mode = _iconModeIndex; // 0 keep, 1 first, 2 random, 3 newest

            foreach (var folder in folders)
            {
                // Random mode picks a new icon every time by definition - reusing a cached
                // selection would defeat the entire point of choosing it, so this mode always
                // does a full fresh scan rather than consulting the cache.
                if (mode != 2 && TryReuseCachedResult(folder, mode, out var cachedFr))
                {
                    _allFolders[folder] = cachedFr!;
                    continue;
                }

                var icoFiles = snapshot.TryGetValue(folder, out var folderFiles) ? folderFiles.IcoFiles : new List<string>();
                string? selected = null;

                if (icoFiles.Count > 0)
                {
                    string? currentSelection = ReadCurrentIconResourceFromDesktopIni(folder);
                    selected = mode switch
                    {
                        1 => icoFiles.OrderBy(f => f).First(),
                        2 => icoFiles[rnd.Next(icoFiles.Count)],
                        3 => icoFiles.OrderByDescending(File.GetLastWriteTimeUtc).First(),
                        _ => (currentSelection != null && icoFiles.Contains(currentSelection))
                                ? currentSelection
                                : icoFiles.First()
                    };
                }

                bool hasSub = false;
                try { hasSub = Directory.EnumerateDirectories(folder).Any(); } catch { }

                bool hasUnconvertedImage = folderFiles?.HasUnconvertedImage ?? false;
                string? customName = ReadLocalizedNameFromDesktopIni(folder);

                var fr = new FolderResult
                {
                    FolderPath = folder,
                    IconFiles = icoFiles,
                    SelectedIcon = selected,
                    Status = "Pending",
                    HasSubfolders = hasSub,
                    HasUnconvertedImage = hasUnconvertedImage,
                    CustomName = customName
                };

                if (selected != null) fr.IconState = DetermineIconState(folder, selected);

                _allFolders[folder] = fr;

                // Random mode is never cached (see above) - every other mode gets its result
                // stored for next time, keyed on the filesystem signals that would need to change
                // for it to go stale.
                if (mode != 2)
                    StoreCacheEntry(folder, mode, icoFiles, selected, hasSub, hasUnconvertedImage, customName, fr.IconState);
            }
        }

        /// <summary>Attempts to reuse a cached scan result for this folder. Returns false (leaving
        /// fr null) if there's no entry, the icon mode used to build it doesn't match the current
        /// one, or any of the three staleness signals (folder mtime, desktop.ini mtime, selected
        /// icon's own mtime - see FolderScanCacheEntry) no longer match what's on disk. In every
        /// one of those cases the caller falls through to a full, fresh scan of this folder rather
        /// than risking a stale result.</summary>
        private bool TryReuseCachedResult(string folder, int mode, out FolderResult? fr)
        {
            fr = null;
            FolderScanCacheEntry? entry;
            lock (_scanCache)
            {
                if (!_scanCache.Entries.TryGetValue(folder, out entry)) return false;
            }
            if (entry.IconModeIndexUsed != mode) return false;

            long folderMTime;
            try { folderMTime = Directory.GetLastWriteTimeUtc(folder).Ticks; }
            catch { return false; }
            if (folderMTime != entry.FolderMTimeTicks) return false;

            string desktopIni = Path.Combine(folder, "desktop.ini");
            long? desktopIniMTime = null;
            if (File.Exists(desktopIni))
            {
                try { desktopIniMTime = File.GetLastWriteTimeUtc(desktopIni).Ticks; }
                catch { return false; }
            }
            if (desktopIniMTime != entry.DesktopIniMTimeTicks) return false;

            long? selectedIconMTime = null;
            if (entry.SelectedIcon != null)
            {
                if (!File.Exists(entry.SelectedIcon)) return false;
                try { selectedIconMTime = File.GetLastWriteTimeUtc(entry.SelectedIcon).Ticks; }
                catch { return false; }
            }
            if (selectedIconMTime != entry.SelectedIconMTimeTicks) return false;

            fr = new FolderResult
            {
                FolderPath = folder,
                IconFiles = entry.IconFiles,
                SelectedIcon = entry.SelectedIcon,
                Status = "Pending",
                HasSubfolders = entry.HasSubfolders,
                HasUnconvertedImage = entry.HasUnconvertedImage,
                CustomName = entry.CustomName,
                IconState = entry.IconState
            };
            return true;
        }

        private void StoreCacheEntry(string folder, int mode, List<string> icoFiles, string? selected,
            bool hasSub, bool hasUnconvertedImage, string? customName, string iconState)
        {
            long folderMTime;
            try { folderMTime = Directory.GetLastWriteTimeUtc(folder).Ticks; }
            catch { return; } // can't even read the folder's own mtime - don't cache a possibly-wrong entry

            string desktopIni = Path.Combine(folder, "desktop.ini");
            long? desktopIniMTime = null;
            if (File.Exists(desktopIni))
            {
                try { desktopIniMTime = File.GetLastWriteTimeUtc(desktopIni).Ticks; } catch { }
            }

            long? selectedIconMTime = null;
            if (selected != null)
            {
                try { selectedIconMTime = File.GetLastWriteTimeUtc(selected).Ticks; } catch { }
            }

            var entry = new FolderScanCacheEntry
            {
                FolderMTimeTicks = folderMTime,
                DesktopIniMTimeTicks = desktopIniMTime,
                SelectedIconMTimeTicks = selectedIconMTime,
                IconModeIndexUsed = mode,
                IconFiles = icoFiles,
                SelectedIcon = selected,
                HasSubfolders = hasSub,
                HasUnconvertedImage = hasUnconvertedImage,
                CustomName = customName,
                IconState = iconState
            };

            lock (_scanCache)
            {
                _scanCache.Entries[folder] = entry;
            }
        }

        /// <summary>How many currently-scanned folders Apply will actually touch - the same
        /// definition btnApply_Click itself uses, so the header text and the confirmation dialog
        /// never disagree. Independent of any gallery filter/search/view-mode narrowing, since
        /// Apply always acts on the full scanned set.</summary>
        private int CountFoldersThatWillChange() =>
            _allFolders.Values.Where(f => f.IconFiles.Count > 0).Count(f => f.IconState != "Up to date");

        /// <summary>Shows a message in the gallery area explaining why it's empty, rather than
        /// just leaving it blank - distinguishes "haven't scanned yet" from "filter/search hid
        /// everything" from "genuinely nothing needs changing".</summary>
        private void UpdateGalleryEmptyState()
        {
            int visibleCount = folderResultsView != null ? folderResultsView.Cast<object>().Count() : FolderResults.Count;
            if (visibleCount > 0)
            {
                txtGalleryEmpty.Visibility = Visibility.Collapsed;
                return;
            }

            if (_allFolders.Count == 0)
            {
                txtGalleryEmpty.Text = "Pick a folder to get started";
            }
            else if (_activeCategoryFilter != null || _filterMultipleIconsOnly || !string.IsNullOrEmpty(txtSearch.Text))
            {
                txtGalleryEmpty.Text = "No folders match the current filter or search. Try a different chip, or clear the search box above.";
            }
            else
            {
                txtGalleryEmpty.Text = "No folders need changes here - they're all already up to date. Turn on \"Show up-to-date folders\" below to see them anyway.";
            }
            txtGalleryEmpty.Visibility = Visibility.Visible;
        }

        /// <summary>Populates the gallery for the "Show all folders" flat view. Adds items in
        /// batches with a yield in between rather than all at once - for a folder with thousands
        /// of matches, WPF creating/laying out that many tiles is real UI-thread work that can't
        /// be moved to a background thread (only the UI thread can touch visual elements), so this
        /// doesn't reduce the total time, but it keeps the window responsive and repainting
        /// progressively instead of going stiff for the whole duration.</summary>
        private async Task RefreshFlatViewAsync()
        {
            int myGeneration = ++_galleryRefreshGeneration;

            FolderResults.Clear();
            txtGalleryEmpty.Visibility = Visibility.Collapsed; // clear any stale message from before this call - re-shown at the end only if there really is nothing
            var toShow = _allFolders.Values
                .Where(f => (f.IconFiles.Count > 0 || _activeCategoryFilter != null) && ShouldDisplayInGallery(f))
                .OrderBy(f => Path.GetFileName(f.FolderPath.TrimEnd(Path.DirectorySeparatorChar)), StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.FolderPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int i = 0;
            foreach (var fr in toShow)
            {
                if (myGeneration != _galleryRefreshGeneration) return; // a newer call has since started - abandon this one

                FolderResults.Add(fr);
                if (++i % 250 == 0)
                {
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                    if (myGeneration != _galleryRefreshGeneration) return;
                }
            }

            if (myGeneration != _galleryRefreshGeneration) return;
            RefreshPreviewCount();
            UpdateGalleryEmptyState();
        }

        /// <summary>Which category filter chip a folder belongs to - matches the Tag values used
        /// on the chips in XAML (chipWithIco/chipWithNonIco/chipWithBoth/chipWithoutIcon).</summary>
        private static string FolderCategory(FolderResult fr)
        {
            bool hasIco = fr.IconFiles.Count > 0;
            bool hasImg = fr.HasUnconvertedImage;
            if (hasIco && hasImg) return "WithBoth";
            if (hasIco) return "WithIco";
            if (hasImg) return "WithNonIco";
            return "WithoutIcon";
        }

        /// <summary>Decides whether a folder belongs in the gallery view right now.
        /// - A category filter chip being active overrides the New/Changed/up-to-date rules below:
        ///   only folders in that exact category show, regardless of icon state.
        /// - "Has multiple icons" being checked is a separate, independently-combined filter (AND,
        ///   not OR/override) - it narrows whatever the category filter and state rules already
        ///   allow down to just folders with more than one icon candidate.
        /// - Folders with no icon at all are navigation-only placeholders (Explorer mode) and
        ///   always show, since the state toggle doesn't apply to them.
        /// - "Show up-to-date folders too" being on shows everything, unconditionally.
        /// - Otherwise, only folders Apply will actually change (New/Changed) are shown.</summary>
        private bool ShouldDisplayInGallery(FolderResult fr)
        {
            if (_activeCategoryFilter != null && FolderCategory(fr) != _activeCategoryFilter) return false;
            if (_filterMultipleIconsOnly && !fr.HasMultipleIcons) return false;
            if (_activeCategoryFilter != null || _filterMultipleIconsOnly) return true;

            if (fr.IconFiles.Count == 0) return true;
            if (chkShowUpToDate.IsChecked == true) return true;
            return fr.IconState != "Up to date";
        }

        /// <summary>Reads both the currently-referenced icon path and this app's own
        /// "applied at" marker out of a folder's desktop.ini, if present.</summary>
        private static (string? iconPath, long? appliedTicks) ReadDesktopIniIconInfo(string folder)
        {
            string desktopIni = Path.Combine(folder, "desktop.ini");
            if (!File.Exists(desktopIni)) return (null, null);

            string? iconPath = null;
            long? ticks = null;
            try
            {
                foreach (var line in File.ReadAllLines(desktopIni, Encoding.Default))
                {
                    var t = line.Trim();
                    if (t.StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase))
                    {
                        string rel = t.Substring("IconResource=".Length).Split(',')[0];
                        iconPath = Path.IsPathRooted(rel) ? rel : Path.Combine(folder, rel);
                    }
                    else if (t.StartsWith("IconSetterAppliedTimeUtc=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (long.TryParse(t.Substring("IconSetterAppliedTimeUtc=".Length), out long parsed))
                            ticks = parsed;
                    }
                }
            }
            catch { }
            return (iconPath, ticks);
        }

        /// <summary>
        /// "New" - no desktop.ini/IconResource yet.
        /// "Up to date" - already pointing at exactly this icon file, and that file hasn't been
        /// modified since it was applied.
        /// "Changed" - either pointing at a different icon than what's currently selected, or the
        /// icon file was replaced/edited since it was last applied (or this desktop.ini predates
        /// this feature and has no record to compare against).
        /// </summary>
        private static string DetermineIconState(string folder, string selectedIcon)
        {
            var (existingIcon, appliedTicks) = ReadDesktopIniIconInfo(folder);
            if (existingIcon == null) return "New";

            bool pathMatches = string.Equals(existingIcon, selectedIcon, StringComparison.OrdinalIgnoreCase);
            if (!pathMatches) return "Changed";

            if (!appliedTicks.HasValue) return "Changed"; // pre-existing desktop.ini with no marker to compare

            try
            {
                long currentTicks = File.GetLastWriteTimeUtc(selectedIcon).Ticks;
                return currentTicks == appliedTicks.Value ? "Up to date" : "Changed";
            }
            catch
            {
                return "Changed";
            }
        }

        // ===================== Explorer-style browsing =====================

        private async void ViewMode_Changed(object sender, RoutedEventArgs e)
        {
            bool showAll = rbShowAllFolders.IsChecked == true;
            _explorerMode = !showAll;
            breadcrumbRow.Visibility = _explorerMode ? Visibility.Visible : Visibility.Collapsed;

            if (_explorerMode)
            {
                _explorerRoot = txtRoot.Text.TrimEnd(Path.DirectorySeparatorChar);
                await PopulateExplorerLevelAsync(_explorerRoot);
            }
            else
            {
                await RefreshFlatViewAsync();
            }
        }

        private async void ChkShowUpToDate_Changed(object sender, RoutedEventArgs e)
        {
            // Purely re-filters whatever's currently on screen - doesn't change which mode
            // (flat vs Explorer-style) is active.
            if (_explorerMode) await PopulateExplorerLevelAsync(_explorerCurrent);
            else await RefreshFlatViewAsync();
        }

        /// <summary>"Has multiple icons" is an independent filter, not part of the mutually
        /// exclusive category chip group - a folder can be "With .ico" AND have multiple icons at
        /// once, so this combines with the active category filter (if any) via AND rather than
        /// replacing it.</summary>
        private async void ChkMultipleIconsFilter_Changed(object sender, RoutedEventArgs e)
        {
            _filterMultipleIconsOnly = chipMultipleIcons.IsChecked == true;
            if (_explorerMode) await PopulateExplorerLevelAsync(_explorerCurrent);
            else await RefreshFlatViewAsync();
        }

        /// <summary>Category filter chips (With .ico / Needs conversion / Has both / No icon yet)
        /// are mutually exclusive despite being CheckBoxes rather than RadioButtons - a
        /// ToggleChipStyle look doesn't exist for radio buttons, so exclusivity is enforced here
        /// instead. _updatingCategoryChips guards against the recursive Unchecked events firing
        /// while this handler is itself unchecking the other chips.</summary>
        private async void CategoryChip_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingCategoryChips) return;
            if (sender is not System.Windows.Controls.CheckBox chip) return;

            _updatingCategoryChips = true;
            try
            {
                foreach (var other in new[] { chipWithIco, chipWithNonIco, chipWithBoth, chipWithoutIcon })
                {
                    if (other != chip) other.IsChecked = false;
                }

                _activeCategoryFilter = chip.IsChecked == true ? chip.Tag as string : null;
            }
            finally
            {
                _updatingCategoryChips = false;
            }

            if (_explorerMode) await PopulateExplorerLevelAsync(_explorerCurrent);
            else await RefreshFlatViewAsync();
        }

        /// <summary>Shows only the immediate subfolders of <paramref name="folder"/>, reusing the
        /// same FolderResult instances as the master set so any icon choice made here (prev/next/
        /// shuffle) is the same choice Apply will see later. Batched the same way as
        /// RefreshFlatViewAsync, for the (less common but possible) case of a single folder having
        /// thousands of direct subfolders.</summary>
        private async Task PopulateExplorerLevelAsync(string folder)
        {
            int myGeneration = ++_galleryRefreshGeneration;

            FolderResults.Clear();
            txtGalleryEmpty.Visibility = Visibility.Collapsed; // clear any stale message from before this call - re-shown at the end only if there really is nothing
            _explorerCurrent = folder;

            IEnumerable<string> subfolders;
            try
            {
                subfolders = Directory.GetDirectories(folder, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                subfolders = Enumerable.Empty<string>();
            }

            int i = 0;
            foreach (var sub in subfolders)
            {
                if (myGeneration != _galleryRefreshGeneration) return; // a newer call has since started - abandon this one

                if (_allFolders.TryGetValue(sub, out var fr) && ShouldDisplayInGallery(fr))
                {
                    FolderResults.Add(fr);
                    if (++i % 250 == 0)
                    {
                        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                        if (myGeneration != _galleryRefreshGeneration) return;
                    }
                }
            }

            if (myGeneration != _galleryRefreshGeneration) return;
            RefreshPreviewCount();
            UpdateGalleryEmptyState();
            UpdateBreadcrumb();
        }

        private void UpdateBreadcrumb()
        {
            breadcrumbBar.Children.Clear();

            string rootName = Path.GetFileName(_explorerRoot);
            if (string.IsNullOrEmpty(rootName)) rootName = _explorerRoot;

            var segments = new List<(string Label, string Path)> { (rootName, _explorerRoot) };

            if (_explorerCurrent.Length > _explorerRoot.Length)
            {
                string relative = _explorerCurrent.Substring(_explorerRoot.Length).Trim(Path.DirectorySeparatorChar);
                string acc = _explorerRoot;
                foreach (var part in relative.Split(Path.DirectorySeparatorChar))
                {
                    acc = Path.Combine(acc, part);
                    segments.Add((part, acc));
                }
            }

            for (int i = 0; i < segments.Count; i++)
            {
                bool isLast = i == segments.Count - 1;
                var tb = new System.Windows.Controls.TextBlock
                {
                    Text = segments[i].Label,
                    Tag = segments[i].Path,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = isLast ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = isLast
                        ? (System.Windows.Media.Brush)FindResource("TextPrimaryBrush")
                        : (System.Windows.Media.Brush)FindResource("AccentBrush"),
                    Cursor = isLast ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand
                };
                if (!isLast)
                {
                    tb.TextDecorations = System.Windows.TextDecorations.Underline;
                    tb.MouseLeftButtonUp += Breadcrumb_Click;
                }
                breadcrumbBar.Children.Add(tb);

                if (!isLast)
                {
                    breadcrumbBar.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text = " › ",
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
                    });
                }
            }

            btnUpLevel.IsEnabled = !string.Equals(
                _explorerCurrent.TrimEnd(Path.DirectorySeparatorChar),
                _explorerRoot.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        private async void Breadcrumb_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.TextBlock tb && tb.Tag is string path)
                await PopulateExplorerLevelAsync(path);
        }

        private async void btnUpLevel_Click(object sender, RoutedEventArgs e)
        {
            string? parent = Path.GetDirectoryName(_explorerCurrent.TrimEnd(Path.DirectorySeparatorChar));
            if (parent != null && _explorerCurrent.Length > _explorerRoot.Length)
                await PopulateExplorerLevelAsync(parent);
        }

        private async void FolderTile_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_explorerMode) return;
            if (sender is FrameworkElement fe && fe.Tag is FolderResult fr && fr.HasSubfolders)
                await PopulateExplorerLevelAsync(fr.FolderPath);
        }

        private static string? ReadLocalizedNameFromDesktopIni(string folder)
        {
            string desktopIni = Path.Combine(folder, "desktop.ini");
            if (!File.Exists(desktopIni)) return null;
            try
            {
                foreach (var line in File.ReadAllLines(desktopIni, Encoding.Default))
                {
                    if (line.Trim().StartsWith("LocalizedResourceName=", StringComparison.OrdinalIgnoreCase))
                    {
                        string value = line.Substring(line.IndexOf('=') + 1).Trim();
                        if (!value.StartsWith("@")) return value;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string? ReadCurrentIconResourceFromDesktopIni(string folder)
        {
            string desktopIni = Path.Combine(folder, "desktop.ini");
            if (!File.Exists(desktopIni)) return null;
            try
            {
                foreach (var line in File.ReadAllLines(desktopIni, Encoding.Default))
                {
                    var t = line.Trim();
                    if (t.StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase))
                    {
                        string rel = t.Substring("IconResource=".Length).Split(',')[0];
                        return Path.IsPathRooted(rel) ? rel : Path.Combine(folder, rel);
                    }
                }
            }
            catch { }
            return null;
        }

        private void btnContinueToPreview_Click(object sender, RoutedEventArgs e)
        {
            warningContainer.Visibility = Visibility.Collapsed;
            btnContinueToPreview.Visibility = Visibility.Collapsed;
            btnSkipImage.Visibility = Visibility.Collapsed;
            btnSkipAllSmallImages.Visibility = Visibility.Collapsed;
            loadingWarning.Text = "";
            ResetLoadingIconPreview();
            _smallImagePrompt?.TrySetResult(true); // "convert anyway" - resumes ConvertImagesAsync's loop
        }

        private void btnSkipImage_Click(object sender, RoutedEventArgs e)
        {
            warningContainer.Visibility = Visibility.Collapsed;
            btnContinueToPreview.Visibility = Visibility.Collapsed;
            btnSkipImage.Visibility = Visibility.Collapsed;
            btnSkipAllSmallImages.Visibility = Visibility.Collapsed;
            loadingWarning.Text = "";
            ResetLoadingIconPreview();
            _smallImagePrompt?.TrySetResult(false); // skip just this image - resumes the loop on the next one
        }

        private void btnSkipAllSmallImages_Click(object sender, RoutedEventArgs e)
        {
            warningContainer.Visibility = Visibility.Collapsed;
            btnContinueToPreview.Visibility = Visibility.Collapsed;
            btnSkipImage.Visibility = Visibility.Collapsed;
            btnSkipAllSmallImages.Visibility = Visibility.Collapsed;
            loadingWarning.Text = "";
            ResetLoadingIconPreview();
            _skipAllSmallImagesThisSession = true; // no more prompts for the rest of this app session
            _smallImagePrompt?.TrySetResult(false); // skip this one too - resumes the loop on the next image
        }

        /// <summary>Reverts the loading overlay's image preview back to the plain spinner. Resuming
        /// an awaited Task isn't instant - it goes through the dispatcher's message queue - so
        /// without this, there's a visible gap where the overlay still shows the exact same image
        /// (with "Converting…" underneath it) even after the user's already moved past it, making
        /// Skip/Convert-anyway look like they didn't do anything.</summary>
        private void ResetLoadingIconPreview()
        {
            loadingCurrentIcon.Source = null;
            loadingCurrentIcon.Visibility = Visibility.Collapsed;
            loadingSpinner.Visibility = Visibility.Visible;
        }

        private void btnCancelConversion_Click(object sender, RoutedEventArgs e)
        {
            _cancelRequested = true;
            loadingText.Text = "Cancelling…";
            warningContainer.Visibility = Visibility.Collapsed;
            btnContinueToPreview.Visibility = Visibility.Collapsed;
            btnSkipImage.Visibility = Visibility.Collapsed;
            btnSkipAllSmallImages.Visibility = Visibility.Collapsed;
            loadingWarning.Text = "";
            _smallImagePrompt?.TrySetResult(false); // unblock the await if a per-image prompt is pending
            ResetToOptions();
        }

        private void ResetToOptions()
        {
            loadingProgress.Value = 0;
            loadingCurrentIcon.Source = null;
            loadingCurrentIcon.Visibility = Visibility.Collapsed;
            loadingSpinner.Visibility = Visibility.Visible;
            loadingPanel.Visibility = Visibility.Collapsed;
        }

        // ===================== Gallery interaction =====================

        private void Shuffle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is FolderResult fr && fr.HasMultipleIcons)
            {
                fr.SelectedIcon = fr.IconFiles[rnd.Next(fr.IconFiles.Count)];
                fr.IconState = DetermineIconState(fr.FolderPath, fr.SelectedIcon!);
                RefreshPreviewCount();
            }
        }

        private void PrevIcon_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is FolderResult fr && fr.HasMultipleIcons)
            {
                int idx = fr.IconFiles.IndexOf(fr.SelectedIcon!);
                if (idx <= 0) idx = fr.IconFiles.Count;
                fr.SelectedIcon = fr.IconFiles[idx - 1];
                fr.IconState = DetermineIconState(fr.FolderPath, fr.SelectedIcon!);
                RefreshPreviewCount();
            }
        }

        private void NextIcon_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is FolderResult fr && fr.HasMultipleIcons)
            {
                int idx = fr.IconFiles.IndexOf(fr.SelectedIcon!);
                if (idx < 0 || idx >= fr.IconFiles.Count - 1) idx = -1;
                fr.SelectedIcon = fr.IconFiles[idx + 1];
                fr.IconState = DetermineIconState(fr.FolderPath, fr.SelectedIcon!);
                RefreshPreviewCount();
            }
        }

        /// <summary>Updates just the will-change count text, without touching which folders are
        /// currently in the gallery - used after actions (picking a different icon) that change a
        /// folder's IconState without going through RefreshFlatView/PopulateExplorerLevel.</summary>
        private void RefreshPreviewCount()
        {
            int willChange = CountFoldersThatWillChange();
            txtPreviewCount.Text = willChange == 1 ? "1 folder will be changed" : $"{willChange} folders will be changed";
        }

        // ===================== Apply / Remove =====================

        private async void btnApply_Click(object sender, RoutedEventArgs e)
        {
            // Always the full scanned set, regardless of whether Explorer-style browsing is
            // currently narrowing what's shown in the gallery - that toggle is purely visual.
            var toProcess = _allFolders.Values.Where(f => f.IconFiles.Count > 0).ToList();
            int willChange = CountFoldersThatWillChange();

            if (willChange == 0)
            {
                WpfMessageBox.Show("Every scanned folder is already up to date - nothing to apply.",
                    "Icon Setter", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = WpfMessageBox.Show(
                $"This will set a custom icon on {willChange} folder{(willChange == 1 ? "" : "s")}, " +
                "including any that aren't currently visible (outside the current filter, search, or Explorer-style level). Continue?",
                "Apply icons", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            ResultLog.Clear();
            previewPanel.Visibility = Visibility.Collapsed;
            resultsPanel.Visibility = Visibility.Visible;
            txtSummary.Text = "Applying icons…";
            delayNoticeBanner.Visibility = Visibility.Collapsed; // clear any notice left over from a previous run
            lblResultsCol1.Text = "Applied:";
            lblResultsCol3.Visibility = Visibility.Visible;
            txtSkipped.Visibility = Visibility.Visible;
            btnUndo.Visibility = Visibility.Collapsed; // hide any leftover undo from a previous run until this one finishes
            _lastApplied.Clear();

            int applied = 0, alreadyUpToDate = 0, errors = 0, reCustomized = 0;

            foreach (var fr in toProcess)
            {
                if (fr.IconState == "Up to date")
                {
                    alreadyUpToDate++;
                    continue;
                }

                bool wasReCustomization = fr.IconState == "Changed";

                fr.Status = "Processing…";
                ResultLog.Add(fr);
                await Task.Delay(30);

                try
                {
                    var snapshot = CaptureFolderIconSnapshot(fr.FolderPath);
                    ApplyCustomIcon(fr);
                    applied++;
                    if (wasReCustomization) reCustomized++;
                    _lastApplied.Add((fr, snapshot));
                }
                catch (Exception ex)
                {
                    fr.Status = "Error: " + ex.Message;
                    errors++;
                }
            }

            txtSummary.Text = "Apply complete! If your new icons haven't appeared, click \"Refresh Explorer\" or press F5!";
            txtApplied.Text = applied.ToString();
            txtErrors.Text = errors.ToString();
            txtSkipped.Text = alreadyUpToDate.ToString();
            btnUndo.Visibility = _lastApplied.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Brand-new folders (no prior custom icon) redraw instantly via the targeted
            // per-folder notification in ApplyCustomIcon. A folder that already had a *different*
            // icon can be left showing the old one for a bit - Explorer appears to hold onto a
            // cached icon handle for that specific item that no notification reliably evicts
            // right away. Flag that here so it doesn't look like the apply silently failed.
            if (reCustomized > 0)
            {
                txtDelayNotice.Text = reCustomized == 1
                    ? "1 folder already had an icon applied, it may take a few minutes before refreshing will show the new icon."
                    : $"{reCustomized} folders already had icons applied, it may take a few minutes before refreshing will show the new icon.";
                delayNoticeBanner.Visibility = Visibility.Visible;
            }
            else
            {
                delayNoticeBanner.Visibility = Visibility.Collapsed;
            }

            // Global flush, on top of the per-folder targeted notifications already sent in
            // ApplyCustomIcon. This is what nudges the "already had a different icon" case (see
            // the delay-notice above) - it doesn't fully solve it, but it's the best trigger
            // available short of the manual "Force Explorer refresh" button.
            if (applied > 0)
                ShellNotify.ForceGlobalRefresh();
        }

        private async void btnUndo_Click(object sender, RoutedEventArgs e)
        {
            if (_lastApplied.Count == 0) return;

            var toUndo = _lastApplied.ToList();
            _lastApplied.Clear();
            btnUndo.Visibility = Visibility.Collapsed;

            txtSummary.Text = "Undoing…";
            delayNoticeBanner.Visibility = Visibility.Collapsed;
            lblResultsCol1.Text = "Reverted:";
            lblResultsCol3.Visibility = Visibility.Collapsed;
            txtSkipped.Visibility = Visibility.Collapsed;

            int reverted = 0, errors = 0;

            foreach (var (fr, snapshot) in toUndo)
            {
                fr.Status = "Processing…";
                await Task.Delay(30);

                try
                {
                    RevertFolderIcon(fr, snapshot);
                    reverted++;
                }
                catch (Exception ex)
                {
                    fr.Status = "Error: " + ex.Message;
                    errors++;
                }
            }

            txtSummary.Text = "Undo complete";
            txtApplied.Text = reverted.ToString();
            txtErrors.Text = errors.ToString();

            if (reverted > 0)
                ShellNotify.ForceGlobalRefresh();
        }

        /// <summary>Restores a folder to exactly the state <paramref name="snapshot"/> captured
        /// right before Apply touched it - the previous desktop.ini byte-for-byte (whoever wrote
        /// it) if there was one, or no desktop.ini at all if the folder was untouched before.
        /// Undoes precisely what Apply did, rather than always resetting to the Windows default.</summary>
        private void RevertFolderIcon(FolderResult fr, FolderIconSnapshot snapshot)
        {
            string folder = fr.FolderPath;
            string desktopIni = Path.Combine(folder, "desktop.ini");

            if (File.Exists(desktopIni))
                File.SetAttributes(desktopIni, FileAttributes.Normal);

            if (snapshot.HadDesktopIni)
            {
                File.WriteAllBytes(desktopIni, snapshot.DesktopIniRawBytes!);
                File.SetAttributes(desktopIni, snapshot.DesktopIniAttributes);
            }
            else if (File.Exists(desktopIni))
            {
                File.Delete(desktopIni);
            }

            File.SetAttributes(folder, snapshot.FolderAttributesBefore);

            // Same targeted notification Apply uses, for the same reason - makes this folder
            // redraw with its restored icon (or lack of one) right away instead of waiting on a
            // global refresh.
            ShellNotify.NotifyItemUpdated(folder);

            fr.Status = "Reverted";
            fr.IconState = DetermineIconState(folder, fr.SelectedIcon!);
        }

        private void ApplyCustomIcon(FolderResult fr)
        {
            string folder = fr.FolderPath;
            string desktopIni = Path.Combine(folder, "desktop.ini");
            string icoFilePath = fr.SelectedIcon!;
            string icoFileName = Path.GetFileName(icoFilePath);

            // This is the key branch: the shell API only gets a folder that's never had a custom
            // icon to redraw instantly. A folder that already had one - even a different one -
            // doesn't refresh any faster through the API than through hand-writing desktop.ini;
            // both need Explorer to notice on its own, sometimes after a real delay. So new
            // folders get the API path, re-customizations get the direct-write path that's
            // already proven to eventually catch up without restarting Explorer. (Two variations
            // of also calling the API for re-customizations were tried and reverted - neither
            // reliably helped, and the second one coincided with new folders seeming to regress
            // too, which points at Explorer's own notification handling being inconsistent from
            // one test session to the next rather than anything specific to that code path.)
            bool hadDesktopIniBefore = File.Exists(desktopIni);

            string? localizedNameLine = null;
            if (hadDesktopIniBefore)
            {
                try
                {
                    foreach (var line in File.ReadAllLines(desktopIni, Encoding.Default))
                    {
                        if (line.Trim().StartsWith("LocalizedResourceName=", StringComparison.OrdinalIgnoreCase))
                        {
                            localizedNameLine = line.Trim();
                            break;
                        }
                    }
                }
                catch { }

                try { File.SetAttributes(desktopIni, FileAttributes.Normal); } catch { }
            }

            // Recording the icon file's own timestamp is what powers the New/Changed/Up-to-date
            // detection later (DetermineIconState) - not a refresh trick, just bookkeeping.
            try { File.SetLastWriteTimeUtc(icoFilePath, DateTime.UtcNow); } catch { }
            long appliedTicks = File.GetLastWriteTimeUtc(icoFilePath).Ticks;

            if (!hadDesktopIniBefore)
            {
                // Brand-new folder: register through the official shell API - this is what gets
                // Explorer to redraw promptly on a plain refresh (see FolderCustomSettings). It
                // creates desktop.ini itself as part of registering the setting.
                FolderCustomSettings.SetFolderIcon(folder, icoFileName);

                // Layer this app's own bookkeeping onto the desktop.ini the API just wrote,
                // without touching the IconResource line it set.
                try { File.SetAttributes(desktopIni, FileAttributes.Normal); } catch { }
                var extraLines = new List<string> { "ConfirmFileOp=0", $"IconSetterAppliedTimeUtc={appliedTicks}" };
                File.AppendAllLines(desktopIni, extraLines, new UTF8Encoding(false));
            }
            else
            {
                // Re-customization: write desktop.ini directly instead of going through the shell
                // API, since the API doesn't refresh this case any faster in practice.
                var lines = new List<string>
                {
                    "[.ShellClassInfo]",
                    $"IconResource={icoFileName},0",
                    "ConfirmFileOp=0",
                    $"IconSetterAppliedTimeUtc={appliedTicks}"
                };
                if (!string.IsNullOrEmpty(localizedNameLine)) lines.Add(localizedNameLine);

                File.WriteAllLines(desktopIni, lines, new UTF8Encoding(true));
            }

            File.SetAttributes(desktopIni, FileAttributes.Hidden | FileAttributes.System);

            var fAttrs = File.GetAttributes(folder);
            File.SetAttributes(folder, fAttrs | FileAttributes.ReadOnly | FileAttributes.System);

            // The shell API call above (brand-new folder case) has an undocumented Windows side
            // effect of hiding whichever .ico file it's given - the same thing Explorer's own
            // "Change Icon" dialog does. That's asserted explicitly here too, so re-customizations
            // (which never call that API) get the same treatment. This only ever adds the Hidden
            // attribute to the newly-selected icon - it deliberately never removes Hidden from any
            // other .ico file, including one this app hid during an earlier apply with a different
            // selection: once a file is hidden, it stays hidden, regardless of why. Skipped
            // entirely when the user's turned this off in Options.
            if (_hideIcoAfterApply)
            {
                try
                {
                    var icoAttrs = File.GetAttributes(icoFilePath);
                    if ((icoAttrs & FileAttributes.Hidden) == 0)
                        File.SetAttributes(icoFilePath, icoAttrs | FileAttributes.Hidden);
                }
                catch { }
            }

            // Extra nudge either way - doesn't help the re-customization delay much, but doesn't
            // hurt, and it's still what makes Undo/Revert's own writes redraw promptly.
            ShellNotify.NotifyItemUpdated(folder);

            fr.Status = "Applied";
            fr.IconState = "Up to date";
        }

        private void btnForceRefresh_Click(object sender, RoutedEventArgs e)
        {
            ShellNotify.ForceGlobalRefresh();
            WpfMessageBox.Show(
                "Sent a full icon-cache refresh to Explorer. This can take a few seconds and may briefly flicker taskbar/desktop icons.",
                "Icon Setter", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void btnRestartExplorer_Click(object sender, RoutedEventArgs e)
        {
            var confirm = WpfMessageBox.Show(
                "This will close every open Explorer window (including the taskbar and desktop icons briefly disappearing) and reopen it. " +
                "Icon Setter will try to reopen the folder windows you currently have open, but any unsaved Explorer state - open tabs " +
                "within a window, selections, scroll position, or the order the windows were in - won't be preserved. Continue?",
                "Restart Explorer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            btnRestartExplorer.IsEnabled = false;
            btnForceRefresh.IsEnabled = false;
            btnRestartExplorerResults.IsEnabled = false;
            btnForceRefreshResults.IsEnabled = false;
            try
            {
                int reopened = await ExplorerRestarter.RestartAndReopenAsync();
                WpfMessageBox.Show(
                    reopened > 0
                        ? $"Explorer has been restarted, and {reopened} folder window{(reopened == 1 ? "" : "s")} reopened."
                        : "Explorer has been restarted.",
                    "Icon Setter", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                btnRestartExplorer.IsEnabled = true;
                btnForceRefresh.IsEnabled = true;
                btnRestartExplorerResults.IsEnabled = true;
                btnForceRefreshResults.IsEnabled = true;
            }
        }

        private void btnExportLog_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "CSV file (*.csv)|*.csv",
                FileName = $"IconSetter_Log_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Folder,Status,IconFile");
                foreach (var fr in ResultLog)
                {
                    sb.AppendLine(string.Join(",",
                        CsvEscape(fr.FolderPath), CsvEscape(fr.Status), CsvEscape(fr.SelectedIcon ?? "")));
                }
                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show("Couldn't save the log: " + ex.Message);
            }
        }

        private static string CsvEscape(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private void btnBackFromResults_Click(object sender, RoutedEventArgs e)
        {
            resultsPanel.Visibility = Visibility.Collapsed;
            previewPanel.Visibility = Visibility.Visible;
        }

        private void TxtSearch_GotFocus(object sender, RoutedEventArgs e)
        {
            txtSearchPlaceholder.Visibility = Visibility.Collapsed;
        }

        private void TxtSearch_LostFocus(object sender, RoutedEventArgs e)
        {
            txtSearchPlaceholder.Visibility = string.IsNullOrEmpty(txtSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void txtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string filter = txtSearch.Text?.Trim() ?? string.Empty;
            folderResultsView!.Filter = string.IsNullOrEmpty(filter)
                ? null
                : obj => obj is FolderResult fr && fr.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
            folderResultsView.Refresh();
            UpdateGalleryEmptyState();
        }

        // ===================== Stats panel =====================

        private struct FolderStatsCounts
        {
            public int WithIco, WithNonIco, WithBoth, WithoutIcons, MultipleIcons;
        }

        /// <summary>Pure computation, safe to run on a background thread - touches only the file
        /// system, no UI elements. Uses the shared snapshot when one's provided (the normal case,
        /// from ScanAndBuildGalleryAsync); builds its own otherwise (the standalone quick-peek from
        /// txtRoot_KeyDown, which doesn't have one to share).</summary>
        private static FolderStatsCounts ComputeFolderStats(List<string> folders, Dictionary<string, FolderFileSnapshot>? snapshot = null)
        {
            snapshot ??= SnapshotFolders(folders);
            var counts = new FolderStatsCounts();

            foreach (var folder in folders)
            {
                if (!snapshot.TryGetValue(folder, out var fileInfo)) continue;
                bool hasIco = fileInfo.IcoFiles.Count > 0;
                bool hasImg = fileInfo.HasUnconvertedImage;

                if (hasIco && hasImg) counts.WithBoth++;
                else if (hasIco) counts.WithIco++;
                else if (hasImg) counts.WithNonIco++;
                else counts.WithoutIcons++;

                if (fileInfo.IcoFiles.Count > 1) counts.MultipleIcons++;
            }

            return counts;
        }

        /// <summary>Runs the folder-stats computation on a background thread (a large tree can
        /// mean thousands of Directory.GetFiles calls, which would otherwise freeze the UI thread
        /// and trigger a Windows "Not Responding" prompt), then applies the results to the chips
        /// and count text back on the UI thread.</summary>
        private async Task UpdateFolderStatsAsync(List<string> folders, Dictionary<string, FolderFileSnapshot>? snapshot = null)
        {
            var counts = await Task.Run(() => ComputeFolderStats(folders, snapshot));

            chipWithIco.Content = $"Has .ico ({counts.WithIco})";
            chipWithNonIco.Content = $"Has \"icon\" image ({counts.WithNonIco})";
            chipWithBoth.Content = $"Has both ({counts.WithBoth})";
            chipWithoutIcon.Content = $"No .ico ({counts.WithoutIcons})";
            chipMultipleIcons.Content = $"Has multiple .ico ({counts.MultipleIcons})";

            // Overwritten by RefreshFlatView/PopulateExplorerLevel once the actual scan runs -
            // this is just an immediate "you picked a folder" acknowledgment before that happens.
            txtPreviewCount.Text = folders.Count == 0
                ? "No folders detected"
                : $"{folders.Count} folder(s) detected";
        }

        // ===================== Filename helper =====================

        private string GetNextIconPath(string basePath)
        {
            string dir = Path.GetDirectoryName(basePath)!;
            string name = Path.GetFileNameWithoutExtension(basePath);
            string ext = Path.GetExtension(basePath);

            var files = Directory.EnumerateFiles(dir, name + "*.ico", SearchOption.TopDirectoryOnly)
                .Where(f => (File.GetAttributes(f) & FileAttributes.System) == 0)
                .Select(Path.GetFileNameWithoutExtension);

            var used = new HashSet<int>();
            int spaceCount = 0, noSpaceCount = 0;

            foreach (var fname in files)
            {
                if (fname == null) continue;
                if (fname.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    used.Add(1);
                }
                else if (fname.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    string suffix = fname.Substring(name.Length);
                    if (suffix.StartsWith(" ")) { spaceCount++; suffix = suffix.TrimStart(); }
                    else if (suffix.Length > 0) { noSpaceCount++; }

                    if (int.TryParse(suffix, out int n)) used.Add(n);
                }
            }

            bool spaceStyle = spaceCount > noSpaceCount;
            int candidate = 1;
            while (used.Contains(candidate)) candidate++;

            string sep = spaceStyle ? " " : "";
            string nameSuffix = candidate == 1 ? "" : $"{sep}{candidate}";
            return Path.Combine(dir, $"{name}{nameSuffix}{ext}");
        }
    }
}
