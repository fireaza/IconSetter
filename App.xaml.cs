using System.Windows;
using System.Windows.Threading;

namespace IconSetter
{
    // Explicitly qualified as System.Windows.Application: this project also has
    // UseWindowsForms enabled (for the folder-browse dialog), and with both toggles on,
    // .NET's implicit global usings bring in System.Windows.Forms too - so the bare name
    // "Application" (and MessageBox/MessageBoxButton/MessageBoxImage below) is ambiguous
    // between the WPF and WinForms types unless qualified.
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Catch anything unhandled so the user gets a readable message
            // instead of the app silently vanishing.
            DispatcherUnhandledException += (s, args) =>
            {
                System.Windows.MessageBox.Show(
                    "Something went wrong:\n\n" + args.Exception.Message,
                    "Icon Setter - Unexpected error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                args.Handled = true;
            };
        }
    }
}
