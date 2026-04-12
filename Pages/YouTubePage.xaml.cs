using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Animation;
using System.Text.Json;
using Zink.Models;
using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class YouTubePage : Page
    {
        private AppWindow _appWindow;
        private bool _webViewInitialized;
        private bool _navigationHandlerAttached;
        private string _pendingSearchQuery;
        private bool _isResolvingBestMatch;
        private bool _hasShownCurrentLoad;
        private LikedRadioSong _pendingSong;
        private string _resolvedVideoUrl;

        public YouTubePage()
        {
            this.InitializeComponent();
            this.Loaded += YouTubePage_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _pendingSong = null;
            _pendingSearchQuery = null;
            _resolvedVideoUrl = null;

            if (e.Parameter is LikedRadioSong song)
            {
                _pendingSong = song;
                _pendingSearchQuery = BuildYouTubeQuery(song);
                _resolvedVideoUrl = song.YouTubeVideoUrl?.Trim();
            }
            else if (e.Parameter is string query && !string.IsNullOrWhiteSpace(query))
            {
                _pendingSearchQuery = query.Trim();
            }

            if (_webViewInitialized)
            {
                _ = NavigateToInitialTargetAsync();
            }
        }

        private async void YouTubePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_webViewInitialized)
            {
                await NavigateToInitialTargetAsync();
                return;
            }

            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZinkYouTubeWebViewData");

            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataFolder, null);
            await YouTubeWebView.EnsureCoreWebView2Async(env);

            YouTubeWebView.CoreWebView2.ContainsFullScreenElementChanged += CoreWebView2_ContainsFullScreenElementChanged;

            if (!_navigationHandlerAttached)
            {
                YouTubeWebView.CoreWebView2.NavigationCompleted += YouTubeWebView_NavigationCompleted;
                _navigationHandlerAttached = true;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            _webViewInitialized = true;

            await NavigateToInitialTargetAsync();
        }

        private async Task NavigateToInitialTargetAsync()
        {
            if (YouTubeWebView?.CoreWebView2 == null)
                return;

            _hasShownCurrentLoad = false;

            YouTubeWebView.Visibility = Visibility.Collapsed;
            YouTubeWebView.Opacity = 0;
            YouTubeLoader.Opacity = 1;
            YouTubeLoader.Visibility = Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(_resolvedVideoUrl))
            {
                _isResolvingBestMatch = false;
                YouTubeWebView.Source = new Uri(_resolvedVideoUrl);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_pendingSearchQuery))
            {
                _isResolvingBestMatch = true;
                string searchUrl = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(_pendingSearchQuery)}";
                YouTubeWebView.Source = new Uri(searchUrl);
            }
            else
            {
                _isResolvingBestMatch = false;
                YouTubeWebView.Source = new Uri("https://www.youtube.com/");
            }

            await Task.CompletedTask;
        }

        private async void YouTubeWebView_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            await WaitForReadyStateAsync();

            string currentUrl = "";
            try
            {
                currentUrl = sender.Source ?? "";
            }
            catch { }

            if (_isResolvingBestMatch &&
                !string.IsNullOrWhiteSpace(_pendingSearchQuery) &&
                currentUrl.Contains("/results?", StringComparison.OrdinalIgnoreCase))
            {
                bool navigated = await TryNavigateToBestVideoWithRetriesAsync(_pendingSearchQuery);
                if (navigated)
                    return;
            }

            if (!string.IsNullOrWhiteSpace(currentUrl) &&
                currentUrl.Contains("watch?v=", StringComparison.OrdinalIgnoreCase))
            {
                _resolvedVideoUrl = currentUrl;

                if (_pendingSong != null && _pendingSong.Id != Guid.Empty)
                {
                    try
                    {
                        await LikedRadioLikesService.Instance.MarkYouTubeMatchAsync(_pendingSong.Id, currentUrl);
                    }
                    catch { }
                }
            }

            ShowWebViewOnce();
        }

        private async Task WaitForReadyStateAsync()
        {
            while (true)
            {
                try
                {
                    string result = await YouTubeWebView.CoreWebView2.ExecuteScriptAsync("document.readyState");
                    if (result.Contains("complete", StringComparison.OrdinalIgnoreCase))
                        break;
                }
                catch { }

                await Task.Delay(200);
            }
        }

        private async Task<bool> TryNavigateToBestVideoWithRetriesAsync(string query)
        {
            for (int i = 0; i < 15; i++)
            {
                bool navigated = await TryNavigateToBestVideoAsync(query);
                if (navigated)
                {
                    _isResolvingBestMatch = false;
                    return true;
                }

                await Task.Delay(350);
            }

            _isResolvingBestMatch = false;
            return false;
        }

        private async Task<bool> TryNavigateToBestVideoAsync(string query)
        {
            string[] parts = SplitArtistAndTitle(query);
            string artist = parts[0];
            string title = parts[1];

            string artistJson = JsonSerializer.Serialize(artist ?? "");
            string titleJson = JsonSerializer.Serialize(title ?? "");

            string script = $@"
(function() {{
    function normalize(text) {{
        return (text || '')
            .toLowerCase()
            .replace(/[^\w\s]/g, ' ')
            .replace(/\s+/g, ' ')
            .trim();
    }}

    function containsAllWords(haystack, needle) {{
        var n = normalize(needle);
        if (!n) return false;
        var words = n.split(' ').filter(Boolean);
        if (words.length === 0) return false;
        var h = normalize(haystack);
        return words.every(function(w) {{ return h.indexOf(w) >= 0; }});
    }}

    function parseDurationSeconds(durationText) {{
        var raw = (durationText || '').trim();
        if (!raw) return -1;

        var parts = raw.split(':').map(function(p) {{ return parseInt(p, 10); }});
        if (parts.some(function(n) {{ return isNaN(n); }})) return -1;

        if (parts.length === 2) return (parts[0] * 60) + parts[1];
        if (parts.length === 3) return (parts[0] * 3600) + (parts[1] * 60) + parts[2];

        return -1;
    }}

    var artist = {artistJson};
    var title = {titleJson};
    var artistNorm = normalize(artist);
    var titleNorm = normalize(title);

    var badWords = [
        'cover','karaoke','instrumental','slowed','reverb','nightcore',
        'remix','reaction','live','concert','fan made','fanmade','8d',
        'sped up','spedup','edit audio','bass boosted','tribute'
    ];

    var goodWords = [
        'official audio','official video','audio','topic','vevo','visualizer','lyrics'
    ];

    var anchors = Array.from(document.querySelectorAll('a#video-title[href*=""watch?v=""]'));

    if (!anchors || anchors.length === 0)
        return JSON.stringify({{ found: false, reason: 'no-results' }});

    var best = null;
    var bestScore = -999999;

    for (var i = 0; i < anchors.length; i++) {{
        var a = anchors[i];
        var titleText = normalize(a.textContent || a.title || '');
        var container = a.closest('ytd-video-renderer,ytd-rich-item-renderer,ytd-compact-video-renderer') || a.parentElement || document;
        var channelAnchor = container.querySelector('#channel-name a, ytd-channel-name a');
        var channelText = normalize(channelAnchor ? channelAnchor.textContent : '');

        var durationElement =
            container.querySelector('ytd-thumbnail-overlay-time-status-renderer span') ||
            container.querySelector('#text.ytd-thumbnail-overlay-time-status-renderer') ||
            container.querySelector('.ytd-thumbnail-overlay-time-status-renderer');

        var durationText = durationElement ? durationElement.textContent : '';
        var durationSeconds = parseDurationSeconds(durationText);

        var score = 0;

        if (artistNorm && titleText.indexOf(artistNorm) >= 0) score += 90;
        if (titleNorm && titleText.indexOf(titleNorm) >= 0) score += 130;

        if (artistNorm && containsAllWords(titleText, artistNorm)) score += 35;
        if (titleNorm && containsAllWords(titleText, titleNorm)) score += 55;

        if (artistNorm && channelText.indexOf(artistNorm) >= 0) score += 55;
        if (channelText.indexOf('topic') >= 0) score += 40;
        if (channelText.indexOf('vevo') >= 0) score += 35;

        for (var g = 0; g < goodWords.length; g++) {{
            if (titleText.indexOf(goodWords[g]) >= 0) score += 12;
        }}

        for (var b = 0; b < badWords.length; b++) {{
            if (titleText.indexOf(badWords[b]) >= 0) score -= 70;
        }}

        if (artistNorm && titleNorm && titleText.indexOf(artistNorm) >= 0 && titleText.indexOf(titleNorm) >= 0)
            score += 80;

        if (durationSeconds > 0) {{
            if (durationSeconds >= 90 && durationSeconds <= 420) score += 35;
            else if (durationSeconds > 420 && durationSeconds <= 900) score += 5;
            else if (durationSeconds > 900) score -= 70;
            else if (durationSeconds < 60) score -= 45;
        }}

        if (!best || score > bestScore) {{
            best = a;
            bestScore = score;
        }}
    }}

    if (!best)
        return JSON.stringify({{ found: false, reason: 'no-best' }});

    var href = best.href || '';
    if (!href)
        return JSON.stringify({{ found: false, reason: 'no-href' }});

    if (href.startsWith('/'))
        href = 'https://www.youtube.com' + href;

    window.location.href = href;
    return JSON.stringify({{ found: true, href: href }});
}})();";

            try
            {
                string result = await YouTubeWebView.CoreWebView2.ExecuteScriptAsync(script);
                if (string.IsNullOrWhiteSpace(result))
                    return false;

                return result.Contains(@"""found"":true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string[] SplitArtistAndTitle(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new[] { "", "" };

            int sep = query.IndexOf(" - ", StringComparison.Ordinal);
            if (sep > 0)
            {
                string artist = query.Substring(0, sep).Trim();
                string title = query.Substring(sep + 3).Trim();
                return new[] { artist, title };
            }

            return new[] { "", query.Trim() };
        }

        private static string BuildYouTubeQuery(LikedRadioSong song)
        {
            string artist = song?.Artist?.Trim() ?? "";
            string title = song?.Title?.Trim() ?? "";

            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                return $"{artist} - {title}";

            if (!string.IsNullOrWhiteSpace(title))
                return title;

            if (!string.IsNullOrWhiteSpace(artist))
                return artist;

            return "";
        }

        private void ShowWebViewOnce()
        {
            if (_hasShownCurrentLoad)
                return;

            _hasShownCurrentLoad = true;

            DispatcherQueue.TryEnqueue(() =>
            {
                YouTubeWebView.Visibility = Visibility.Visible;

                var fadeIn = (Storyboard)Resources["FadeInWebViewStoryboard"];
                var fadeOut = (Storyboard)Resources["FadeOutLoaderStoryboard"];
                fadeIn.Begin();
                fadeOut.Begin();
            });
        }

        private void CoreWebView2_ContainsFullScreenElementChanged(CoreWebView2 sender, object args)
        {
            if (_appWindow == null) return;

            if (sender.ContainsFullScreenElement)
            {
                _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                App.MainWindow.SetSidebarVisibility(false);
            }
            else
            {
                _appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                App.MainWindow.SetSidebarVisibility(true);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            _pendingSearchQuery = null;
            _isResolvingBestMatch = false;
            _hasShownCurrentLoad = false;
            _pendingSong = null;
            _resolvedVideoUrl = null;

            if (YouTubeWebView != null)
            {
                YouTubeWebView.Source = new Uri("about:blank");
            }
        }
    }
}