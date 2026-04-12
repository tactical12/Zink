using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading.Tasks;
using WinRT.Interop;

// Force global Windows namespaces to avoid Zink.Windows collisions
using WinSystem = global::Windows.System;

namespace Zink.Pages
{
    public sealed partial class DisneyPlusPage : Page
    {
        // UK-friendly home URL
        private const string HomeUrl = "https://www.disneyplus.com/en-gb/home";
        private bool _initialized;
        private bool _isAppFullscreen;

        public DisneyPlusPage()
        {
            InitializeComponent();
            Loaded += DisneyPlusPage_Loaded;
            Unloaded += DisneyPlusPage_Unloaded;

            // Let Esc exit app fullscreen
            KeyDown += DisneyPlusPage_KeyDown;
        }

        private async void DisneyPlusPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;
            await InitWebViewAsync();
        }

        private void DisneyPlusPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // If the page unloads, exit fullscreen just in case
            if (_isAppFullscreen)
                ExitAppFullscreenSafe();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            // Stop any playback when leaving the page
            try { MyWebView.CoreWebView2?.Navigate("about:blank"); } catch { }
            base.OnNavigatedFrom(e);
        }

        private async Task InitWebViewAsync()
        {
            ShowLoading(true);

            // Ensure control is ready
            await MyWebView.EnsureCoreWebView2Async();

            var cwv2 = MyWebView.CoreWebView2;

            // Basic quality-of-life settings
            var s = cwv2.Settings;
            s.IsStatusBarEnabled = false;
            s.AreDefaultContextMenusEnabled = true;
            s.AreDevToolsEnabled = true; // toggle off for production if desired
            s.IsZoomControlEnabled = true;

            // Keep all new windows inside this WebView
            cwv2.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                if (!string.IsNullOrEmpty(e.Uri))
                    cwv2.Navigate(e.Uri);
            };

            // Site-driven fullscreen (e.g., when a video enters fullscreen)
            cwv2.ContainsFullScreenElementChanged += (s, e) =>
            {
                if (cwv2.ContainsFullScreenElement)
                    EnterAppFullscreenSafe();
                else
                    ExitAppFullscreenSafe();
            };

            // Allow autoplay so previews don’t get blocked
            cwv2.PermissionRequested += (s, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Autoplay)
                {
                    e.State = CoreWebView2PermissionState.Allow;
                }
            };

            // Show a loading overlay during navigations
            MyWebView.NavigationStarting += (_, __) => ShowLoading(true);
            MyWebView.NavigationCompleted += (_, args) =>
            {
                ShowLoading(false);
            };

            // Go!
            cwv2.Navigate(HomeUrl);
        }

        private void ShowLoading(bool show)
        {
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EnterAppFullscreenSafe()
        {
            try
            {
                var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                _isAppFullscreen = true;
            }
            catch { /* ignore */ }
        }

        private void ExitAppFullscreenSafe()
        {
            try
            {
                var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                _isAppFullscreen = false;
            }
            catch { /* ignore */ }
        }

        private void DisneyPlusPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Escape exits app-level fullscreen
            if (e.Key == WinSystem.VirtualKey.Escape && _isAppFullscreen)
            {
                ExitAppFullscreenSafe();
                e.Handled = true;
            }
        }
    }
}
