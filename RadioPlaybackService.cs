using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Zink.Services;

namespace Zink
{
    public sealed class RadioStation
    {
        public string Title { get; set; }
        public Uri StreamUri { get; set; }
        public Uri LogoUri { get; set; }               // e.g. ms-appx:///Assets/Radio/...
        public bool IsHls { get; set; }                 // optional hint; not strictly required here
        public string MetadataUrlOverride { get; set; } // optional JSON endpoint for now-playing
    }

    public sealed class RadioPlaybackService
    {
        public static RadioPlaybackService Instance { get; } = new RadioPlaybackService();

        private RadioPlaybackService() { _mediaPlayer = MediaPlayerSingleton.Instance; }

        private readonly MediaPlayer _mediaPlayer;
        private CancellationTokenSource _metaCts;
        private readonly IcyMetadataReader _icy = new();

        public RadioStation CurrentStation { get; private set; }
        public string NowPlayingTitle { get; private set; }
        public string NowPlayingArtist { get; private set; }
        public Uri ArtworkUri { get; private set; }
        public bool IsPlaying => _mediaPlayer.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing;
        public double Volume { get => _mediaPlayer.Volume; set => _mediaPlayer.Volume = value; }

        public event EventHandler OnStationChanged;
        public event EventHandler OnNowPlayingChanged;
        public event EventHandler OnPlaybackStateChanged;

        public async Task PlayAsync(RadioStation station)
        {
            CurrentStation = station;

            // Build a MediaPlaybackItem so we can listen for timed metadata (ID3 on HLS etc.)
            var src = MediaSource.CreateFromUri(station.StreamUri);
            var item = new MediaPlaybackItem(src);

            // Hook existing tracks (if any) and future changes
            item.TimedMetadataTracksChanged += Item_TimedMetadataTracksChanged;
            foreach (var track in item.TimedMetadataTracks)
                HookTimedMetadataTrack(track);

            _mediaPlayer.Source = item;
            _mediaPlayer.Play();

            OnStationChanged?.Invoke(this, EventArgs.Empty);
            OnPlaybackStateChanged?.Invoke(this, EventArgs.Empty);

            // Stop prior metadata polling and start fresh
            _metaCts?.Cancel();
            _metaCts = new CancellationTokenSource();

            if (!string.IsNullOrWhiteSpace(station.MetadataUrlOverride))
            {
                _ = PollJsonNowPlaying(station.MetadataUrlOverride, _metaCts.Token);
            }
            else
            {
                _ = _icy.StartAsync(station.StreamUri, UpdateFromIcy, _metaCts.Token);
            }

            // Seed UI with station logo until we resolve artwork
            NowPlayingTitle = null;
            NowPlayingArtist = null;
            ArtworkUri = station.LogoUri;
            OnNowPlayingChanged?.Invoke(this, EventArgs.Empty);
        }

        public void TogglePlayPause()
        {
            if (IsPlaying) _mediaPlayer.Pause(); else _mediaPlayer.Play();
            OnPlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            try
            {
                _metaCts?.Cancel();
                _mediaPlayer.Pause();
                _mediaPlayer.Source = null;
            }
            finally
            {
                OnPlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task PollJsonNowPlaying(string url, CancellationToken ct)
        {
            using var http = new HttpClient();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var json = await http.GetStringAsync(url, ct);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    var title = root.TryGetProperty("title", out var t) ? t.GetString() :
                                root.TryGetProperty("song", out var s) ? s.GetString() : null;
                    var artist = root.TryGetProperty("artist", out var a) ? a.GetString() : null;

                    UpdateNowPlaying(title, artist);
                }
                catch
                {
                    // swallow and retry
                }

                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
        }

        private async void UpdateFromIcy(string combined)
        {
            string artist = null, title = null;

            if (!string.IsNullOrWhiteSpace(combined))
            {
                var parts = combined.Split(" - ", 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2) { artist = parts[0]; title = parts[1]; }
                else { title = combined; }
            }

            UpdateNowPlaying(title, artist);
            await FetchArtworkAsync(title, artist);
        }

        private void UpdateNowPlaying(string title, string artist)
        {
            bool changed =
                !string.Equals(NowPlayingTitle, title, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(NowPlayingArtist, artist, StringComparison.OrdinalIgnoreCase);

            NowPlayingTitle = title;
            NowPlayingArtist = artist;

            if (changed) OnNowPlayingChanged?.Invoke(this, EventArgs.Empty);
        }

        private async Task FetchArtworkAsync(string title, string artist)
        {
            try
            {
                var uri = await ArtworkProvider.TryFindAsync(title, artist);
                if (uri != null)
                {
                    ArtworkUri = uri;
                    OnNowPlayingChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch
            {
                // non-fatal
            }
        }

        // ===== Timed metadata via MediaPlaybackItem / TimedMetadataTrack (ID3/HLS etc.) =====

        private void Item_TimedMetadataTracksChanged(MediaPlaybackItem sender, IVectorChangedEventArgs args)
        {
            // Hook any tracks (newly added or already present)
            foreach (var track in sender.TimedMetadataTracks)
                HookTimedMetadataTrack(track);
        }

        private void HookTimedMetadataTrack(TimedMetadataTrack track)
        {
            if (track == null) return;
            if (track.TrackKind != MediaTrackKind.TimedMetadata) return;

            // Avoid double subscription
            track.CueEntered -= Track_CueEntered;
            track.CueEntered += Track_CueEntered;
        }

        private async void Track_CueEntered(TimedMetadataTrack sender, MediaCueEventArgs args)
        {
            try
            {
                string text = null;

                // 1) DataCue (common for ID3 over HLS)
                if (args.Cue is DataCue dataCue && dataCue.Data is IBuffer buffer)
                {
                    using var reader = DataReader.FromBuffer(buffer);
                    var bytes = new byte[buffer.Length];
                    reader.ReadBytes(bytes);
                    text = System.Text.Encoding.UTF8.GetString(bytes);
                }

                // 2) TimedTextCue fallback (use Lines[].Text, not a nonexistent .Text property)
                if (string.IsNullOrWhiteSpace(text) && args.Cue is TimedTextCue ttc && ttc.Lines != null && ttc.Lines.Count > 0)
                {
                    text = string.Join(" ", ttc.Lines.Select(l => l.Text));
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    // Parse "Artist - Title" if possible
                    string artist = null, title = null;
                    var parts = text.Split(" - ", 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2) { artist = parts[0]; title = parts[1]; }
                    else { title = text; }

                    UpdateNowPlaying(title, artist);
                    await FetchArtworkAsync(title, artist);
                }
            }
            catch
            {
                // metadata formats vary a lot; ignore parse errors
            }
        }
    }
}
