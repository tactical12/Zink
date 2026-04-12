using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zink.Services.Social;

namespace Zink.Pages.Social
{
    public sealed partial class FriendRequestsPage : Page
    {
        public FriendRequestsPage()
        {
            this.InitializeComponent();
            Loaded += FriendRequestsPage_Loaded;
        }

        private async void FriendRequestsPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            RequestsList.ItemsSource = await SocialManager.Instance.Api.GetRequestsAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadAsync();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private async void AcceptButton_Click(object sender, RoutedEventArgs e)
        {
            var requestId = long.Parse(((Button)sender).Tag?.ToString() ?? "0");
            await SocialManager.Instance.Api.RespondRequestAsync(requestId, true);
            await LoadAsync();
        }

        private async void DeclineButton_Click(object sender, RoutedEventArgs e)
        {
            var requestId = long.Parse(((Button)sender).Tag?.ToString() ?? "0");
            await SocialManager.Instance.Api.RespondRequestAsync(requestId, false);
            await LoadAsync();
        }
    }
}