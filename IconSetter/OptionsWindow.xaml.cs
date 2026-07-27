using System.Windows;

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

        public OptionsWindow()
        {
            InitializeComponent();
        }

        private void btnDone_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
