using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Windows.Storage;
using Zink.Models;

namespace Zink.Services
{
    public sealed class LikedRadioLikesService
    {
        public static LikedRadioLikesService Instance { get; } = new();
        public ObservableCollection<LikedRadioSong> Liked { get; } = new();

        private readonly string _filePath =
            Path.Combine(ApplicationData.Current.LocalFolder.Path, "liked_radio_songs.json");

        private bool _isLoaded;

        private LikedRadioLikesService() { }

        public async Task LoadAsync()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _isLoaded = true;
                    return;
                }

                var json = await File.ReadAllTextAsync(_filePath);
                var items = JsonSerializer.Deserialize<LikedRadioSong[]>(json) ?? Array.Empty<LikedRadioSong>();

                Liked.Clear();
                foreach (var item in items.OrderByDescending(i => i.LikedAtUtc))
                    Liked.Add(item);

                _isLoaded = true;
            }
            catch
            {
                _isLoaded = true;
            }
        }

        public async Task EnsureLoadedAsync()
        {
            if (_isLoaded)
                return;

            await LoadAsync();
        }

        public async Task SaveAsync()
        {
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            var json = JsonSerializer.Serialize(Liked.ToArray(), opts);
            await File.WriteAllTextAsync(_filePath, json);
        }

        public async Task<LikedRadioSong> AddOrUpdateAsync(string title, string artist, string album, string artworkUrl, string stationName)
        {
            await EnsureLoadedAsync();

            title ??= "";
            artist ??= "";
            album ??= "";
            artworkUrl ??= "";
            stationName ??= "";

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
                throw new ArgumentException("Title or artist is required");

            var existing = Liked.FirstOrDefault(x =>
                string.Equals(x.Title ?? "", title, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Artist ?? "", artist, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(album)) existing.Album = album;
                if (!string.IsNullOrWhiteSpace(artworkUrl)) existing.ArtworkUrl = artworkUrl;
                if (!string.IsNullOrWhiteSpace(stationName)) existing.StationName = stationName;

                existing.LikedAtUtc = DateTime.UtcNow;

                ReorderToTop(existing);
                await SaveAsync();
                return existing;
            }

            var item = new LikedRadioSong
            {
                Title = title,
                Artist = artist,
                Album = album,
                ArtworkUrl = artworkUrl,
                StationName = stationName,
                LikedAtUtc = DateTime.UtcNow
            };

            Liked.Insert(0, item);
            await SaveAsync();
            return item;
        }

        public async Task RemoveAsync(Guid id)
        {
            await EnsureLoadedAsync();

            var target = Liked.FirstOrDefault(x => x.Id == id);
            if (target != null)
            {
                Liked.Remove(target);
                await SaveAsync();
            }
        }

        public async Task MarkAsAddedToSpotifyAsync(Guid id, string spotifyTrackId, string spotifyTrackUrl)
        {
            await EnsureLoadedAsync();

            var target = Liked.FirstOrDefault(x => x.Id == id);
            if (target == null)
                return;

            target.SpotifyTrackId = spotifyTrackId ?? "";
            target.SpotifyTrackUrl = spotifyTrackUrl ?? "";
            target.AddedToSpotifyLikedSongs = true;
            target.SpotifySyncedAtUtc = DateTime.UtcNow;

            await SaveAsync();
        }

        public async Task MarkYouTubeMatchAsync(Guid id, string youtubeVideoUrl)
        {
            await EnsureLoadedAsync();

            var target = Liked.FirstOrDefault(x => x.Id == id);
            if (target == null)
                return;

            target.YouTubeVideoUrl = youtubeVideoUrl ?? "";
            target.YouTubeMatchedAtUtc = string.IsNullOrWhiteSpace(youtubeVideoUrl) ? null : DateTime.UtcNow;

            await SaveAsync();
        }

        private void ReorderToTop(LikedRadioSong item)
        {
            var index = Liked.IndexOf(item);
            if (index > 0)
            {
                Liked.RemoveAt(index);
                Liked.Insert(0, item);
            }
        }
    }
}