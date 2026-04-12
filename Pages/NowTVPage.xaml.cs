using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading.Tasks;
using WinRT.Interop;
using WinSystem = global::Windows.System;

namespace Zink.Pages
{
    public sealed partial class NowTVPage : Page
    {
        private const string HomeUrl = "https://www.nowtv.com/gb/watch"; // NOW (UK)
        private bool _initialized;
        private bool _isAppFullscreen;

        public NowTVPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            KeyDown += OnKeyDown;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;
            await InitWebViewAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_isAppFullscreen) ExitAppFullscreen();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            try { MyWebView.CoreWebView2?.Navigate("about:blank"); } catch { }
            base.OnNavigatedFrom(e);
        }

        private async Task InitWebViewAsync()
        {
            ShowLoading(true);

            await MyWebView.EnsureCoreWebView2Async();
            var cwv2 = MyWebView.CoreWebView2;

            var s = cwv2.Settings;
            s.IsStatusBarEnabled = false;
            s.AreDefaultContextMenusEnabled = true;
            s.AreDevToolsEnabled = true;
            s.IsZoomControlEnabled = true;

            cwv2.NewWindowRequested += (_, ev) =>
            {
                ev.Handled = true;
                if (!string.IsNullOrEmpty(ev.Uri)) cwv2.Navigate(ev.Uri);
            };

            cwv2.PermissionRequested += (_, ev) =>
            {
                if (ev.PermissionKind == CoreWebView2PermissionKind.Autoplay)
                    ev.State = CoreWebView2PermissionState.Allow;
            };

            cwv2.ContainsFullScreenElementChanged += (_, __) =>
            {
                if (cwv2.ContainsFullScreenElement) EnterAppFullscreen();
                else ExitAppFullscreen();
            };

            MyWebView.NavigationStarting += (_, __) => ShowLoading(true);
            MyWebView.NavigationCompleted += (_, __) => ShowLoading(false);

            cwv2.Navigate(HomeUrl);
        }

        private void ShowLoading(bool show) =>
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        private void EnterAppFullscreen()
        {
            try
            {
                var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                AppWindow.GetFromWindowId(id)?.SetPresenter(AppWindowPresenterKind.FullScreen);
                _isAppFullscreen = true;
            }
            catch { }
        }

        private void ExitAppFullscreen()
        {
            try
            {
                var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                AppWindow.GetFromWindowId(id)?.SetPresenter(AppWindowPresenterKind.Overlapped);
                _isAppFullscreen = false;
            }
            catch { }
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == WinSystem.VirtualKey.Escape && _isAppFullscreen)
            {
                ExitAppFullscreen();
                e.Handled = true;
            }
        }
    }
}
