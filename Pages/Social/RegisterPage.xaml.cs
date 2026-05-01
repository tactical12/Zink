using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zink.Services.Social;

namespace Zink.Pages.Social
{
    public sealed partial class RegisterPage : Page
    {
        public RegisterPage()
        {
            this.InitializeComponent();
        }

        private async void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetBusy(true);
                StatusText.Text = "";

                var auth = await SocialManager.Instance.Api.RegisterAsync(
                    new Zink.Services.Social.RegisterRequest(
                        EmailBox.Text.Trim(),
                        PasswordBox.Password,
                        UsernameBox.Text.Trim(),
                        DisplayNameBox.Text.Trim()));

                await SocialManager.Instance.Realtime.ConnectAsync();

                StatusText.Text = $"Account created for {auth.Username}";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
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
