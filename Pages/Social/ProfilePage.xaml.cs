using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zink.Services.Social;

namespace Zink.Pages.Social
{
    public sealed partial class ProfilePage : Page
    {
        public ProfilePage()
        {
            this.InitializeComponent();
            Loaded += ProfilePage_Loaded;
        }

        private async void ProfilePage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var me = await SocialManager.Instance.Api.GetMeAsync();

                EmailText.Text = $"Email: {me.Email}";
                UsernameText.Text = $"Username: {me.Username}";
                DisplayNameBox.Text = me.DisplayName;
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await SocialManager.Instance.Api.UpdateProfileAsync(DisplayNameBox.Text.Trim(), null);
                StatusText.Text = "Profile updated.";
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await SocialManager.Instance.Api.LogoutAsync();
                Frame.Navigate(typeof(LoginPage));
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
        }
    }
}