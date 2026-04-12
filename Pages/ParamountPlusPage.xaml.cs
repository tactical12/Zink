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
    public sealed partial class ParamountPlusPage : Page
    {
        // Generic home; the site will geo-route as needed
        private const string HomeUrl = "https://www.paramountplus.com/";
        private bool _initialized;
        private bool _isAppFullscreen;

        public ParamountPlusPage()
        {
            InitializeComponent();
            Loaded += ParamountPlusPage_Loaded;
            Unloaded += ParamountPlusPage_Unloaded;
            KeyDown += ParamountPlusPage_KeyDown; // Esc to exit app fullscreen
        }

        private async void ParamountPlusPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;
            await InitWebViewAsync();
        }

        private void ParamountPlusPage_Unloaded(object sender, RoutedEventArgs e)
        {
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

            // Ensure CoreWebView2 exists (no custom environment args to avoid overload errors)
            await MyWebView.EnsureCoreWebView2Async();

            var cwv2 = MyWebView.CoreWebView2;

            // Tweak settings
            var s = cwv2.Settings;
            s.IsStatusBarEnabled = false;
            s.AreDefaultContextMenusEnabled = true;
            s.AreDevToolsEnabled = true; // set false for production if you prefer
            s.IsZoomControlEnabled = true;

            // Keep popups in the same view
            cwv2.NewWindowRequested += (s2, e2) =>
            {
                e2.Handled = true;
                if (!string.IsNullOrEmpty(e2.Uri))
                    cwv2.Navigate(e2.Uri);
            };

            // Allow autoplay (trailers etc.)
            cwv2.PermissionRequested += (s2, e2) =>
            {
                if (e2.PermissionKind == CoreWebView2PermissionKind.Autoplay)
                    e2.State = CoreWebView2PermissionState.Allow;
            };

            // Site fullscreen -> App fullscreen
            cwv2.ContainsFullScreenElementChanged += (s2, e2) =>
            {
                if (cwv2.ContainsFullScreenElement)
                    EnterAppFullscreenSafe();
                else
                    ExitAppFullscreenSafe();
            };

            // Loading overlay
            MyWebView.NavigationStarting += (_, __) => ShowLoading(true);
            MyWebView.NavigationCompleted += (_, args) => ShowLoading(false);

            // Navigate
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
            catch { }
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
            catch { }
        }

        private void ParamountPlusPage_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == WinSystem.VirtualKey.Escape && _isAppFullscreen)
            {
                ExitAppFullscreenSafe();
                e.Handled = true;
            }
        }
    }
}
