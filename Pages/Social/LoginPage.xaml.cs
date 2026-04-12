using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zink.Services.Social;

namespace Zink.Pages.Social
{
    public sealed partial class LoginPage : Page
    {
        public LoginPage()
        {
            this.InitializeComponent();
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetBusy(true);
                StatusText.Text = "";

                var auth = await SocialManager.Instance.Api.LoginAsync(
                    new LoginRequest(
                        EmailOrUsernameBox.Text.Trim(),
                        PasswordBox.Password));

                StatusText.Text = $"Logged in as {auth.Username}";

                try
                {
                    await SocialManager.Instance.Realtime.ConnectAsync();
                }
                catch
                {
                    StatusText.Text += " (Realtime connection failed, will retry later)";
                }
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

        private void RegisterButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(RegisterPage));
        }

        private void SetBusy(bool isBusy)
        {
            BusyRing.IsActive = isBusy;
            BusyRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}