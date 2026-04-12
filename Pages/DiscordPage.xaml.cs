using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Zink.Pages
{
    public sealed partial class DiscordPage : Page
    {
        private CancellationTokenSource _readyCts;

        public DiscordPage()
        {
            this.InitializeComponent();
            InitializeDiscordWebView();
        }

        private async void InitializeDiscordWebView()
        {
            try
            {
                ShowLoader(true);

                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ZinkDiscordWebView2Data");

                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, null);
                await DiscordWebView.EnsureCoreWebView2Async(env);

                // Basic settings
                var s = DiscordWebView.CoreWebView2.Settings;
                s.AreDefaultContextMenusEnabled = true;
                s.AreDevToolsEnabled = true;
                s.IsStatusBarEnabled = false;

                // Manage loader visibility across navigations
                DiscordWebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
                DiscordWebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                DiscordWebView.CoreWebView2.DOMContentLoaded += CoreWebView2_DOMContentLoaded;
                DiscordWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

                // Inject readiness probe. ASCII only in this string.
                await DiscordWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
(function(){
  function readyPing(){
    try{
      var app = document.querySelector('#app-mount');
      var complete = document.readyState === 'complete';
      if (app && app.children && app.children.length > 0 && complete){
        if (window.chrome && window.chrome.webview){
          window.chrome.webview.postMessage({ type:'discord-ready' });
        }
        return;
      }
    }catch(e){}
    setTimeout(readyPing, 500);
  }
  window.addEventListener('load', function(){ setTimeout(readyPing, 200); });
  readyPing();
})();");

                // Go to Discord
                DiscordWebView.Source = new Uri("https://discord.com/app");
            }
            catch
            {
                // If init fails, hide loader so the error surface is visible
                ShowLoader(false);
                throw;
            }
        }

        private void CoreWebView2_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            // Any top level navigation: show loader and set a fallback timer
            ShowLoader(true);

            _readyCts?.Cancel();
            _readyCts = new CancellationTokenSource();
            _ = HideLoaderAfterTimeoutAsync(TimeSpan.FromSeconds(20), _readyCts.Token);
        }

        private void CoreWebView2_DOMContentLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args)
        {
            // Keep loader up; SPA hydration comes later. The injected script will signal readiness.
        }

        private void CoreWebView2_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!args.IsSuccess)
            {
                // Show the error page; do not block with loader
                _readyCts?.Cancel();
                ShowLoader(false);
            }
            // On success, still wait for the 'discord-ready' message.
        }

        private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                var json = args.WebMessageAsJson ?? "{}";
                if (json.Contains("\"type\":\"discord-ready\"", StringComparison.Ordinal))
                {
                    _readyCts?.Cancel();
                    ShowLoader(false);
                }
            }
            catch
            {
                // Ignore parsing errors
            }
        }

        private async Task HideLoaderAfterTimeoutAsync(TimeSpan delay, CancellationToken token)
        {
            try
            {
                await Task.Delay(delay, token);
                if (!token.IsCancellationRequested)
                {
                    ShowLoader(false);
                }
            }
            catch (TaskCanceledException) { }
        }

        private void ShowLoader(bool show)
        {
            if (LoaderOverlay == null) return;
            LoaderOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (LoaderRing != null) LoaderRing.IsActive = show;
        }
    }
}
