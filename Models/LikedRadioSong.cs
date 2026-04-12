using System;

namespace Zink.Models
{
    public sealed class LikedRadioSong
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string ArtworkUrl { get; set; } = "";
        public string StationName { get; set; } = "";
        public DateTime LikedAtUtc { get; set; } = DateTime.UtcNow;

        public string SpotifyTrackId { get; set; } = "";
        public string SpotifyTrackUrl { get; set; } = "";
        public bool AddedToSpotifyLikedSongs { get; set; } = false;
        public DateTime? SpotifySyncedAtUtc { get; set; }

        public string YouTubeVideoUrl { get; set; } = "";
        public DateTime? YouTubeMatchedAtUtc { get; set; }

        public string SpotifyButtonText => AddedToSpotifyLikedSongs ? "Added to Spotify" : "Add to Spotify";
        public bool CanAddToSpotify => !AddedToSpotifyLikedSongs;
    }
}