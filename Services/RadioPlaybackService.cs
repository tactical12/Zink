using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Timers;

namespace Zink.Services
{
    /// Singleton: source of truth for what’s playing (page + widget).
    public sealed class RadioPlaybackService : INotifyPropertyChanged
    {
        public static RadioPlaybackService Instance { get; } = new();

        private RadioPlaybackService()
        {
            _tick.Interval = 1000;
            _tick.AutoReset = true;
            _tick.Elapsed += (_, __) => OnPropertyChanged(nameof(Elapsed));
            _tick.Start();
        }

        private readonly Timer _tick = new();

        private string _stationTitle = "";
        private Uri? _stationLogoUri;
        private string _artistName = "";
        private string _songTitle = "";
        private TimeSpan? _duration = null; // null => Live
        private Uri? _artistImageUri = null;
        private DateTimeOffset _startedAt = default;
        private bool _isPlaying;

        public string StationTitle { get => _stationTitle; private set { _stationTitle = value; OnPropertyChanged(); } }
        public string ArtistName { get => _artistName; private set { _artistName = value; OnPropertyChanged(); } }
        public string SongTitle { get => _songTitle; private set { _songTitle = value; OnPropertyChanged(); } }

        public TimeSpan Elapsed
        {
            get
            {
                if (_startedAt == default) return TimeSpan.Zero;
                var s = DateTimeOffset.Now - _startedAt;
                return s < TimeSpan.Zero ? TimeSpan.Zero : s;
            }
        }

        public TimeSpan? Duration { get => _duration; private set { _duration = value; OnPropertyChanged(); } }
        public Uri? ArtworkOrLogo => _artistImageUri ?? _stationLogoUri;
        public bool IsPlaying { get => _isPlaying; private set { _isPlaying = value; OnPropertyChanged(); } }

        // Called by your RadioPage when station changes / metadata arrives
        public void SetStationPlaying(string stationId, string stationTitle, Uri? stationLogoUri, bool isPlaying)
        {
            StationTitle = stationTitle ?? "";
            _stationLogoUri = stationLogoUri;
            IsPlaying = isPlaying;
            OnPropertyChanged(nameof(ArtworkOrLogo));
        }

        public void UpdateNowPlaying(string artist, string title, TimeSpan? duration = null, Uri? artistImageUri = null)
        {
            ArtistName = artist ?? "";
            SongTitle = title ?? "";
            Duration = duration;
            _artistImageUri = artistImageUri;
            _startedAt = DateTimeOffset.Now;
            OnPropertyChanged(nameof(ArtworkOrLogo));
            OnPropertyChanged(nameof(Elapsed));
        }

        public void SetIsPlaying(bool isPlaying) => IsPlaying = isPlaying;

        // Widget → player intents
        public event Action? PlayRequested;
        public event Action? PauseRequested;
        public event Action? StopRequested;
        public void RequestPlay() => PlayRequested?.Invoke();
        public void RequestPause() => PauseRequested?.Invoke();
        public void RequestStop() => StopRequested?.Invoke();

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
