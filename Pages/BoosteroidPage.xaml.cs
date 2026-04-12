using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;

namespace Zink.Pages
{
    public sealed partial class BoosteroidPage : Page
    {
        // Rotate through known endpoints if DNS/connect fails
        private readonly List<Uri> _boosteroidUris = new()
        {
            new Uri("https://play.boosteroid.com/"),
            new Uri("https://cloud.boosteroid.com/"),
            new Uri("https://boosteroid.com/")
        };

        private int _uriIndex = 0;
        private bool _initialized;

        public BoosteroidPage()
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
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Zink_WebView2", "Boosteroid");
                Directory.CreateDirectory(userDataFolder);

                var opts = new CoreWebView2EnvironmentOptions();
                try { opts.AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required"; } catch { }

                var env = await CoreWebView2Environment.CreateWithOptionsAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataFolder,
                    options: opts);

                await BoosteroidWebView.EnsureCoreWebView2Async(env);

                try
                {
                    BoosteroidWebView.CoreWebView2.Settings.UserAgent =
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36 Edg/124.0.0.0";
                }
                catch { }

                BoosteroidWebView.CoreWebView2.ContainsFullScreenElementChanged += (_, __) =>
                {
                    try
                    {
                        if (BoosteroidWebView.CoreWebView2.ContainsFullScreenElement)
                            App.MainWindow?.EnterFullscreenMode();
                        else
                            App.MainWindow?.ExitFullscreenMode();
                    }
                    catch { }
                };

                BoosteroidWebView.CoreWebView2.ProcessFailed += (_, __) =>
                {
                    try { BoosteroidWebView.Reload(); } catch { }
                };

                BoosteroidWebView.NavigationCompleted += WebView_NavigationCompleted;

                NavigateCurrent();
                BoosteroidWebView.Focus(FocusState.Programmatic);
            }
            catch (Exception ex)
            {
                Loader.IsActive = false;
                ErrorText.Text = "Boosteroid failed to initialize. " + ex.Message;
                ErrorText.Visibility = Visibility.Visible;
            }
        }

        private void NavigateCurrent()
        {
            try
            {
                ErrorText.Visibility = Visibility.Collapsed;
                Loader.IsActive = true;
                BoosteroidWebView.Source = _boosteroidUris[_uriIndex];
            }
            catch { }
        }

        private void TryNextFallback(string reason)
        {
            _uriIndex++;
            if (_uriIndex < _boosteroidUris.Count)
            {
                ErrorText.Text = $"{reason} Trying {_boosteroidUris[_uriIndex].Host}…";
                ErrorText.Visibility = Visibility.Visible;
                NavigateCurrent();
            }
            else
            {
                Loader.IsActive = false;
                ErrorText.Text = $"{reason} All Boosteroid endpoints failed.";
                ErrorText.Visibility = Visibility.Visible;
            }
        }

        private void WebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            Loader.IsActive = false;

            if (!args.IsSuccess)
            {
                // Compatible statuses for older SDKs
                if (args.WebErrorStatus == CoreWebView2WebErrorStatus.HostNameNotResolved ||
                    args.WebErrorStatus == CoreWebView2WebErrorStatus.CannotConnect ||
                    args.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted)
                {
                    TryNextFallback($"Boosteroid error: {args.WebErrorStatus}.");
                    return;
                }

                ErrorText.Text = $"Boosteroid error: {args.WebErrorStatus}.";
                ErrorText.Visibility = Visibility.Visible;
            }
            else
            {
                ErrorText.Visibility = Visibility.Collapsed;
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
            try { BoosteroidWebView.CoreWebView2?.Navigate("about:blank"); } catch { }
            try { App.MainWindow?.ExitFullscreenMode(); } catch { }
        }
    }
}
