using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Storage;
using Zink.Pages;

namespace Zink.Services
{
    public sealed class NotificationService
    {
        private const string StoredNotificationsKey = "Zink.Notifications.Stored";
        public const string AppUpdateNotificationKind = "AppUpdateAvailable";
        public const string AppUpdateNotificationTitle = "Zink app update ready";
        public const string AppUpdateNotificationMessage = "The Zink app has a new update ready to be installed. Installing the update is recommended because it can contain bug fixes, performance improvements and new features.";
        public const string AppUpdateNotificationActionLabel = "Install the update";
        public const string AppUpdateNotificationActionUri = "ms-windows-store://downloadsandupdates";
        private static readonly NotificationService _instance = new NotificationService();

        public static NotificationService Instance => _instance;
        public ObservableCollection<Notification> Notifications { get; } = new();

        private NotificationService()
        {
            LoadStoredNotifications();
        }

        public void Show(string title, string message)
        {
            Show(title, message, store: true);
        }

        public void Show(string title, string message, bool store)
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

            if (store)
                AddStoredNotification(title, message);
        }

        public void ShowAppUpdateReady()
        {
            Show(
                AppUpdateNotificationTitle,
                AppUpdateNotificationMessage,
                store: false);

            AddOrUpdateStoredNotification(
                AppUpdateNotificationKind,
                AppUpdateNotificationTitle,
                AppUpdateNotificationMessage,
                AppUpdateNotificationActionLabel,
                AppUpdateNotificationActionUri);
        }

        public void Delete(Notification notification)
        {
            if (notification == null)
                return;

            try
            {
                var existing = Notifications.FirstOrDefault(item =>
                    string.Equals(item.Id, notification.Id, StringComparison.Ordinal));

                if (existing != null)
                    Notifications.Remove(existing);

                SaveStoredNotifications();
            }
            catch
            {
            }
        }

        public void DeleteAll()
        {
            try
            {
                Notifications.Clear();
                SaveStoredNotifications();
            }
            catch
            {
            }
        }

        private void AddStoredNotification(string title, string message)
        {
            try
            {
                Notifications.Insert(0, new Notification
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = title ?? "",
                    Message = message ?? "",
                    Timestamp = DateTime.Now
                });

                SaveStoredNotifications();
            }
            catch
            {
            }
        }

        private void AddOrUpdateStoredNotification(
            string kind,
            string title,
            string message,
            string actionLabel,
            string actionUri)
        {
            try
            {
                var existing = Notifications.FirstOrDefault(item =>
                    string.Equals(item.Kind, kind, StringComparison.Ordinal));

                if (existing != null)
                {
                    existing.Title = title ?? "";
                    existing.Message = message ?? "";
                    existing.ActionLabel = actionLabel ?? "";
                    existing.ActionUri = actionUri ?? "";
                    existing.Timestamp = DateTime.Now;

                    Notifications.Remove(existing);
                    Notifications.Insert(0, existing);
                }
                else
                {
                    Notifications.Insert(0, new Notification
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Kind = kind ?? "",
                        Title = title ?? "",
                        Message = message ?? "",
                        ActionLabel = actionLabel ?? "",
                        ActionUri = actionUri ?? "",
                        Timestamp = DateTime.Now
                    });
                }

                SaveStoredNotifications();
            }
            catch
            {
            }
        }

        private void LoadStoredNotifications()
        {
            try
            {
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(StoredNotificationsKey, out var value) ||
                    value is not string json ||
                    string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                var stored = JsonSerializer.Deserialize<Notification[]>(json);
                if (stored == null)
                    return;

                Notifications.Clear();
                foreach (var notification in stored.OrderByDescending(item => item.Timestamp))
                {
                    if (string.IsNullOrWhiteSpace(notification.Id))
                        notification.Id = Guid.NewGuid().ToString("N");

                    Notifications.Add(notification);
                }
            }
            catch
            {
            }
        }

        private void SaveStoredNotifications()
        {
            try
            {
                var json = JsonSerializer.Serialize(Notifications.ToArray());
                ApplicationData.Current.LocalSettings.Values[StoredNotificationsKey] = json;
            }
            catch
            {
            }
        }
    }
}
