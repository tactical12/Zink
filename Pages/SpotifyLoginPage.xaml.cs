using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class SpotifyLoginPage : Page
    {
        private bool _attached = false;
        private bool _tokenExchanged = false;

        public SpotifyLoginPage()
        {
            this.InitializeComponent();
            SpotifyWebView.NavigationStarting += SpotifyWebView_NavigationStarting;
            SpotifyWebView.NavigationCompleted += SpotifyWebView_NavigationCompleted;
            StartSpotifyLogin();

            this.Loaded += async (_, __) =>
            {
                try
                {
                    await SpotifyWebView.EnsureCoreWebView2Async();
                }
                catch { }
            };

            SpotifyWebView.CoreWebView2Initialized += SpotifyWebView_CoreWebView2Initialized;
        }

        private void StartSpotifyLogin()
        {
            SpotifyWebView.Source = new Uri(SpotifyAuthHelper.GetNativeAuthorizationUrl(showDialog: false));
        }

        private async void SpotifyWebView_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            var url = args.Uri;
            if (url.StartsWith(SpotifyAuthHelper.DefaultRedirectUri, StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;

                if (_tokenExchanged)
                {
                    try
                    {
                        SpotifyWebView.Source = new Uri("https://open.spotify.com/");
                    }
                    catch { }
                    return;
                }

                try
                {
                    var uri = new Uri(url);
                    var code = System.Web.HttpUtility.ParseQueryString(uri.Query).Get("code");

                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        await SpotifyAuthHelper.ExchangeCodeForTokenAsync(code);
                        _tokenExchanged = true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Spotify token exchange failed: " + ex.Message);
                }

                try
                {
                    SpotifyWebView.Source = new Uri("https://open.spotify.com/");
                }
                catch { }
            }
        }

        private async void SpotifyWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (SpotifyWebView.Source != null &&
                SpotifyWebView.Source.AbsoluteUri.StartsWith("https://open.spotify.com", StringComparison.OrdinalIgnoreCase) &&
                !_attached)
            {
                try
                {
                    if (SpotifyWebView.CoreWebView2 == null)
                        await SpotifyWebView.EnsureCoreWebView2Async();

                    SpotifyControllerService.Instance.Attach(SpotifyWebView.CoreWebView2);
                    _attached = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Failed to attach SpotifyControllerService: " + ex.Message);
                }
            }
        }

        private void SpotifyWebView_CoreWebView2Initialized(
            WebView2 sender,
            Microsoft.UI.Xaml.Controls.CoreWebView2InitializedEventArgs args)
        {
            if (_attached) return;

            try
            {
                if (sender.CoreWebView2 != null)
                {
                    SpotifyControllerService.Instance.Attach(sender.CoreWebView2);
                    _attached = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Failed to early-attach SpotifyControllerService: " + ex.Message);
            }
        }
    }
}
