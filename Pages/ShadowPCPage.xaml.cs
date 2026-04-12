using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using Windows.System;

namespace Zink.Pages
{
    public sealed partial class ShadowPCPage : Page
    {
        // Pin to the login URL you provided
        private readonly Uri _shadowUri = new("https://pc.shadow.tech/login?_gl=1*15ow5ry*_gcl_au*MTA5MzE5NDYyMi4xNzU2MTU3NjE3");
        private bool _initialized;

        public ShadowPCPage()
        {
            InitializeComponent();
            Loaded += Page_Loaded;
            Unloaded += Page_Unloaded;
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                // Persistent profile
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Zink_WebView2", "ShadowPC");
                Directory.CreateDirectory(userDataFolder);

                // Use CreateWithOptionsAsync for older SDKs
                var opts = new CoreWebView2EnvironmentOptions();
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder,
                    options: opts);

                await ShadowWebView.EnsureCoreWebView2Async(env);

                // Desktop Edge UA (some services require this)
                try
                {
                    ShadowWebView.CoreWebView2.Settings.UserAgent =
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0";
                }
                catch { }

                // Fullscreen sync with MainWindow
                ShadowWebView.CoreWebView2.ContainsFullScreenElementChanged += (_, __) =>
                {
                    try
                    {
                        if (ShadowWebView.CoreWebView2.ContainsFullScreenElement)
                            App.MainWindow?.EnterFullscreenMode();
                        else
                            App.MainWindow?.ExitFullscreenMode();
                    }
                    catch { }
                };

                // Basic crash recovery
                ShadowWebView.CoreWebView2.ProcessFailed += (_, __) =>
                {
                    try { ShadowWebView.Reload(); } catch { }
                };

                ShadowWebView.NavigationCompleted += WebView_NavigationCompleted;

                ShadowWebView.Source = _shadowUri;
                ShadowWebView.Focus(FocusState.Programmatic);
            }
            catch (Exception ex)
            {
                Loader.IsActive = false;
                ShowError("Error initializing Shadow WebView: " + ex.Message, showOpen: true);
            }
        }

        private void WebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            Loader.IsActive = false;

            if (!args.IsSuccess)
            {
                ShowError($"Shadow load failed: {args.WebErrorStatus}.", showOpen: true);
            }
            else
            {
                HideError();
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e) => TryStopPlayback();

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            TryStopPlayback();
            base.OnNavigatedFrom(e);
        }

        private void TryStopPlayback()
        {
            try { ShadowWebView.CoreWebView2?.Navigate("about:blank"); } catch { }
            try { App.MainWindow?.ExitFullscreenMode(); } catch { }
        }

        // UI helpers (match your XAML: ErrorText + OpenBrowserBtn)
        private void ShowError(string text, bool showOpen)
        {
            ErrorText.Text = text;
            ErrorText.Visibility = Visibility.Visible;
            OpenBrowserBtn.Visibility = showOpen ? Visibility.Visible : Visibility.Collapsed;
        }

        private void HideError()
        {
            ErrorText.Visibility = Visibility.Collapsed;
            OpenBrowserBtn.Visibility = Visibility.Collapsed;
        }

        private async void OpenBrowserBtn_Click(object sender, RoutedEventArgs e)
        {
            try { await Launcher.LaunchUriAsync(_shadowUri); } catch { }
        }
    }
}
