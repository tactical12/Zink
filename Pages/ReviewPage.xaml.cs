using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

// Prevent Zink.Windows.* collisions
using Launcher = global::Windows.System.Launcher;

namespace Zink.Pages
{
    public sealed partial class ReviewPage : Page
    {
        // Your Store ProductId from the URL
        private const string StoreProductId = "9NX0L20X52L7";

        public ReviewPage()
        {
            this.InitializeComponent();
        }

        private async void LeaveReview_Click(object sender, RoutedEventArgs e)
            => await OpenStoreReviewPageAsync();

        private async void OpenStorePage_Click(object sender, RoutedEventArgs e)
            => await OpenStorePageAsync();

        private async Task OpenStoreReviewPageAsync()
        {
            await LaunchWithUiStateAsync(
                primary: new Uri($"ms-windows-store://review/?ProductId={Uri.EscapeDataString(StoreProductId)}"),
                fallback: new Uri($"https://apps.microsoft.com/store/detail/{Uri.EscapeDataString(StoreProductId)}"),
                successMessage: "Opening Store review...",
                failureMessage: "Couldn't open Microsoft Store. Opening in browser instead..."
            );
        }

        private async Task OpenStorePageAsync()
        {
            await LaunchWithUiStateAsync(
                primary: new Uri($"ms-windows-store://pdp/?ProductId={Uri.EscapeDataString(StoreProductId)}"),
                fallback: new Uri($"https://apps.microsoft.com/store/detail/{Uri.EscapeDataString(StoreProductId)}"),
                successMessage: "Opening Store page...",
                failureMessage: "Couldn't open Microsoft Store. Opening in browser instead..."
            );
        }

        private async Task LaunchWithUiStateAsync(Uri primary, Uri fallback, string successMessage, string failureMessage)
        {
            SetButtonsEnabled(false);

            try
            {
                var ok = await Launcher.LaunchUriAsync(primary);
                if (ok)
                {
                    ShowInfo(successMessage, InfoBarSeverity.Success);
                    return;
                }

                // Store couldn't open (policy/disabled/missing) -> web fallback
                ShowInfo(failureMessage, InfoBarSeverity.Warning);

                var webOk = await Launcher.LaunchUriAsync(fallback);
                if (!webOk)
                {
                    ShowInfo("Couldn't open the Store or the browser.", InfoBarSeverity.Error);
                }
            }
            catch (Exception ex)
            {
                // Try web fallback even on exception
                ShowInfo("Couldn't open Microsoft Store. Opening in browser instead...", InfoBarSeverity.Warning);

                try
                {
                    var webOk = await Launcher.LaunchUriAsync(fallback);
                    if (!webOk)
                    {
                        ShowInfo("Couldn't open the Store or the browser.", InfoBarSeverity.Error);
                    }
                }
                catch (Exception ex2)
                {
                    ShowInfo("Couldn't open review page: " + ex.Message + " / " + ex2.Message, InfoBarSeverity.Error);
                }
            }
            finally
            {
                SetButtonsEnabled(true);
            }
        }

        private void SetButtonsEnabled(bool enabled)
        {
            // These names come from the updated XAML:
            // x:Name="LeaveReviewButton" and x:Name="OpenStorePageButton"
            if (LeaveReviewButton != null) LeaveReviewButton.IsEnabled = enabled;
            if (OpenStorePageButton != null) OpenStorePageButton.IsEnabled = enabled;
        }

        private void ShowInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
        {
            StatusBar.Severity = severity;
            StatusBar.Message = message;
            StatusBar.IsOpen = true;
        }
    }
}
