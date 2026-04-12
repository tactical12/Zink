using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading.Tasks;

namespace Zink.Pages
{
    public sealed partial class AmazonMusicPage : Page
    {
        private static readonly Uri HomeUri = new("https://music.amazon.co.uk/");
        private bool _initialized;

        public AmazonMusicPage()
        {
            InitializeComponent();
            Loaded += AmazonMusicPage_Loaded;
            Unloaded += AmazonMusicPage_Unloaded;
        }

        private async void AmazonMusicPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;

            await InitWebViewAsync();

            try { MyWebView.CoreWebView2.Navigate(HomeUri.ToString()); }
            catch { MyWebView.Source = HomeUri; }
        }

        private void AmazonMusicPage_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                MyWebView.CoreWebView2?.Stop();
                MyWebView.CoreWebView2?.Navigate("about:blank");
            }
            catch { }
        }

        private async Task InitWebViewAsync()
        {
            // Use default environment (compatible with older SDKs)
            await MyWebView.EnsureCoreWebView2Async();

            var core = MyWebView.CoreWebView2;
            if (core == null) return;

            // Correct casing: Settings (not settings)
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = false;

            // Open target=_blank in same view
            core.NewWindowRequested += (s, e) =>
            {
                try
                {
                    e.Handled = true;
                    if (!string.IsNullOrEmpty(e.Uri))
                        core.Navigate(e.Uri);
                }
                catch { }
            };

            // Optional: observe failures via e.IsSuccess
            core.NavigationCompleted += (s, e) => { /* no-op */ };
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            try { MyWebView.CoreWebView2?.Navigate("about:blank"); } catch { }
            base.OnNavigatedFrom(e);
        }
    }
}
