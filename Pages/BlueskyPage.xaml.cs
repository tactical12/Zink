using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Zink.Pages
{
    public sealed partial class BlueskyPage : Page
    {
        private static readonly Uri HomeUri = new("https://bsky.app/");
        private bool _initialized;

        public BlueskyPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += (_, __) => TryStopPlayback();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            TryStopPlayback();
            base.OnNavigatedFrom(e);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;

            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Zink_Bluesky_WebView2");

            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, folder, null);
            await MyWebView.EnsureCoreWebView2Async(env);

            MyWebView.CoreWebView2.NavigationStarting += (_, __) => LoadingRing.IsActive = true;
            MyWebView.CoreWebView2.NavigationCompleted += (_, __) => LoadingRing.IsActive = false;
            MyWebView.CoreWebView2.NewWindowRequested += (s, e) =>
            {
                e.NewWindow = s; e.Handled = true;
                if (!string.IsNullOrEmpty(e.Uri)) try { MyWebView.CoreWebView2.Navigate(e.Uri); } catch { }
            };

            MyWebView.Source = HomeUri;
        }

        private void TryStopPlayback()
        {
            try { MyWebView.CoreWebView2?.Navigate("about:blank"); } catch { }
        }
    }
}
