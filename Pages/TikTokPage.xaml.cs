using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;

namespace Zink.Pages
{
    public sealed partial class TikTokPage : Page
    {
        public TikTokPage()
        {
            this.InitializeComponent();
            this.Loaded += TikTokPage_Loaded;
        }

        private async void TikTokPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            await TikTokWebView.EnsureCoreWebView2Async();

            TikTokWebView.Source = new Uri("https://www.tiktok.com");

            TikTokWebView.CoreWebView2.NavigationCompleted += (s, args) =>
            {
                TikTokWebView.CoreWebView2.ExecuteScriptAsync(@"
                    (function() {
                        const videos = document.querySelectorAll('video');
                        for (const video of videos) {
                            video.muted = false;
                            video.volume = 1.0;
                            video.play();
                        }
                    })();
                ");
            };
        }
    }
}
