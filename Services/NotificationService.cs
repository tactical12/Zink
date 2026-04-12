using System;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Zink.Services
{
    public sealed class NotificationService
    {
        private static readonly NotificationService _instance = new NotificationService();
        public static NotificationService Instance => _instance;

        private NotificationService() { }

        public void Show(string title, string message)
        {
            try
            {
                var notification = new AppNotificationBuilder()
                    .AddText(title)
                    .AddText(message)
                    .BuildNotification();

                AppNotificationManager.Default.Show(notification);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Notification FAILED: " + ex);
            }
        }
    }
}
