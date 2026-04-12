using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zink.Services;    // <-- needed

namespace Zink.Pages
{
    public sealed partial class NotificationsPage : Page
    {
        public NotificationsPage()
        {
            this.InitializeComponent();
        }

        private void OnSendTestNotificationClicked(object sender, RoutedEventArgs e)
        {
            NotificationService.Instance.Show(
                "Test Alert",
                "If you see this, your toast is working!");
        }
    }
}
