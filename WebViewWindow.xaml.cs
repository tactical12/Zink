using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;

namespace Zink
{
    public sealed partial class WebViewWindow : Page
    {
        private AppWindow _appWindow;
        private CoreWebView2? _coreWebView;
        private readonly Window _window;
        private readonly string _stationName;
        private readonly string _streamUrl;

        private bool _audioHooked;
        private bool _hidden;
        private CancellationTokenSource? _hideDebounceCts;

        public WebViewWindow(string stationName, string streamUrl)
        {
            InitializeComponent();

            _stationName = stationName;
            _streamUrl = streamUrl;

            // Host this Page in its own Window
            _window = new Window { Content = this };
            _window.Activate();

            _ = InitializeWindowAsync();
        }

        public void StopStream()
        {
            try { _coreWebView?.Navigate("about:blank"); } catch { }
        }

        private async Task InitializeWindowAsync()
        {
            await Task.Delay(300);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var winId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(winId)!;
            _appWindow.Title = _stationName;

            var display = DisplayArea.GetFromWindowId(winId, DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            var width = (int)(work.Width * 0.75);
            var height = (int)(work.Height * 0.85);

            _appWindow.MoveAndResize(new RectInt32
            {
                X = work.X + (work.Width - width) / 2,
                Y = work.Y + (work.Height - height) / 2,
                Width = width,
                Height = height
            });

            await LoadAndMonitorAsync();
        }

        private async Task LoadAndMonitorAsync()
        {
            StreamTitle.Text = "Loading...";

            await RadioWebView.EnsureCoreWebView2Async();

            if (_coreWebView != null)
            {
                _coreWebView.NavigationCompleted -= CoreWebView_NavigationCompleted;
                if (_audioHooked)
                {
                    _coreWebView.IsDocumentPlayingAudioChanged -= CoreWebView_IsDocumentPlayingAudioChanged;
                    _audioHooked = false;
                }
            }

            _coreWebView = RadioWebView.CoreWebView2;

            // Hook BEFORE navigating so we don’t miss early playback
            _coreWebView.IsDocumentPlayingAudioChanged += CoreWebView_IsDocumentPlayingAudioChanged;
            _audioHooked = true;

            // Also check immediately in case audio is already playing
            if (_coreWebView.IsDocumentPlayingAudio)
                _ = DebouncedHideAsync();

            _coreWebView.NavigationCompleted += CoreWebView_NavigationCompleted;

            RadioWebView.Source = new Uri(_streamUrl);
        }

        private void CoreWebView_NavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (_hidden) return;

            var url = sender.Source;

            if (url.Contains("account.bbc.com", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("/signin", StringComparison.OrdinalIgnoreCase))
            {
                StreamTitle.Text = "Sign-in required…";
                return;
            }

            if (url.Contains("play/live", StringComparison.OrdinalIgnoreCase))
            {
                StreamTitle.Text = "Player loaded, waiting for audio…";
            }
        }

        private void CoreWebView_IsDocumentPlayingAudioChanged(CoreWebView2 sender, object args)
        {
            if (_hidden) return;

            if (sender.IsDocumentPlayingAudio)
            {
                // Start / restart debounce
                _ = DebouncedHideAsync();
            }
            else
            {
                // Audio stopped; cancel any pending hide
                try { _hideDebounceCts?.Cancel(); } catch { }
            }
        }

        private async Task DebouncedHideAsync()
        {
            try { _hideDebounceCts?.Cancel(); } catch { }
            _hideDebounceCts = new CancellationTokenSource();
            var ct = _hideDebounceCts.Token;

            // Wait 2s; only hide if audio is STILL playing
            try { await Task.Delay(2000, ct); } catch { return; }

            if (_coreWebView?.IsDocumentPlayingAudio == true && !_hidden)
            {
                _hidden = true;
                DispatcherQueue.TryEnqueue(() =>
                {
                    StreamTitle.Text = "Playing…";
                    // Hide (keep WebView alive so audio continues)
                    try { _appWindow.Hide(); } catch { /* fallback: shrink window */ }
                    // Fallback if Hide isn’t supported in some envs:
                    // _appWindow.MoveAndResize(new RectInt32 { X = -50000, Y = -50000, Width = 1, Height = 1 });
                    // or set Visibility collapse on content, but Hide should suffice.
                });
            }
        }
    }
}
