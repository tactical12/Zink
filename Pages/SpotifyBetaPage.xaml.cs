using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using DispatcherTimer = Microsoft.UI.Xaml.DispatcherTimer;

namespace Zink.Pages
{
    public sealed class SpotifyBetaTrack
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public string SpotifyUrl { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string Kind { get; set; } = "Track";
    }

    public sealed class SpotifyBetaDevice
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public bool IsActive { get; set; }
        public string DisplayName => string.IsNullOrWhiteSpace(Type) ? Name : $"{Name} - {Type}";
    }

    public sealed partial class SpotifyBetaPage : Page
    {
        private const string LocalRedirectUri = "http://127.0.0.1:43872/callback/";
        private readonly DispatcherTimer _connectClipboardTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private bool _isWaitingForConnectCode;

        public ObservableCollection<SpotifyBetaTrack> Tracks { get; } = new();
        public ObservableCollection<SpotifyBetaTrack> LibraryTracks { get; } = new();
        public ObservableCollection<SpotifyBetaTrack> Albums { get; } = new();
        public ObservableCollection<SpotifyBetaTrack> Recent { get; } = new();
        public ObservableCollection<SpotifyBetaTrack> ForYou { get; } = new();
        public ObservableCollection<SpotifyBetaTrack> Playlists { get; } = new();
        public ObservableCollection<SpotifyBetaTrack> Queue { get; } = new();
        public ObservableCollection<SpotifyBetaDevice> PlaybackDevices { get; } = new();

        private SpotifyBetaTrack? _heroItem;
        private bool _currentIsPlaying;
        private string _selectedPlaybackDeviceId = "";
        private string _zinkPlaybackDeviceId = "";
        private bool _zinkPlaybackReady;
        private bool _playbackSdkLoading;

        public SpotifyBetaPage()
        {
            InitializeComponent();
            LibraryList.ItemsSource = LibraryTracks;
            AlbumsList.ItemsSource = Albums;
            RecentList.ItemsSource = Recent;
            ForYouList.ItemsSource = ForYou;
            PlaylistsList.ItemsSource = Playlists;
            QueueList.ItemsSource = Queue;
            PlaybackDeviceBox.ItemsSource = PlaybackDevices;
            _connectClipboardTimer.Tick += ConnectClipboardTimer_Tick;
            Loaded += SpotifyBetaPage_Loaded;
        }

        private async void SpotifyBetaPage_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshConnectionAsync();
            await EnsureZinkPlaybackAsync();
            await RefreshCurrentPlaybackAsync();
            await LoadPlaybackDevicesAsync();
            await LoadSpotifyHomeAsync();
        }

        private async System.Threading.Tasks.Task RefreshConnectionAsync()
        {
            var connected = await SpotifyAuthHelper.EnsureAccessTokenAsync();
            ConnectionStatusText.Text = connected ? "Connected" : "Not connected";
            ConnectHintText.Text = connected
                ? "Connected through Spotify Web API. This page is fully native and does not load Spotify in a WebView."
                : "Connect opens Spotify in your browser. After sign-in, paste the callback URL or code here and press Finish.";
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ConnectButton.IsEnabled = false;
                ConnectionStatusText.Text = "Opening Spotify";
                await Launcher.LaunchUriAsync(new Uri(SpotifyAuthHelper.GetNativeAuthorizationUrl(SpotifyAuthHelper.DefaultRedirectUri, showDialog: true)));
                ConnectHintText.Text = "When Spotify redirects, copy the browser address or the code value, paste it here, then press Finish.";
                ConnectionStatusText.Text = "Waiting for code";
                _isWaitingForConnectCode = true;
                _connectClipboardTimer.Start();
            }
            catch
            {
                await ShowDialogAsync(
                    "Spotify connection failed",
                    $"Zink could not open Spotify sign-in. Make sure Spotify allows this redirect URL exactly: {SpotifyAuthHelper.DefaultRedirectUri}");
                await RefreshConnectionAsync();
            }
            finally
            {
                ConnectButton.IsEnabled = true;
            }
        }

        private async void ConnectClipboardTimer_Tick(object? sender, object e)
        {
            if (!_isWaitingForConnectCode)
                return;

            try
            {
                var data = Clipboard.GetContent();
                if (!data.Contains(StandardDataFormats.Text))
                    return;

                var text = await data.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text) ||
                    (!text.Contains("example.com/callback", StringComparison.OrdinalIgnoreCase) &&
                     !text.Contains("code=", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                ManualCodeInput.Text = text;
                await CompleteSpotifyConnectionFromTextAsync(text, showErrors: false);
            }
            catch
            {
            }
        }

        private async void FinishManualConnectButton_Click(object sender, RoutedEventArgs e)
        {
            await CompleteSpotifyConnectionFromTextAsync(ManualCodeInput.Text, showErrors: true);
        }

        private async Task CompleteSpotifyConnectionFromTextAsync(string text, bool showErrors)
        {
            try
            {
                var code = SpotifyAuthHelper.TryGetCodeFromRedirect(text);
                if (string.IsNullOrWhiteSpace(code))
                {
                    if (showErrors)
                        await ShowDialogAsync("Spotify code missing", "Paste the Spotify callback URL or the code value, then press Finish.");
                    return;
                }

                _connectClipboardTimer.Stop();
                _isWaitingForConnectCode = false;
                ConnectionStatusText.Text = "Connecting";
                await SpotifyAuthHelper.ExchangeCodeForTokenAsync(code, SpotifyAuthHelper.DefaultRedirectUri);
                ManualCodeInput.Text = "";
                await RefreshConnectionAsync();
                await EnsureZinkPlaybackAsync(forceReload: true);
                await RefreshCurrentPlaybackAsync();
                await LoadSpotifyHomeAsync();
            }
            catch
            {
                if (showErrors)
                    await ShowDialogAsync("Spotify connection failed", "Zink could not finish connecting Spotify with that callback URL or code.");
                await RefreshConnectionAsync();
            }
        }

        private async Task<string?> RunBrowserOAuthAsync()
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add(LocalRedirectUri);
            listener.Start();

            await Launcher.LaunchUriAsync(new Uri(SpotifyAuthHelper.GetNativeAuthorizationUrl(LocalRedirectUri, showDialog: true)));

            var contextTask = listener.GetContextAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(2));
            var completed = await Task.WhenAny(contextTask, timeoutTask);

            if (completed != contextTask)
                return null;

            var context = await contextTask;
            var code = context.Request.QueryString["code"];
            var error = context.Request.QueryString["error"];

            var html = string.IsNullOrWhiteSpace(error)
                ? "<html><body style=\"font-family:Segoe UI,Arial;background:#071016;color:white;padding:32px\"><h1>Spotify connected</h1><p>You can return to Zink now.</p></body></html>"
                : "<html><body style=\"font-family:Segoe UI,Arial;background:#071016;color:white;padding:32px\"><h1>Spotify connection cancelled</h1><p>You can return to Zink and try again.</p></body></html>";

            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            context.Response.Close();

            return string.IsNullOrWhiteSpace(error) ? code : null;
        }

        private async Task EnsureZinkPlaybackAsync(bool forceReload = false)
        {
            if (_playbackSdkLoading)
                return;

            if (_zinkPlaybackReady && !forceReload)
                return;

            var token = await SpotifyAuthHelper.GetAccessTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                return;

            try
            {
                _playbackSdkLoading = true;
                await SpotifyPlaybackWebView.EnsureCoreWebView2Async();
                SpotifyPlaybackWebView.CoreWebView2.WebMessageReceived -= SpotifyPlaybackWebView_WebMessageReceived;
                SpotifyPlaybackWebView.CoreWebView2.WebMessageReceived += SpotifyPlaybackWebView_WebMessageReceived;
                SpotifyPlaybackWebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
                SpotifyPlaybackWebView.NavigateToString(BuildSpotifyPlaybackHtml(token));
                ConnectHintText.Text = "Starting Zink Spotify playback engine...";
            }
            catch
            {
                ConnectHintText.Text = "Could not start the Zink Spotify playback engine.";
            }
            finally
            {
                _playbackSdkLoading = false;
            }
        }

        private async void SpotifyPlaybackWebView_WebMessageReceived(
            Microsoft.Web.WebView2.Core.CoreWebView2 sender,
            Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                var json = args.TryGetWebMessageAsString();
                if (string.IsNullOrWhiteSpace(json))
                    return;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? "" : "";

                if (type == "ready")
                {
                    _zinkPlaybackDeviceId = root.TryGetProperty("device_id", out var idEl) ? idEl.GetString() ?? "" : "";
                    _zinkPlaybackReady = !string.IsNullOrWhiteSpace(_zinkPlaybackDeviceId);

                    if (_zinkPlaybackReady)
                    {
                        UpsertZinkPlaybackDevice();
                        _selectedPlaybackDeviceId = _zinkPlaybackDeviceId;
                        await SpotifyAuthHelper.TransferPlaybackAsync(_zinkPlaybackDeviceId, play: false);
                        ConnectHintText.Text = "Zink Spotify Player is ready. Tracks will play through this app.";
                    }
                }
                else if (type == "error")
                {
                    var message = root.TryGetProperty("message", out var messageEl) ? messageEl.GetString() ?? "" : "";
                    ConnectHintText.Text = string.IsNullOrWhiteSpace(message)
                        ? "Spotify playback engine reported an error."
                        : message;
                }
            }
            catch
            {
            }
        }

        private void UpsertZinkPlaybackDevice()
        {
            if (string.IsNullOrWhiteSpace(_zinkPlaybackDeviceId))
                return;

            var existing = PlaybackDevices.FirstOrDefault(x => x.Id == _zinkPlaybackDeviceId);
            if (existing == null)
            {
                existing = new SpotifyBetaDevice
                {
                    Id = _zinkPlaybackDeviceId,
                    Name = "Zink Spotify Player",
                    Type = "This app",
                    IsActive = true
                };
                PlaybackDevices.Insert(0, existing);
            }

            PlaybackDeviceBox.SelectedItem = existing;
        }

        private static string BuildSpotifyPlaybackHtml(string accessToken)
        {
            var tokenJson = JsonSerializer.Serialize(accessToken);
            return $$"""
<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <meta http-equiv="Content-Security-Policy" content="default-src 'self' https://sdk.scdn.co https://*.spotify.com https://*.scdn.co https://*.spotifycdn.com wss://*.spotify.com; script-src 'self' 'unsafe-inline' https://sdk.scdn.co; connect-src https://*.spotify.com https://*.scdn.co wss://*.spotify.com; media-src https://*.spotify.com https://*.scdn.co blob:; img-src https://*.scdn.co https://*.spotifycdn.com data:; style-src 'unsafe-inline';">
</head>
<body style="margin:0;background:#071016;color:white;font-family:Segoe UI,Arial">
  <script src="https://sdk.scdn.co/spotify-player.js"></script>
  <script>
    let accessToken = {{tokenJson}};
    let zinkPlayer = null;
    const post = (payload) => {
      try { chrome.webview.postMessage(JSON.stringify(payload)); } catch {}
    };

    window.onSpotifyWebPlaybackSDKReady = () => {
      zinkPlayer = new Spotify.Player({
        name: 'Zink Spotify Player',
        getOAuthToken: cb => cb(accessToken),
        volume: 0.75
      });

      zinkPlayer.addListener('ready', ({ device_id }) => post({ type: 'ready', device_id }));
      zinkPlayer.addListener('not_ready', ({ device_id }) => post({ type: 'not_ready', device_id }));
      zinkPlayer.addListener('initialization_error', ({ message }) => post({ type: 'error', message }));
      zinkPlayer.addListener('authentication_error', ({ message }) => post({ type: 'error', message: 'Spotify playback authentication failed. Reconnect Spotify and approve the streaming scope.' }));
      zinkPlayer.addListener('account_error', ({ message }) => post({ type: 'error', message: 'Spotify Premium is required for in-app playback.' }));
      zinkPlayer.addListener('playback_error', ({ message }) => post({ type: 'error', message }));
      zinkPlayer.addListener('player_state_changed', state => post({ type: 'state', paused: state ? state.paused : true }));

      window.zinkSpotifyActivate = () => zinkPlayer && zinkPlayer.activateElement();
      window.zinkSpotifySetVolume = value => zinkPlayer && zinkPlayer.setVolume(Math.max(0, Math.min(1, Number(value) || 0)));

      zinkPlayer.connect().then(success => {
        if (!success) post({ type: 'error', message: 'Could not connect Zink Spotify Player.' });
      });
    };
  </script>
</body>
</html>
""";
        }

        private async void LibraryButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadSpotifyHomeAsync();
        }

        private async void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadPlaybackDevicesAsync();
        }

        private void PlaybackDeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlaybackDeviceBox.SelectedItem is SpotifyBetaDevice device)
                _selectedPlaybackDeviceId = device.Id;
        }

        private async void HomeNav_Click(object sender, RoutedEventArgs e)
        {
            await RefreshCurrentPlaybackAsync();
            await LoadSpotifyHomeAsync();
        }

        private void ExploreNav_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Focus(FocusState.Programmatic);
        }

        private async void FavoritesNav_Click(object sender, RoutedEventArgs e)
        {
            if (!await SpotifyAuthHelper.EnsureAccessTokenAsync())
            {
                await RefreshConnectionAsync();
                return;
            }

            LibraryTracks.Clear();
            AddRange(LibraryTracks, (await SpotifyAuthHelper.GetSavedTracksAsync(30)).Select(ToTrack));
            ForYou.Clear();
            AddRange(ForYou, LibraryTracks);
            SetHero(LibraryTracks.FirstOrDefault());
            ConnectHintText.Text = LibraryTracks.Count == 0 ? "No liked songs found." : "Favorites loaded from Spotify Liked Songs.";
        }

        private async Task LoadPlaybackDevicesAsync()
        {
            PlaybackDevices.Clear();

            if (!await SpotifyAuthHelper.EnsureAccessTokenAsync())
                return;

            if (_zinkPlaybackReady)
                UpsertZinkPlaybackDevice();

            var devices = await SpotifyAuthHelper.GetAvailableDevicesAsync();
            foreach (var device in devices)
            {
                if (!string.IsNullOrWhiteSpace(_zinkPlaybackDeviceId) &&
                    string.Equals(device.Id, _zinkPlaybackDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                PlaybackDevices.Add(new SpotifyBetaDevice
                {
                    Id = device.Id,
                    Name = device.Name,
                    Type = device.Type,
                    IsActive = device.IsActive
                });
            }

            var active = PlaybackDevices.FirstOrDefault(x => x.Id == _zinkPlaybackDeviceId) ??
                         PlaybackDevices.FirstOrDefault(x => x.IsActive) ??
                         PlaybackDevices.FirstOrDefault();
            if (active != null)
            {
                PlaybackDeviceBox.SelectedItem = active;
                _selectedPlaybackDeviceId = active.Id;
                ConnectHintText.Text = $"Playback source: {active.DisplayName}";
            }
            else
            {
                _selectedPlaybackDeviceId = "";
                ConnectHintText.Text = "No Spotify playback source found. Open Spotify once on this PC or another device, then refresh sources.";
            }
        }

        private async Task<bool> EnsurePlaybackDeviceAsync(bool play = true)
        {
            await EnsureZinkPlaybackAsync();

            if (string.IsNullOrWhiteSpace(_selectedPlaybackDeviceId))
                await LoadPlaybackDevicesAsync();

            if (string.IsNullOrWhiteSpace(_selectedPlaybackDeviceId))
            {
                await ShowDialogAsync(
                    "No playback source",
                    "Spotify has not reported an available playback source. Open Spotify once on this PC, phone, or speaker, then press the refresh button beside Playback Source.");
                return false;
            }

            try
            {
                if (SpotifyPlaybackWebView.CoreWebView2 != null)
                    await SpotifyPlaybackWebView.CoreWebView2.ExecuteScriptAsync("window.zinkSpotifyActivate && window.zinkSpotifyActivate();");
            }
            catch
            {
            }

            var transferred = await SpotifyAuthHelper.TransferPlaybackAsync(_selectedPlaybackDeviceId, play);
            if (!transferred)
            {
                await ShowDialogAsync(
                    "Playback source unavailable",
                    "Zink could not transfer Spotify playback to the selected source. Make sure Spotify is open on that device, then refresh sources.");
                return false;
            }

            return true;
        }

        private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            await SearchAsync(args.QueryText);
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await SearchAsync(SearchBox.Text);
        }

        private async System.Threading.Tasks.Task SearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return;

            if (!await SpotifyAuthHelper.EnsureAccessTokenAsync())
            {
                await ShowDialogAsync("Spotify is not connected", "Connect Spotify first, then search again.");
                await RefreshConnectionAsync();
                return;
            }

            SearchBox.IsEnabled = false;
            Tracks.Clear();
            ConnectHintText.Text = "Searching Spotify...";

            try
            {
                var results = await SpotifyAuthHelper.SearchTracksAsync(query);

                foreach (var item in results)
                {
                    Tracks.Add(new SpotifyBetaTrack
                    {
                        Id = item.Id,
                        Title = item.Title,
                        Artist = item.Artist,
                        Album = item.Album,
                        ImageUrl = item.ImageUrl,
                        SpotifyUrl = item.SpotifyUrl,
                        Subtitle = item.Subtitle,
                        Kind = item.Kind
                    });
                }

                ForYou.Clear();
                AddRange(ForYou, Tracks);
                SetHero(Tracks.FirstOrDefault());
                ConnectHintText.Text = Tracks.Count == 0 ? "No Spotify tracks found." : "Search results loaded into For You.";
            }
            catch
            {
                ConnectHintText.Text = "Spotify search failed.";
            }
            finally
            {
                SearchBox.IsEnabled = true;
            }
        }

        private async System.Threading.Tasks.Task LoadSpotifyHomeAsync()
        {
            if (!await SpotifyAuthHelper.EnsureAccessTokenAsync())
            {
                EmptyLibraryText.Text = "Connect Spotify to load liked songs.";
                EmptyLibraryText.Visibility = Visibility.Visible;
                await RefreshConnectionAsync();
                return;
            }

            ConnectHintText.Text = "Loading live Spotify data...";
            LibraryTracks.Clear();
            Albums.Clear();
            Recent.Clear();
            ForYou.Clear();
            Playlists.Clear();
            Queue.Clear();
            EmptyLibraryText.Text = "Loading queue...";
            EmptyLibraryText.Visibility = Visibility.Visible;

            try
            {
                var profileTask = SpotifyAuthHelper.GetCurrentUserProfileAsync();
                var savedTask = SpotifyAuthHelper.GetSavedTracksAsync(12);
                var albumsTask = SpotifyAuthHelper.GetSavedAlbumsAsync(10);
                var recentTask = SpotifyAuthHelper.GetRecentlyPlayedAsync(10);
                var topTask = SpotifyAuthHelper.GetTopTracksAsync(8);
                var playlistsTask = SpotifyAuthHelper.GetCurrentUserPlaylistsAsync(10);
                var queueTask = SpotifyAuthHelper.GetQueueAsync(8);

                await Task.WhenAll(profileTask, savedTask, albumsTask, recentTask, topTask, playlistsTask, queueTask);

                var profile = await profileTask;
                if (profile != null)
                {
                    ProfileNameText.Text = string.IsNullOrWhiteSpace(profile.DisplayName) ? "Spotify" : profile.DisplayName;
                    SetImage(ProfileImage, profile.ImageUrl);
                }

                AddRange(LibraryTracks, (await savedTask).Select(ToTrack));
                AddRange(Albums, (await albumsTask).Select(ToTrack));
                AddRange(Recent, (await recentTask).Select(ToTrack));
                AddRange(ForYou, (await topTask).Select(ToTrack));
                AddRange(Playlists, (await playlistsTask).Select(ToTrack));
                AddRange(Queue, (await queueTask).Select(ToTrack));

                if (ForYou.Count == 0)
                    AddRange(ForYou, LibraryTracks.Take(8));

                if (Albums.Count == 0)
                    AddRange(Albums, LibraryTracks.Take(10));

                if (Recent.Count == 0)
                    AddRange(Recent, LibraryTracks.Skip(2).Take(10));

                if (Queue.Count == 0)
                    AddRange(Queue, Recent.Count > 0 ? Recent.Take(8) : LibraryTracks.Take(8));

                var hero = Recent.FirstOrDefault() ?? ForYou.FirstOrDefault() ?? Albums.FirstOrDefault() ?? LibraryTracks.FirstOrDefault() ?? Playlists.FirstOrDefault();
                SetHero(hero);

                EmptyLibraryText.Text = Queue.Count == 0 ? "No queue data from Spotify." : "";
                EmptyLibraryText.Visibility = Queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ConnectHintText.Text = "Live Spotify data is loaded from your account.";
            }
            catch
            {
                EmptyLibraryText.Text = "Could not load Spotify data.";
                EmptyLibraryText.Visibility = Visibility.Visible;
            }
            finally
            {
            }
        }

        private async void PlayTrack_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string trackId || string.IsNullOrWhiteSpace(trackId))
                return;

            try
            {
                button.IsEnabled = false;
                if (!await EnsurePlaybackDeviceAsync(play: true))
                    return;

                var started = await SpotifyAuthHelper.StartPlaybackAsync(trackId, _selectedPlaybackDeviceId);

                if (!started)
                {
                    var track = FindTrack(trackId);
                    await ShowDialogAsync(
                        "Playback failed",
                        string.IsNullOrWhiteSpace(track?.Title)
                            ? "Spotify could not start this track on the selected playback source."
                            : $"Spotify could not start {track.Title} on the selected playback source.");
                }

                await RefreshCurrentPlaybackAsync();
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private async void SaveTrack_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string trackId || string.IsNullOrWhiteSpace(trackId))
                return;

            try
            {
                button.IsEnabled = false;
                var saved = await SpotifyAuthHelper.AddTrackToLikedSongsAsync(trackId);

                await ShowDialogAsync(
                    saved ? "Saved to Spotify" : "Spotify save failed",
                    saved ? "The track was added to your Spotify Liked Songs." : "Zink could not add this track to your Spotify Liked Songs.");
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private async void OpenTrack_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
                await Launcher.LaunchUriAsync(uri);
        }

        private async void RefreshCurrentButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshCurrentPlaybackAsync();
        }

        private async void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIsPlaying)
            {
                await SpotifyAuthHelper.PausePlaybackAsync();
            }
            else
            {
                if (!await EnsurePlaybackDeviceAsync(play: false))
                    return;

                await SpotifyAuthHelper.ResumePlaybackAsync();
            }

            await RefreshCurrentPlaybackAsync();
        }

        private async void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await EnsurePlaybackDeviceAsync(play: false))
                return;

            await SpotifyAuthHelper.PreviousTrackAsync();
            await RefreshCurrentPlaybackAsync();
            await LoadQueueOnlyAsync();
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await EnsurePlaybackDeviceAsync(play: false))
                return;

            await SpotifyAuthHelper.NextTrackAsync();
            await RefreshCurrentPlaybackAsync();
            await LoadQueueOnlyAsync();
        }

        private void PreviousButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            PreviousButton_Click(sender, new RoutedEventArgs());
        }

        private void NextButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            NextButton_Click(sender, new RoutedEventArgs());
        }

        private async void RefreshNowPlaying_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await RefreshCurrentPlaybackAsync();
        }

        private async void RefreshHome_Tapped(object sender, TappedRoutedEventArgs e)
        {
            await RefreshCurrentPlaybackAsync();
            await LoadSpotifyHomeAsync();
        }

        private async void ClearQueue_Click(object sender, RoutedEventArgs e)
        {
            Queue.Clear();
            EmptyLibraryText.Text = "Queue cleared locally. Spotify does not expose a clear-queue command.";
            EmptyLibraryText.Visibility = Visibility.Visible;
            await RefreshCurrentPlaybackAsync();
        }

        private async void ShowAlbums_Click(object sender, RoutedEventArgs e)
        {
            ForYou.Clear();
            AddRange(ForYou, Albums);
            SetHero(Albums.FirstOrDefault());
            ConnectHintText.Text = Albums.Count == 0 ? "No saved albums found." : "Saved albums shown in For You.";
            await Task.CompletedTask;
        }

        private async void ShowRecent_Click(object sender, RoutedEventArgs e)
        {
            ForYou.Clear();
            AddRange(ForYou, Recent);
            SetHero(Recent.FirstOrDefault());
            ConnectHintText.Text = Recent.Count == 0 ? "No recently played tracks found." : "Recently played tracks shown in For You.";
            await Task.CompletedTask;
        }

        private async void SpotifyItem_Click(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not SpotifyBetaTrack item)
                return;

            await ActivateSpotifyItemAsync(item);
        }

        private async void PlayHeroButton_Click(object sender, RoutedEventArgs e)
        {
            if (_heroItem == null)
                return;

            if (string.Equals(_heroItem.Kind, "Track", StringComparison.OrdinalIgnoreCase))
            {
                if (!await EnsurePlaybackDeviceAsync(play: true))
                    return;

                await SpotifyAuthHelper.StartPlaybackAsync(_heroItem.Id, _selectedPlaybackDeviceId);
                await RefreshCurrentPlaybackAsync();
                return;
            }

            ConnectHintText.Text = $"{_heroItem.Kind} selected. Choose a track from the page to play it on the PC source.";
        }

        private async void SaveHeroButton_Click(object sender, RoutedEventArgs e)
        {
            if (_heroItem == null)
                return;

            if (string.Equals(_heroItem.Kind, "Track", StringComparison.OrdinalIgnoreCase))
                await SpotifyAuthHelper.AddTrackToLikedSongsAsync(_heroItem.Id);
            else if (string.Equals(_heroItem.Kind, "Album", StringComparison.OrdinalIgnoreCase))
                await SpotifyAuthHelper.SaveAlbumAsync(_heroItem.Id);
            else
                ConnectHintText.Text = $"{_heroItem.Kind} save is not supported by Spotify Web API here.";

            await LoadSpotifyHomeAsync();
        }

        private async System.Threading.Tasks.Task RefreshCurrentPlaybackAsync()
        {
            try
            {
                var current = await SpotifyAuthHelper.GetCurrentPlaybackAsync();

                if (current == null)
                {
                    NowPlayingTitle.Text = "Nothing playing";
                    NowPlayingArtist.Text = "Open Spotify on one of your devices to start playback.";
                    NowPlayingAlbum.Text = "";
                    BottomTitleText.Text = "Nothing playing";
                    BottomArtistText.Text = "Spotify";
                    NowPlayingArt.Source = null;
                    BottomNowPlayingArt.Source = null;
                    NowPlayingFallbackIcon.Visibility = Visibility.Visible;
                    NowPlayingProgress.Value = 0;
                    _currentIsPlaying = false;
                    return;
                }

                NowPlayingTitle.Text = current.Title;
                NowPlayingArtist.Text = string.IsNullOrWhiteSpace(current.Artist)
                    ? current.Album
                    : current.Artist;
                NowPlayingAlbum.Text = current.Album;
                BottomTitleText.Text = current.Title;
                BottomArtistText.Text = string.IsNullOrWhiteSpace(current.Artist) ? current.Album : current.Artist;
                NowPlayingProgress.Maximum = Math.Max(1, current.DurationMs);
                NowPlayingProgress.Value = Math.Clamp(current.ProgressMs, 0, Math.Max(1, current.DurationMs));
                _currentIsPlaying = current.IsPlaying;
                SetImage(NowPlayingArt, current.ImageUrl);
                SetImage(BottomNowPlayingArt, current.ImageUrl);
                NowPlayingFallbackIcon.Visibility = string.IsNullOrWhiteSpace(current.ImageUrl) ? Visibility.Visible : Visibility.Collapsed;
            }
            catch
            {
                NowPlayingTitle.Text = "Spotify unavailable";
                NowPlayingArtist.Text = "Check your connection and try again.";
            }
        }

        private void TrackImage_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Image image && image.DataContext is SpotifyBetaTrack track)
                SetImage(image, track.ImageUrl);
        }

        private SpotifyBetaTrack? FindTrack(string id)
        {
            foreach (var track in Tracks)
            {
                if (string.Equals(track.Id, id, StringComparison.OrdinalIgnoreCase))
                    return track;
            }

            foreach (var track in LibraryTracks)
            {
                if (string.Equals(track.Id, id, StringComparison.OrdinalIgnoreCase))
                    return track;
            }

            return null;
        }

        private void SetHero(SpotifyBetaTrack? item)
        {
            _heroItem = item;

            if (item == null)
            {
                HeroBadgeText.Text = "SPOTIFY";
                HeroTitleText.Text = "Connect Spotify";
                HeroSubtitleText.Text = "Real Spotify data will fill this home surface.";
                HeroImage.Source = null;
                return;
            }

            HeroBadgeText.Text = string.IsNullOrWhiteSpace(item.Subtitle) ? item.Kind.ToUpperInvariant() : item.Subtitle.ToUpperInvariant();
            HeroTitleText.Text = item.Title;
            HeroSubtitleText.Text = string.IsNullOrWhiteSpace(item.Artist)
                ? item.Album
                : $"{item.Artist}  -  {item.Album}";
            SetImage(HeroImage, item.ImageUrl);
        }

        private async Task ActivateSpotifyItemAsync(SpotifyBetaTrack item)
        {
            _heroItem = item;
            SetHero(item);

            if (string.Equals(item.Kind, "Track", StringComparison.OrdinalIgnoreCase))
            {
                if (!await EnsurePlaybackDeviceAsync(play: true))
                    return;

                var started = await SpotifyAuthHelper.StartPlaybackAsync(item.Id, _selectedPlaybackDeviceId);
                if (!started)
                    await ShowDialogAsync("Playback failed", $"Spotify could not start {item.Title} on the selected playback source.");

                await RefreshCurrentPlaybackAsync();
                await LoadQueueOnlyAsync();
                return;
            }

            ConnectHintText.Text = $"{item.Kind} selected. Spotify only allows direct in-app playback for track IDs here.";
        }

        private async Task LoadQueueOnlyAsync()
        {
            Queue.Clear();
            AddRange(Queue, (await SpotifyAuthHelper.GetQueueAsync(8)).Select(ToTrack));

            if (Queue.Count == 0)
                AddRange(Queue, Recent.Count > 0 ? Recent.Take(8) : LibraryTracks.Take(8));

            EmptyLibraryText.Text = Queue.Count == 0 ? "No queue data from Spotify." : "";
            EmptyLibraryText.Visibility = Queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void AddRange(ObservableCollection<SpotifyBetaTrack> target, IEnumerable<SpotifyBetaTrack> items)
        {
            foreach (var item in items)
                target.Add(item);
        }

        private static SpotifyBetaTrack ToTrack(SpotifyAuthHelper.SpotifyTrackResult item)
        {
            return new SpotifyBetaTrack
            {
                Id = item.Id,
                Title = item.Title,
                Artist = item.Artist,
                Album = item.Album,
                ImageUrl = item.ImageUrl,
                SpotifyUrl = item.SpotifyUrl,
                Subtitle = item.Subtitle,
                Kind = item.Kind
            };
        }

        private static void SetImage(Image image, string url)
        {
            try
            {
                image.Source = string.IsNullOrWhiteSpace(url)
                    ? null
                    : new BitmapImage(new Uri(url));
            }
            catch
            {
                image.Source = null;
            }
        }

        private async System.Threading.Tasks.Task ShowDialogAsync(string title, string message)
        {
            await new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            }.ShowAsync();
        }
    }
}
