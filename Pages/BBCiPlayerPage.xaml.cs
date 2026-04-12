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
    public sealed partial class BBCiPlayerPage : Page
    {
        private const string HomeUrl = "https://www.bbc.co.uk/iplayer";
        private bool _initialized;
        private bool _isAppFullscreen;

        public BBCiPlayerPage()
        {
            InitializeComponent();
            Loaded += BBCiPlayerPage_Loaded;
            Unloaded += BBCiPlayerPage_Unloaded;
            KeyDown += BBCiPlayerPage_KeyDown; // Esc to exit app fullscreen
        }

        private async void BBCiPlayerPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;
            await InitWebViewAsync();
        }

        private void BBCiPlayerPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // ensure we leave fullscreen and restore UI when page is unloaded
            if (_isAppFullscreen)
                ExitAppFullscreenSafe();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            // Best-effort: stop playback when leaving the page
            try { MyWebView.CoreWebView2?.Navigate("about:blank"); } catch { }
            base.OnNavigatedFrom(e);
        }

        private async Task InitWebViewAsync()
        {
            ShowLoading(true);

            // Ensure CoreWebView2 exists
            await MyWebView.EnsureCoreWebView2Async();
            var cwv2 = MyWebView.CoreWebView2;

            // SETTINGS
            var s = cwv2.Settings;
            s.IsStatusBarEnabled = false;
            s.AreDefaultContextMenusEnabled = true;
            s.AreDevToolsEnabled = true;      // set false for production if you prefer
            s.IsZoomControlEnabled = true;

            // Keep popups opening in same view
            cwv2.NewWindowRequested += (snd, ev) =>
            {
                ev.Handled = true;
                if (!string.IsNullOrEmpty(ev.Uri))
                    cwv2.Navigate(ev.Uri);
            };

            // Allow autoplay where possible (trailers etc.)
            cwv2.PermissionRequested += (snd, ev) =>
            {
                if (ev.PermissionKind == CoreWebView2PermissionKind.Autoplay)
                    ev.State = CoreWebView2PermissionState.Allow;
            };

            // Old fallback handler (some sites still trigger this)
            cwv2.ContainsFullScreenElementChanged += (snd, ev) =>
            {
                try
                {
                    if (cwv2.ContainsFullScreenElement)
                        EnterAppFullscreenSafe();
                    else
                        ExitAppFullscreenSafe();
                }
                catch { /* ignore */ }
            };

            // Inject script to reliably detect fullscreenchange and post messages
            const string fullscreenWatcherScript = @"
                (function() {
                    function notify() {
                        try {
                            var state = document.fullscreenElement ? 'enter-fullscreen' : 'exit-fullscreen';
                            window.chrome?.webview?.postMessage(state);
                        } catch(e) { /* ignore */ }
                    }
                    document.addEventListener('fullscreenchange', notify, true);
                    document.addEventListener('webkitfullscreenchange', notify, true);
                    document.addEventListener('mozfullscreenchange', notify, true);
                    var mo = new MutationObserver(function() { notify(); });
                    mo.observe(document, { childList: true, subtree: true });
                })();
            ";

            try
            {
                await cwv2.AddScriptToExecuteOnDocumentCreatedAsync(fullscreenWatcherScript);
            }
            catch
            {
                // fallback is ContainsFullScreenElementChanged
            }

            cwv2.WebMessageReceived += (snd, ev) =>
            {
                try
                {
                    var msg = ev.TryGetWebMessageAsString();
                    if (string.Equals(msg, "enter-fullscreen", StringComparison.OrdinalIgnoreCase))
                        EnterAppFullscreenSafe();
                    else if (string.Equals(msg, "exit-fullscreen", StringComparison.OrdinalIgnoreCase))
                        ExitAppFullscreenSafe();
                }
                catch { /* ignore */ }
            };

            // Loading UI
            MyWebView.NavigationStarting += (_, __) => ShowLoading(true);
            MyWebView.NavigationCompleted += (_, __) => ShowLoading(false);

            // Navigate
            cwv2.Navigate(HomeUrl);
        }

        private void ShowLoading(bool show) =>
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        private void EnterAppFullscreenSafe()
        {
            try
            {
                // Ask main window to save current sidebar state and hide it
                try
                {
                    App.MainWindow?.SaveAndHideSidebar();
                }
                catch { /* ignore if not available */ }

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

                // Restore saved sidebar state
                try
                {
                    App.MainWindow?.RestoreSavedSidebar();
                }
                catch { /* ignore if not available */ }
            }
            catch { /* ignore */ }
        }

        private void BBCiPlayerPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == WinSystem.VirtualKey.Escape && _isAppFullscreen)
            {
                ExitAppFullscreenSafe();
                e.Handled = true;
            }
        }
    }
}
