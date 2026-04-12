using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading.Tasks;
using WinRT.Interop;

// Force global Windows namespaces to avoid Zink.Windows collisions
using WinSystem = global::Windows.System;

namespace Zink.Pages
{
    public sealed partial class NetflixPage : Page
    {
        private const string HomeUrl = "https://www.netflix.com/browse";
        private bool _initialized;
        private bool _isAppFullscreen;

        public NetflixPage()
        {
            InitializeComponent();
            Loaded += NetflixPage_Loaded;
            Unloaded += NetflixPage_Unloaded; // stop audio if the view unloads
        }

        private async void NetflixPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;
            await InitWebViewAsync();
        }

        // Stop audio when navigating away
        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            StopPlayback();
            base.OnNavigatedFrom(e);
        }
        private void NetflixPage_Unloaded(object sender, RoutedEventArgs e) => StopPlayback();

        private void StopPlayback()
        {
            try { WebView.CoreWebView2?.Navigate("about:blank"); } catch { /* ignore */ }
        }

        private async Task InitWebViewAsync()
        {
            await WebView.EnsureCoreWebView2Async();

            var s = WebView.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = true;
            s.AreDevToolsEnabled = true;
            s.AreBrowserAcceleratorKeysEnabled = true;
            s.IsZoomControlEnabled = true;
            s.IsStatusBarEnabled = false;

            // Open popups in the same view
            WebView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (!string.IsNullOrEmpty(args.Uri))
                    WebView.CoreWebView2.Navigate(args.Uri);
            };

            // Permissions (deny location; allow typical media)
            WebView.CoreWebView2.PermissionRequested += (_, e) =>
            {
                e.State = e.PermissionKind == CoreWebView2PermissionKind.Geolocation
                    ? CoreWebView2PermissionState.Deny
                    : CoreWebView2PermissionState.Allow;
            };

            // Mirror HTML5 fullscreen ? app fullscreen + hide sidebar
            WebView.CoreWebView2.ContainsFullScreenElementChanged += (_, __) =>
            {
                if (WebView.CoreWebView2.ContainsFullScreenElement) EnterAppFullscreen();
                else ExitAppFullscreen();
            };

            // ESC exits app fullscreen
            KeyDown += (_, e) =>
            {
                if (e.Key == WinSystem.VirtualKey.Escape && _isAppFullscreen)
                {
                    ExitAppFullscreen();
                    e.Handled = true;
                }
            };

            WebView.CoreWebView2.Navigate(HomeUrl);
        }

        private AppWindow GetAppWindow()
        {
            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            return AppWindow.GetFromWindowId(id);
        }

        private void EnterAppFullscreen()
        {
            if (_isAppFullscreen) return;
            _isAppFullscreen = true;

            TopBar.Visibility = Visibility.Collapsed;
            App.MainWindow.SetSidebarVisibility(false);  // hide + collapse width
            GetAppWindow().SetPresenter(AppWindowPresenterKind.FullScreen);
        }

        private void ExitAppFullscreen()
        {
            if (!_isAppFullscreen) return;
            _isAppFullscreen = false;

            TopBar.Visibility = Visibility.Visible;
            App.MainWindow.SetSidebarVisibility(true);   // restore pane + width
            GetAppWindow().SetPresenter(AppWindowPresenterKind.Overlapped);
        }

        // Top bar actions
        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (WebView.CoreWebView2?.CanGoBack == true) WebView.CoreWebView2.GoBack();
        }
        private void Forward_Click(object sender, RoutedEventArgs e)
        {
            if (WebView.CoreWebView2?.CanGoForward == true) WebView.CoreWebView2.GoForward();
        }
        private void Reload_Click(object sender, RoutedEventArgs e) => WebView.CoreWebView2?.Reload();
        private void Home_Click(object sender, RoutedEventArgs e) => WebView.CoreWebView2?.Navigate(HomeUrl);
    }
}
