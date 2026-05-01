using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Zink.Services;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Zink
{
    public sealed partial class HomePage : Page
    {
        private readonly DispatcherTimer _spotifyNowPlayingTimer;
        private GlobalSystemMediaTransportControlsSessionManager? _mediaSessionManager;
        private string _lastSpotifyDisplay = string.Empty;
        private DateTimeOffset _lastSpotifyDiagnosticAtUtc;

        public HomePage()
        {
            this.InitializeComponent();

            _spotifyNowPlayingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _spotifyNowPlayingTimer.Tick += async (_, __) => await RefreshSpotifyNowPlayingAsync();

            Loaded += HomePage_Loaded;
            Unloaded += HomePage_Unloaded;
        }

        private async void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            await RefreshSpotifyNowPlayingAsync();
            _spotifyNowPlayingTimer.Start();
        }

        private void HomePage_Unloaded(object sender, RoutedEventArgs e)
        {
            _spotifyNowPlayingTimer.Stop();
        }

        private async Task RefreshSpotifyNowPlayingAsync()
        {
            try
            {
                _mediaSessionManager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                var spotifySession = await FindSpotifySessionAsync(_mediaSessionManager);

                if (spotifySession == null)
                {
                    if (!TryShowSpotifyProcessNowPlaying())
                        ShowNoSpotifySession();

                    return;
                }

                var mediaProperties = await spotifySession.TryGetMediaPropertiesAsync();
                var playbackInfo = spotifySession.GetPlaybackInfo();
                var timeline = spotifySession.GetTimelineProperties();

                if (string.IsNullOrWhiteSpace(mediaProperties.Title) &&
                    TryShowSpotifyProcessNowPlaying())
                {
                    return;
                }

                var title = string.IsNullOrWhiteSpace(mediaProperties.Title) ? "Spotify" : mediaProperties.Title;
                var artist = string.IsNullOrWhiteSpace(mediaProperties.Artist) ? "Unknown artist" : mediaProperties.Artist;
                var album = string.IsNullOrWhiteSpace(mediaProperties.AlbumTitle) ? "" : mediaProperties.AlbumTitle;
                var status = playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    ? "Playing"
                    : playbackInfo.PlaybackStatus.ToString();

                SpotifyNowPlayingTitle.Text = title;
                SpotifyNowPlayingArtist.Text = artist;
                SpotifyNowPlayingAlbum.Text = album;
                SpotifyNowPlayingStatus.Text = status;
                SpotifyNowPlayingProgress.IsIndeterminate = false;

                UpdateSpotifyProgress(timeline);

                var displayKey = $"{title}|{artist}|{album}";
                if (!string.Equals(displayKey, _lastSpotifyDisplay, StringComparison.Ordinal))
                {
                    _lastSpotifyDisplay = displayKey;
                    await SetSpotifyThumbnailAsync(mediaProperties.Thumbnail);
                }
            }
            catch
            {
                if (!TryShowSpotifyProcessNowPlaying())
                    SpotifyNowPlayingStatus.Text = "Unavailable";
            }
        }

        private async Task<GlobalSystemMediaTransportControlsSession?> FindSpotifySessionAsync(
            GlobalSystemMediaTransportControlsSessionManager manager)
        {
            GlobalSystemMediaTransportControlsSession? currentPlayingFallback = null;
            var current = manager.GetCurrentSession();
            var diagnostics = new StringBuilder();

            foreach (var session in manager.GetSessions())
            {
                var source = session.SourceAppUserModelId ?? "";
                var status = session.GetPlaybackInfo()?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
                var title = "";
                var artist = "";

                try
                {
                    var properties = await session.TryGetMediaPropertiesAsync();
                    title = properties.Title ?? "";
                    artist = properties.Artist ?? "";
                }
                catch
                {
                }

                diagnostics.Append(source)
                    .Append(" | ")
                    .Append(status)
                    .Append(" | ")
                    .Append(title)
                    .Append(" | ")
                    .Append(artist)
                    .AppendLine();

                if (source.Contains("Spotify", StringComparison.OrdinalIgnoreCase) ||
                    IsLikelySpotifySession(source, title, artist))
                {
                    return session;
                }

                if (session == current &&
                    status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing &&
                    (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(artist)))
                {
                    currentPlayingFallback = session;
                }
            }

            WriteSpotifyDiagnosticsThrottled("No Spotify-named media session. Available sessions:\n" + diagnostics);
            return currentPlayingFallback;
        }

        private static bool IsLikelySpotifySession(string source, string title, string artist)
        {
            if (source.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
                return true;

            if (source.Contains("com.squirrel", StringComparison.OrdinalIgnoreCase) &&
                source.Contains("spotify", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrWhiteSpace(title) &&
                !string.IsNullOrWhiteSpace(artist) &&
                !source.Contains("Zink", StringComparison.OrdinalIgnoreCase))
            {
                return source.Contains("Music", StringComparison.OrdinalIgnoreCase) ||
                    source.Contains("Shell", StringComparison.OrdinalIgnoreCase) ||
                    source.Contains("Windows", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private bool TryShowSpotifyProcessNowPlaying()
        {
            try
            {
                var title = Process.GetProcessesByName("Spotify")
                    .Select(process =>
                    {
                        try { return process.MainWindowTitle; }
                        catch { return ""; }
                    })
                    .FirstOrDefault(IsUsableSpotifyWindowTitle);

                if (string.IsNullOrWhiteSpace(title))
                {
                    WriteSpotifyDiagnosticsThrottled("Spotify process fallback found no usable Spotify.exe window title.");
                    return false;
                }

                var trackTitle = title;
                var artist = "Spotify desktop app";

                var separatorIndex = title.IndexOf(" - ", StringComparison.Ordinal);
                if (separatorIndex > 0 && separatorIndex + 3 < title.Length)
                {
                    artist = title[..separatorIndex].Trim();
                    trackTitle = title[(separatorIndex + 3)..].Trim();
                }

                _lastSpotifyDisplay = title;
                SpotifyNowPlayingTitle.Text = trackTitle;
                SpotifyNowPlayingArtist.Text = artist;
                SpotifyNowPlayingAlbum.Text = "Desktop app";
                SpotifyNowPlayingStatus.Text = "Playing";
                SpotifyNowPlayingTime.Text = "";
                SpotifyNowPlayingProgress.IsIndeterminate = true;
                SpotifyNowPlayingArt.Source = null;
                SpotifyNowPlayingFallbackIcon.Visibility = Visibility.Visible;
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine("[Home:Spotify] Process fallback failed: " + ex.Message);
                return false;
            }
        }

        private void WriteSpotifyDiagnosticsThrottled(string message)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastSpotifyDiagnosticAtUtc < TimeSpan.FromSeconds(10))
                return;

            _lastSpotifyDiagnosticAtUtc = now;
            DiagnosticLogService.WriteLine("[Home:Spotify] " + message);
        }

        private static bool IsUsableSpotifyWindowTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return false;

            var text = title.Trim();
            return !string.Equals(text, "Spotify", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("Spotify Free", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("Spotify Premium", StringComparison.OrdinalIgnoreCase);
        }

        private void ShowNoSpotifySession()
        {
            _lastSpotifyDisplay = string.Empty;
            SpotifyNowPlayingTitle.Text = "Nothing playing from Spotify";
            SpotifyNowPlayingArtist.Text = "Open the Spotify desktop app and start a song";
            SpotifyNowPlayingAlbum.Text = "";
            SpotifyNowPlayingStatus.Text = "Idle";
            SpotifyNowPlayingProgress.IsIndeterminate = false;
            SpotifyNowPlayingProgress.Value = 0;
            SpotifyNowPlayingTime.Text = "0:00 / 0:00";
            SpotifyNowPlayingArt.Source = null;
            SpotifyNowPlayingFallbackIcon.Visibility = Visibility.Visible;
        }

        private void UpdateSpotifyProgress(GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
        {
            var duration = timeline.EndTime - timeline.StartTime;
            var position = timeline.Position - timeline.StartTime;

            if (duration.TotalSeconds <= 0)
            {
                SpotifyNowPlayingProgress.Value = 0;
                SpotifyNowPlayingTime.Text = "0:00 / 0:00";
                return;
            }

            SpotifyNowPlayingProgress.Value = Math.Clamp(position.TotalSeconds / duration.TotalSeconds * 100.0, 0, 100);
            SpotifyNowPlayingTime.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
        }

        private async Task SetSpotifyThumbnailAsync(IRandomAccessStreamReference? thumbnail)
        {
            try
            {
                if (thumbnail == null)
                {
                    SpotifyNowPlayingArt.Source = null;
                    SpotifyNowPlayingFallbackIcon.Visibility = Visibility.Visible;
                    return;
                }

                using var stream = await thumbnail.OpenReadAsync();
                var image = new BitmapImage();
                await image.SetSourceAsync(stream);
                SpotifyNowPlayingArt.Source = image;
                SpotifyNowPlayingFallbackIcon.Visibility = Visibility.Collapsed;
            }
            catch (FileNotFoundException)
            {
                SpotifyNowPlayingArt.Source = null;
                SpotifyNowPlayingFallbackIcon.Visibility = Visibility.Visible;
            }
            catch
            {
                SpotifyNowPlayingArt.Source = null;
                SpotifyNowPlayingFallbackIcon.Visibility = Visibility.Visible;
            }
        }

        private static string FormatTime(TimeSpan time)
        {
            if (time < TimeSpan.Zero)
                time = TimeSpan.Zero;

            return time.TotalHours >= 1
                ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
                : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
        }
    }
}
