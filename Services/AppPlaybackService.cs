using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Zink.Services
{
    /// <summary>
    /// Single source of truth for now-playing. 
    /// - Supports INotifyPropertyChanged (bindings)
    /// - Exposes legacy events + Current* properties so existing pages/widgets keep working.
    /// </summary>
    public sealed class AppPlaybackService : INotifyPropertyChanged
    {
        public static AppPlaybackService Instance { get; } = new();

        public enum MediaKind
        {
            None,
            Music,
            Video,
            Radio
        }

        private AppPlaybackService()
        {
            // Always get the dispatcher for the current UI thread
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (_, __) => RaiseOnUI(() => OnPropertyChanged(nameof(Elapsed)));
            _tick.Start();
        }

        private readonly DispatcherQueue? _dispatcher;
        private readonly DispatcherTimer _tick;

        // ================= RADIO backing fields (kept) =================
        private string _stationTitle = "—";
        private Uri? _stationLogoUri;

        private string _artistName = "";
        private string _songTitle = "";
        private TimeSpan? _duration = null;   // null => live
        private Uri? _artistImageUri = null;

        private DateTimeOffset _startedAt = default;
        private bool _isPlaying;

        // ================= GENERIC now-playing (Music/Video/Radi) =================
        private MediaKind _currentKind = MediaKind.None;
        private string _primaryText = "";
        private string _secondaryText = "";
        private Uri? _genericArtworkUri = null;

        public MediaKind CurrentKind
        {
            get => _currentKind;
            private set { if (_currentKind != value) { _currentKind = value; OnPropertyChanged(); } }
        }

        public string PrimaryText
        {
            get => _primaryText;
            private set { if (!string.Equals(_primaryText, value, StringComparison.Ordinal)) { _primaryText = value; OnPropertyChanged(); } }
        }

        public string SecondaryText
        {
            get => _secondaryText;
            private set { if (!string.Equals(_secondaryText, value, StringComparison.Ordinal)) { _secondaryText = value; OnPropertyChanged(); } }
        }

        public Uri? GenericArtworkUri
        {
            get => _genericArtworkUri;
            private set { if (_genericArtworkUri != value) { _genericArtworkUri = value; OnPropertyChanged(); } }
        }

        // ================= INotifyPropertyChanged model (your newer code) =================
        public string StationTitle
        {
            get => _stationTitle;
            private set { if (!string.Equals(_stationTitle, value, StringComparison.Ordinal)) { _stationTitle = value; OnPropertyChanged(); } }
        }
        public string ArtistName
        {
            get => _artistName;
            private set { if (!string.Equals(_artistName, value, StringComparison.Ordinal)) { _artistName = value; OnPropertyChanged(); } }
        }
        public string SongTitle
        {
            get => _songTitle;
            private set { if (!string.Equals(_songTitle, value, StringComparison.Ordinal)) { _songTitle = value; OnPropertyChanged(); } }
        }

        public TimeSpan Elapsed
        {
            get
            {
                if (_startedAt == default) return TimeSpan.Zero;
                var span = DateTimeOffset.Now - _startedAt;
                return span < TimeSpan.Zero ? TimeSpan.Zero : span;
            }
        }

        public TimeSpan? Duration
        {
            get => _duration;
            private set { if (_duration != value) { _duration = value; OnPropertyChanged(); } }
        }

        /// Prefer artist image; fallback to station logo.
        public Uri? ArtworkOrLogo => _artistImageUri ?? _stationLogoUri;

        public bool IsPlaying
        {
            get => _isPlaying;
            private set
            {
                if (_isPlaying != value)
                {
                    _isPlaying = value;
                    OnPropertyChanged();
                    SafeFire(IsPlayingChanged);
                }
            }
        }

        // ================= Back-compat aliases (what your pages expect) =================
        public string CurrentStationTitle => StationTitle;

        // ✅ HomeDashboardPage expects this
        public Uri? CurrentStationLogoUri => _stationLogoUri;

        public string CurrentArtist => ArtistName;
        public string CurrentTitle => SongTitle;

        // Keep as-is (some of your code uses this)
        public Uri CurrentArtworkUri => _artistImageUri;

        // Events your pages/widgets subscribe to
        public event EventHandler NowPlayingChanged;
        public event EventHandler IsPlayingChanged;

        // ================= Public API used by RadioPage / ICY watcher =================
        public void SetStationPlaying(string stationId, string stationTitle, Uri? stationLogoUri, bool isPlaying)
        {
            RaiseOnUI(() =>
            {
                StationTitle = stationTitle ?? "—";
                _stationLogoUri = stationLogoUri;

                OnPropertyChanged(nameof(CurrentStationLogoUri));
                OnPropertyChanged(nameof(ArtworkOrLogo));

                // ✅ set generic "now playing" for dashboard
                CurrentKind = MediaKind.Radio;
                PrimaryText = StationTitle;
                SecondaryText = "";
                GenericArtworkUri = ArtworkOrLogo ?? _stationLogoUri;

                IsPlaying = isPlaying;
                SafeFire(NowPlayingChanged);
            });
        }

        public void UpdateNowPlaying(string artist, string title, TimeSpan? duration = null, Uri? artistImageUri = null)
        {
            RaiseOnUI(() =>
            {
                ArtistName = artist ?? "";
                SongTitle = title ?? "";
                Duration = duration;
                _artistImageUri = artistImageUri;
                _startedAt = DateTimeOffset.Now;

                OnPropertyChanged(nameof(ArtworkOrLogo));
                OnPropertyChanged(nameof(Elapsed)); // reset immediately

                // ✅ keep dashboard fields updated during radio metadata changes
                if (CurrentKind == MediaKind.Radio)
                {
                    PrimaryText = StationTitle;
                    SecondaryText = string.IsNullOrWhiteSpace(ArtistName) && string.IsNullOrWhiteSpace(SongTitle)
                        ? ""
                        : (string.IsNullOrWhiteSpace(ArtistName) ? SongTitle : $"{ArtistName} - {SongTitle}");
                    GenericArtworkUri = ArtworkOrLogo ?? _stationLogoUri;
                }

                SafeFire(NowPlayingChanged);
            });
        }

        public void SetIsPlaying(bool isPlaying) => RaiseOnUI(() => IsPlaying = isPlaying);

        // ✅ NEW: used by MusicPlayerPage + VideoPlayerPage
        public void SetGenericNowPlaying(MediaKind kind, string primary, string secondary, Uri? artworkUri, bool isPlaying)
        {
            RaiseOnUI(() =>
            {
                CurrentKind = kind;
                PrimaryText = primary ?? "";
                SecondaryText = secondary ?? "";
                GenericArtworkUri = artworkUri;

                _startedAt = DateTimeOffset.Now;
                OnPropertyChanged(nameof(Elapsed));

                IsPlaying = isPlaying;
                SafeFire(NowPlayingChanged);
            });
        }

        public void ClearIfKind(MediaKind kind)
        {
            RaiseOnUI(() =>
            {
                if (CurrentKind != kind) return;

                CurrentKind = MediaKind.None;
                PrimaryText = "";
                SecondaryText = "";
                GenericArtworkUri = null;

                _startedAt = default;
                OnPropertyChanged(nameof(Elapsed));

                IsPlaying = false;
                SafeFire(NowPlayingChanged);
            });
        }

        // ================= Widget → player intents =================
        public event Action? PlayRequested;
        public event Action? PauseRequested;
        public event Action? StopRequested;

        public void RequestPlay() { try { PlayRequested?.Invoke(); } catch { } }
        public void RequestPause() { try { PauseRequested?.Invoke(); } catch { } }
        public void RequestStop() { try { StopRequested?.Invoke(); } catch { } }

        // ================= INotifyPropertyChanged =================
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ================= Helpers =================
        private void SafeFire(EventHandler ev)
        {
            try { ev?.Invoke(this, EventArgs.Empty); } catch { }
        }

        private void RaiseOnUI(Action action)
        {
            if (_dispatcher == null) { try { action(); } catch { } return; }
            if (_dispatcher.HasThreadAccess) { action(); }
            else _dispatcher.TryEnqueue(() => { try { action(); } catch { } });
        }
    }
}
