using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Services.Store;
using WinRT.Interop; // <-- needed for WindowNative & InitializeWithWindow

namespace Zink.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private readonly StoreContext _store;

        public SettingsPage()
        {
            this.InitializeComponent();

            // Create the StoreContext
            _store = StoreContext.GetDefault();

            // IMPORTANT: attach the StoreContext to your main WinUI 3 window
            try
            {
                // App.MainWindow is your main WinUI 3 window (you already use it elsewhere)
                IntPtr hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                InitializeWithWindow.Initialize(_store, hwnd);
            }
            catch (Exception ex)
            {
                // Optional: show a one-time init error (won’t stop the app)
                if (StatusText != null)
                {
                    StatusText.Text = $"Error initialising update system: {ex.Message}";
                }
            }
        }

        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            CheckForUpdatesButton.IsEnabled = false;
            StatusText.Text = "Checking for updates…";

            try
            {
                // 1) get list of available updates
                var updates = await _store.GetAppAndOptionalStorePackageUpdatesAsync();

                if (updates.Count == 0)
                {
                    StatusText.Text = "Your app is up to date.";
                }
                else
                {
                    StatusText.Text = $"{updates.Count} update(s) available. Downloading…";

                    // 2) download & install them
                    var result = await _store.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);

                    // 3) examine the result
                    if (result.OverallState == StorePackageUpdateState.Completed)
                    {
                        StatusText.Text = "Update installed. Restart your app to apply changes.";
                    }
                    else
                    {
                        StatusText.Text = $"Update failed: {result.OverallState}";
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error checking for updates: {ex.Message}";
            }
            finally
            {
                CheckForUpdatesButton.IsEnabled = true;
            }
        }
    }
}
