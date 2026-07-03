using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.System;
using Zink.Services;    // <-- needed

namespace Zink.Pages
{
    public sealed partial class NotificationsPage : Page
    {
        public NotificationsPage()
        {
            this.InitializeComponent();
            NotificationsList.ItemsSource = NotificationService.Instance.Notifications;
        }

        private void OnSendTestNotificationClicked(object sender, RoutedEventArgs e)
        {
            NotificationService.Instance.Show(
                "Test Alert",
                "If you see this, your toast is working!");
        }

        private void OnDeleteNotificationClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is Notification notification)
            {
                NotificationService.Instance.Delete(notification);
            }
        }

        private async void OnNotificationActionClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not Notification notification)
                return;

            if (string.IsNullOrWhiteSpace(notification.ActionUri))
                return;

            try
            {
                await Launcher.LaunchUriAsync(new Uri(notification.ActionUri));
            }
            catch
            {
            }
        }

        private void OnDeleteAllNotificationsClicked(object sender, RoutedEventArgs e)
        {
            NotificationService.Instance.DeleteAll();
        }
    }
}
