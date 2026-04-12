using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;

namespace Zink.Pages
{
    public sealed partial class AmazonLunaPage : Page
    {
        private static readonly Uri HomeUri = new("https://luna.amazon.com/");
        private bool _initialized;

        public AmazonLunaPage()
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
                    "Zink_WebView2", "AmazonLuna");
                Directory.CreateDirectory(userDataFolder);

                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, null);
                await LunaWebView.EnsureCoreWebView2Async(env);

                LunaWebView.CoreWebView2.ContainsFullScreenElementChanged += (_, __) =>
                {
                    try
                    {
                        if (LunaWebView.CoreWebView2.ContainsFullScreenElement)
                            App.MainWindow?.EnterFullscreenMode();
                        else
                            App.MainWindow?.ExitFullscreenMode();
                    }
                    catch { }
                };

                LunaWebView.Source = HomeUri;
                LunaWebView.Focus(FocusState.Programmatic);
            }
            catch
            {
                Loader.IsActive = false;
            }
        }

        private void WebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            Loader.IsActive = false;
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e) => TryStopPlayback();

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            TryStopPlayback();
            base.OnNavigatedFrom(e);
        }

        private void TryStopPlayback()
        {
            try { LunaWebView.CoreWebView2?.Navigate("about:blank"); } catch { }
            try { App.MainWindow?.ExitFullscreenMode(); } catch { }
        }
    }
}
