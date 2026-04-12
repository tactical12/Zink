using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading.Tasks;

namespace Zink.Pages
{
    public sealed partial class YouTubeMusicPage : Page
    {
        private static readonly Uri HomeUri = new("https://music.youtube.com/?gl=GB&hl=en-GB");
        private bool _initialized;

        public YouTubeMusicPage()
        {
            InitializeComponent();

            Loaded += YouTubeMusicPage_Loaded;
            Unloaded += YouTubeMusicPage_Unloaded;

            // Show loader while navigating, hide on completion
            MyWebView.NavigationStarting += (_, __) =>
            {
                LoadingOverlay.Visibility = Visibility.Visible;
                MyWebView.Visibility = Visibility.Collapsed;
            };
            MyWebView.NavigationCompleted += MyWebView_NavigationCompleted;
        }

        private async void YouTubeMusicPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;
            await InitWebViewAsync();
        }

        private void YouTubeMusicPage_Unloaded(object sender, RoutedEventArgs e)
        {
            TryStopPlayback();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            TryStopPlayback();
            base.OnNavigatedFrom(e);
        }

        private async Task InitWebViewAsync()
        {
            try
            {
                // Use default environment to avoid overload issues
                await MyWebView.EnsureCoreWebView2Async();

                var core = MyWebView.CoreWebView2;
                var s = core.Settings;
                s.IsStatusBarEnabled = false;
                s.AreDevToolsEnabled = true;
                s.IsZoomControlEnabled = true;
                s.AreDefaultContextMenusEnabled = true;

                // Open popups in the same view
                core.NewWindowRequested += (s2, e2) =>
                {
                    e2.Handled = true;
                    if (!string.IsNullOrEmpty(e2.Uri))
                        core.Navigate(e2.Uri);
                };

                // Basic recovery if renderer crashes
                core.ProcessFailed += (_, __) =>
                {
                    try { core.Navigate(HomeUri.ToString()); } catch { }
                };

                // —— Full-screen album handling (centralized in MainWindow) ——
                core.ContainsFullScreenElementChanged += (_, __) =>
                {
                    try
                    {
                        if (core.ContainsFullScreenElement)
                            App.MainWindow?.EnterFullscreenMode();
                        else
                            App.MainWindow?.ExitFullscreenMode();
                    }
                    catch { /* ignore */ }
                };

                // Also inject a small helper to nudge the app when YT Music toggles big overlays/fullscreen UI.
                // (This complements ContainsFullScreenElement for edge cases.)
                await core.AddScriptToExecuteOnDocumentCreatedAsync(@"
(() => {
  const post = (msg) => { try { chrome.webview.postMessage({ type: 'ytm:ui', state: msg }); } catch (_) {} };
  document.addEventListener('fullscreenchange', () => {
    const fs = !!document.fullscreenElement;
    post(fs ? 'enter' : 'exit');
  });
  // Heuristic: watch for a very large player area that mimics a fullscreen album view.
  const checkBig = () => {
    const w = window.innerWidth, h = window.innerHeight;
    const vids = Array.from(document.querySelectorAll('video'));
    const nearFull = vids.some(v => v.clientWidth > w * 0.9 && v.clientHeight > h * 0.9);
    post(nearFull ? 'enter' : 'exit');
  };
  let t1, t2;
  t1 = setInterval(checkBig, 1200);
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') checkBig(); });
})();");

                core.WebMessageReceived += (s2, e2) =>
                {
                    try
                    {
                        var msg = e2.TryGetWebMessageAsString();
                        if (string.Equals(msg, "{\"type\":\"ytm:ui\",\"state\":\"enter\"}", StringComparison.OrdinalIgnoreCase))
                            App.MainWindow?.EnterFullscreenMode();
                        else if (string.Equals(msg, "{\"type\":\"ytm:ui\",\"state\":\"exit\"}", StringComparison.OrdinalIgnoreCase))
                            App.MainWindow?.ExitFullscreenMode();
                    }
                    catch { /* ignore */ }
                };
                // —— end fullscreen wiring ——

                MyWebView.Source = HomeUri;
            }
            catch
            {
                // Show the control even on init failure so the built-in error page appears
                LoadingOverlay.Visibility = Visibility.Collapsed;
                MyWebView.Visibility = Visibility.Visible;
            }
        }

        private void MyWebView_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            MyWebView.Visibility = Visibility.Visible;

            if (!args.IsSuccess)
            {
                try { MyWebView.CoreWebView2?.Navigate(HomeUri.ToString()); } catch { }
            }
        }

        /// <summary>
        /// Attempts to pause/mute all media and then navigates to about:blank to end playback.
        /// </summary>
        private void TryStopPlayback()
        {
            try
            {
                var core = MyWebView.CoreWebView2;
                if (core != null)
                {
                    // Best-effort: pause all videos, mute, clear sources, then blank.
                    _ = core.ExecuteScriptAsync(@"
(() => {
  try {
    const vids = Array.from(document.querySelectorAll('video'));
    for (const v of vids) {
      try { v.pause(); } catch {}
      try { v.muted = true; } catch {}
      try { v.src = ''; v.removeAttribute('src'); v.load(); } catch {}
    }
    // Also try the YouTube Music player API if present.
    try {
      const playBtn = document.querySelector('ytmusic-player-bar[playback-started] .pause') 
                   || document.querySelector('ytmusic-player-bar .pause');
      if (playBtn) { playBtn.click(); }
    } catch {}
  } catch {}
})();");

                    // Leave app fullscreen if we were in it
                    try { App.MainWindow?.ExitFullscreenMode(); } catch { }

                    // Finally clear to a blank page to ensure audio pipeline is torn down
                    core.Navigate("about:blank");
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
