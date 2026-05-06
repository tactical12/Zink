using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;

namespace Zink.Pages
{
    public sealed partial class TwitchPage : Page
    {
        private static readonly Uri TwitchHome = new("https://www.twitch.tv/");
        private AppWindow _appWindow;

        // ? saved “card” visuals so we can restore after fullscreen
        private Thickness _savedHostMargin;
        private CornerRadius _savedHostCornerRadius;
        private Thickness _savedHostBorderThickness;
        private Brush _savedPageBackground;
        private bool _savedVisualsCaptured;

        public TwitchPage()
        {
            InitializeComponent();
            Loaded += TwitchPage_Loaded;
        }

        private async void TwitchPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Optional: persistent WebView profile
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ZinkTwitchWebViewData");

                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, null);
                await TwitchWebView.EnsureCoreWebView2Async(env);

                // ? In-player fullscreen -> app fullscreen + sidebar hide
                TwitchWebView.CoreWebView2.ContainsFullScreenElementChanged += CoreWebView2_ContainsFullScreenElementChanged;

                // AppWindow for fullscreen toggle
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                _appWindow = AppWindow.GetFromWindowId(windowId);

                TwitchWebView.Source = TwitchHome;
            }
            catch
            {
                // Keep resilient
            }
        }

        private void CoreWebView2_ContainsFullScreenElementChanged(CoreWebView2 sender, object args)
        {
            if (_appWindow == null) return;

            // ? capture original visuals once
            if (!_savedVisualsCaptured)
            {
                _savedHostMargin = HostBorder.Margin;
                _savedHostCornerRadius = HostBorder.CornerRadius;
                _savedHostBorderThickness = HostBorder.BorderThickness;
                _savedPageBackground = Background;
                _savedVisualsCaptured = true;
            }

            if (sender.ContainsFullScreenElement)
            {
                // ? TRUE edge-to-edge: remove the “card” styling
                HostBorder.Margin = new Thickness(0);
                HostBorder.CornerRadius = new CornerRadius(0);
                HostBorder.BorderThickness = new Thickness(0);

                // ? FIX: WinUI 3 uses Microsoft.UI.Colors (NOT Windows.UI.Colors)
                Background = new SolidColorBrush(Microsoft.UI.Colors.Black);

                _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                App.MainWindow.SetSidebarVisibility(false);
            }
            else
            {
                // ? restore visuals
                HostBorder.Margin = _savedHostMargin;
                HostBorder.CornerRadius = _savedHostCornerRadius;
                HostBorder.BorderThickness = _savedHostBorderThickness;
                Background = _savedPageBackground;

                _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                App.MainWindow.SetSidebarVisibility(true);
            }
        }

        private void TwitchWebView_CoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
        {
            if (sender.CoreWebView2 == null)
                return;

            ApplyStoreCompliantTwitchWebViewPolicy(sender.CoreWebView2);

            // Keep window.open inside same WebView
            sender.CoreWebView2.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                try { TwitchWebView.CoreWebView2.Navigate(e.Uri); } catch { }
            };
        }

        private static void ApplyStoreCompliantTwitchWebViewPolicy(CoreWebView2 webView)
        {
            webView.Settings.AreDefaultContextMenusEnabled = true;
            webView.Settings.AreDevToolsEnabled = true;
            webView.Settings.IsZoomControlEnabled = true;
            webView.Settings.IsStatusBarEnabled = false;
            webView.Settings.AreHostObjectsAllowed = false;

            try
            {
                webView.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Balanced;
            }
            catch { }

            webView.PermissionRequested += (s, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Notifications ||
                    e.PermissionKind == CoreWebView2PermissionKind.Geolocation)
                {
                    e.State = CoreWebView2PermissionState.Deny;
                    e.Handled = true;
                }
            };
        }

        private void TwitchWebView_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        private void TwitchWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            try
            {
                if (TwitchWebView != null)
                    TwitchWebView.Source = new Uri("about:blank");
            }
            catch { }
        }
    }
}
