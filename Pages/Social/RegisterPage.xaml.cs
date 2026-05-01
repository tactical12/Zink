using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Zink.Pages.Social
{
    public sealed partial class RegisterPage : Page
    {
        public RegisterPage()
        {
            this.InitializeComponent();
        }

        private void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(false);
            StatusText.Text = "This is currently under development.";
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(LoginPage));
        }

        private void DeveloperSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(DeveloperSettingsPage));
        }

        private void SetBusy(bool isBusy)
        {
            BusyRing.IsActive = isBusy;
            BusyRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
