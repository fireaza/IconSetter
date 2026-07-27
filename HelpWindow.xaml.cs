using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace IconSetter
{
    public partial class HelpWindow : Window
    {
        public HelpWindow()
        {
            InitializeComponent();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // If launching the default browser fails for some reason, there's nothing more
                // useful to do here than silently leave the link unclicked - the URL is still
                // visible as plain text in the window for the user to copy manually.
            }
            e.Handled = true;
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
