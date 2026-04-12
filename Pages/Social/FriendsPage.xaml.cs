using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zink.Services.NativeCalling;
using Zink.Services.Social;

namespace Zink.Pages.Social
{
    public sealed partial class FriendsPage : Page
    {
        public FriendsPage()
        {
            this.InitializeComponent();
            Loaded += FriendsPage_Loaded;
        }

        private async void FriendsPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadFriendsAsync();
        }

        private async Task LoadFriendsAsync()
        {
            try
            {
                StatusText.Text = "";
                FriendsList.ItemsSource = null;
                FriendsList.ItemsSource = await SocialManager.Instance.Api.GetFriendsAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "";

                var query = SearchBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(query))
                {
                    SearchResultsList.ItemsSource = null;
                    return;
                }

                var results = await SocialManager.Instance.Api.SearchUsersAsync(query);

                SearchResultsList.ItemsSource = null;
                SearchResultsList.ItemsSource = results;
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void AddFriendButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "";

                var button = (Button)sender;
                var username = button.Tag?.ToString();

                if (string.IsNullOrWhiteSpace(username))
                    throw new Exception("Invalid username.");

                await SocialManager.Instance.Api.AddFriendByUsernameAsync(username);

                StatusText.Text = "Friend added.";

                await LoadFriendsAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void BlockButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "Block not implemented yet.";
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void RefreshFriendsButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadFriendsAsync();
        }

        private void FriendRequestsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(FriendRequestsPage));
        }

        private void ProfileButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ProfilePage));
        }

        private async void VoiceCallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "";

                var button = (Button)sender;
                var targetUserId = long.Parse(button.Tag?.ToString() ?? "0");

                if (targetUserId <= 0)
                    throw new Exception("Invalid target user.");

                await NativeCallCoordinator.Instance.StartOutgoingAsync(targetUserId, false);

                StatusText.Text = "Calling...";

                Frame.Navigate(typeof(CallPage), new CallPageArgs
                {
                    TargetUserId = targetUserId,
                    IsScreenShare = false
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }

        private async void ShareScreenButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StatusText.Text = "";

                var button = (Button)sender;
                var targetUserId = long.Parse(button.Tag?.ToString() ?? "0");

                if (targetUserId <= 0)
                    throw new Exception("Invalid target user.");

                await NativeCallCoordinator.Instance.StartOutgoingAsync(targetUserId, true);

                StatusText.Text = "Starting screen share call...";

                Frame.Navigate(typeof(CallPage), new CallPageArgs
                {
                    TargetUserId = targetUserId,
                    IsScreenShare = true
                });
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        }
    }
}