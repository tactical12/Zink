using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Zink.Pages;

namespace Zink.Services
{
    /// <summary>
    /// Global search across Music Library (folder token), Video Library (persisted json), and Radio stations (static list).
    /// </summary>
    public sealed class SearchService
    {
        public static SearchService Current { get; } = new SearchService();

        private readonly SemaphoreSlim _cacheGate = new(1, 1);

        private string? _cachedMusicToken;
        private DateTime _cachedMusicAtUtc = DateTime.MinValue;
        private List<SongResult> _cachedSongs = new();

        private DateTime _cachedVideoAtUtc = DateTime.MinValue;
        private List<VideoResult> _cachedVideos = new();

        private SearchService() { }

        public async Task<SearchResults> SearchAsync(string query)
        {
            query ??= string.Empty;
            var q = query.Trim();

            // Empty query => empty results (keeps UI clean)
            if (string.IsNullOrWhiteSpace(q))
                return new SearchResults();

            // Ensure sources loaded/cached
            var songs = await GetSongsAsync().ConfigureAwait(false);
            var videos = await GetVideosAsync().ConfigureAwait(false);
            var stations = GetStations();

            // Filter
            var songsFiltered = songs
                .Where(s => ContainsAny(s.Title, s.Artist, s.Album, q))
                .OrderBy(s => s.Title)
                .Take(50)
                .ToList();

            // Albums / Artists derived from songs
            var albumsFiltered = songs
                .Where(s => !string.IsNullOrWhiteSpace(s.Album) && ContainsAny(s.Album, q))
                .GroupBy(s => s.Album, StringComparer.OrdinalIgnoreCase)
                .Select(g => new AlbumResult { Name = g.First().Album, SampleArtist = g.First().Artist, SongCount = g.Count() })
                .OrderBy(a => a.Name)
                .Take(30)
                .ToList();

            var artistsFiltered = songs
                .Where(s => !string.IsNullOrWhiteSpace(s.Artist) && ContainsAny(s.Artist, q))
                .GroupBy(s => s.Artist, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ArtistResult { Name = g.First().Artist, SongCount = g.Count() })
                .OrderBy(a => a.Name)
                .Take(30)
                .ToList();

            var videosFiltered = videos
                .Where(v => ContainsAny(v.Name, v.FileName, q))
                .OrderBy(v => v.Name)
                .Take(50)
                .ToList();

            var stationsFiltered = stations
                .Where(s => ContainsAny(s.Title, q))
                .OrderBy(s => s.Title)
                .Take(50)
                .ToList();

            return new SearchResults
            {
                Query = q,
                Songs = songsFiltered,
                Albums = albumsFiltered,
                Artists = artistsFiltered,
                Videos = videosFiltered,
                Stations = stationsFiltered
            };
        }

        // ================= MUSIC =================

        private async Task<List<SongResult>> GetSongsAsync()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                var tokenObj = settings.Values["MusicLibraryFolderToken"];
                var token = tokenObj as string;

                if (string.IsNullOrWhiteSpace(token))
                    return new List<SongResult>();

                await _cacheGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Simple cache: refresh if token changed OR cache older than 60 seconds
                    if (!string.Equals(_cachedMusicToken, token, StringComparison.Ordinal) ||
                        (DateTime.UtcNow - _cachedMusicAtUtc).TotalSeconds > 60)
                    {
                        _cachedMusicToken = token;
                        _cachedMusicAtUtc = DateTime.UtcNow;

                        _cachedSongs = await LoadSongsFromTokenAsync(token).ConfigureAwait(false);
                    }

                    return _cachedSongs.ToList();
                }
                finally
                {
                    _cacheGate.Release();
                }
            }
            catch
            {
                return new List<SongResult>();
            }
        }

        private static async Task<List<SongResult>> LoadSongsFromTokenAsync(string token)
        {
            try
            {
                if (!StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
                    return new List<SongResult>();

                var folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(token);
                if (folder == null) return new List<SongResult>();

                var files = await GetAllMusicFilesAsync(folder).ConfigureAwait(false);

                var results = new List<SongResult>(files.Count);
                foreach (var file in files)
                {
                    var song = await TryReadSongAsync(file).ConfigureAwait(false);
                    if (song != null)
                        results.Add(song);
                }

                return results;
            }
            catch
            {
                return new List<SongResult>();
            }
        }

        private static async Task<List<StorageFile>> GetAllMusicFilesAsync(StorageFolder folder)
        {
            var supportedTypes = new[] { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".wma" };
            var files = new List<StorageFile>();
            var queue = new Queue<StorageFolder>();
            queue.Enqueue(folder);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                try
                {
                    var fileList = await current.GetFilesAsync();
                    files.AddRange(fileList.Where(f => supportedTypes.Contains(Path.GetExtension(f.Name).ToLowerInvariant())));

                    var subfolders = await current.GetFoldersAsync();
                    foreach (var sub in subfolders)
                        queue.Enqueue(sub);
                }
                catch { }
            }

            return files;
        }

        private static async Task<SongResult?> TryReadSongAsync(StorageFile file)
        {
            try
            {
                using var stream = await file.OpenStreamForReadAsync().ConfigureAwait(false);
                var tagFile = TagLib.File.Create(new StreamFileAbstraction(file.Name, stream, stream));
                var tag = tagFile.Tag;

                return new SongResult
                {
                    Title = string.IsNullOrEmpty(tag.Title) ? file.DisplayName : tag.Title,
                    Artist = tag.FirstPerformer ?? "",
                    Album = tag.Album ?? "",
                    FilePath = file.Path
                };
            }
            catch
            {
                return null;
            }
        }

        // ================= VIDEO =================

        private async Task<List<VideoResult>> GetVideosAsync()
        {
            try
            {
                await _cacheGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Simple cache: refresh if older than 60 seconds
                    if ((DateTime.UtcNow - _cachedVideoAtUtc).TotalSeconds > 60)
                    {
                        _cachedVideoAtUtc = DateTime.UtcNow;
                        _cachedVideos = await LoadPersistedVideoLibraryAsync().ConfigureAwait(false);
                    }

                    return _cachedVideos.ToList();
                }
                finally
                {
                    _cacheGate.Release();
                }
            }
            catch
            {
                return new List<VideoResult>();
            }
        }

        private static async Task<List<VideoResult>> LoadPersistedVideoLibraryAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync("video_library.json") as StorageFile;
                if (file == null)
                    return new List<VideoResult>();

                var json = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<VideoResult>();

                var save = JsonSerializer.Deserialize<VideoLibrarySaveDto>(json);
                if (save?.Items == null || save.Items.Count == 0)
                    return new List<VideoResult>();

                // Cache folder token -> StorageFolder
                var folderCache = new Dictionary<string, StorageFolder>(StringComparer.Ordinal);
                var results = new List<VideoResult>();

                foreach (var entry in save.Items)
                {
                    if (entry == null) continue;
                    if (string.IsNullOrWhiteSpace(entry.FolderToken)) continue;
                    if (string.IsNullOrWhiteSpace(entry.RelativePath)) continue;

                    StorageFolder? folder = null;
                    if (!folderCache.TryGetValue(entry.FolderToken, out folder))
                    {
                        if (!StorageApplicationPermissions.FutureAccessList.ContainsItem(entry.FolderToken))
                            continue;

                        folder = await StorageApplicationPermissions.FutureAccessList.GetFolderAsync(entry.FolderToken);
                        if (folder == null) continue;

                        folderCache[entry.FolderToken] = folder;
                    }

                    var fullPath = Path.Combine(folder.Path, entry.RelativePath);

                    results.Add(new VideoResult
                    {
                        Name = entry.Name ?? Path.GetFileNameWithoutExtension(entry.FileName ?? fullPath),
                        FileName = entry.FileName ?? Path.GetFileName(fullPath),
                        FullPath = fullPath
                    });
                }

                return results;
            }
            catch
            {
                return new List<VideoResult>();
            }
        }

        private sealed class VideoLibrarySaveDto
        {
            public List<string>? FolderTokens { get; set; }
            public List<VideoLibraryEntryDto>? Items { get; set; }
        }

        private sealed class VideoLibraryEntryDto
        {
            public string? Name { get; set; }
            public string? FileName { get; set; }
            public string? FolderToken { get; set; }
            public string? RelativePath { get; set; }
        }

        // ================= RADIO =================

        private static List<StationResult> GetStations()
        {
            // Kept in sync with RadioPage station list (static data, no UI dependency)
            return new List<StationResult>
            {
                new StationResult { Title = "Capital FM", Image = "ms-appx:///Assets/Radio/capitalfm.png", StreamUrl = "https://media-ssl.musicradio.com/CapitalUK" },
                new StationResult { Title = "Heart", Image = "ms-appx:///Assets/Radio/heartfm.png", StreamUrl = "https://media-ssl.musicradio.com/HeartUK" },
                new StationResult { Title = "Heart Milton Keynes", Image = "ms-appx:///Assets/Radio/heartfm.png", StreamUrl = "https://media-ssl.musicradio.com/HeartMiltonKeynesMP3" },
                new StationResult { Title = "Kiss FM", Image = "ms-appx:///Assets/Radio/kissfm.png", StreamUrl = "https://media-ssl.musicradio.com/Kiss" },
                new StationResult { Title = "Smooth Radio", Image = "ms-appx:///Assets/Radio/smoothradio.png", StreamUrl = "https://media-ssl.musicradio.com/SmoothUK" },
                new StationResult { Title = "Magic Radio", Image = "ms-appx:///Assets/Radio/magicradio.png", StreamUrl = "https://planetradio.co.uk/magic/player/" },

                new StationResult { Title = "BBC Radio 1", Image = "ms-appx:///Assets/Radio/bbcradio1.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_one" },
                new StationResult { Title = "BBC Radio 2", Image = "ms-appx:///Assets/Radio/bbcradio2.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_two" },
                new StationResult { Title = "BBC Radio 3", Image = "ms-appx:///Assets/Radio/bbcradio3.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_three" },
                new StationResult { Title = "BBC Radio 5 Live", Image = "ms-appx:///Assets/Radio/bbcradio5live.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_five_live" },
                new StationResult { Title = "BBC Radio 1Xtra", Image = "ms-appx:///Assets/Radio/bbc1xtra.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_1xtra" },

                new StationResult { Title = "Hits Radio", Image = "ms-appx:///Assets/Radio/hitsradio.png", StreamUrl = "https://hellorayo.co.uk/hits-radio/play?stationId=352" },
                new StationResult { Title = "Greatest Hits Radio", Image = "ms-appx:///Assets/Radio/greatesthitsradio.png", StreamUrl = "https://planetradio.co.uk/greatest-hits/player/" },

                new StationResult { Title = "talkSPORT", Image = "ms-appx:///Assets/Radio/talksport.png", StreamUrl = "https://radio.talksport.com/stream" },

                new StationResult { Title = "Absolute Radio", Image = "ms-appx:///Assets/Radio/absolute.png", StreamUrl = "https://planetradio.co.uk/absolute/player/" },
                new StationResult { Title = "Classic FM", Image = "ms-appx:///Assets/Radio/classicfm.png", StreamUrl = "https://media-ssl.musicradio.com/ClassicFM" },
                new StationResult { Title = "Radio X", Image = "ms-appx:///Assets/Radio/radiox.png", StreamUrl = "https://media-ssl.musicradio.com/RadioXUK" },

                new StationResult { Title = "Gem 106", Image = "ms-appx:///Assets/Radio/gem106.png", StreamUrl = "https://planetradio.co.uk/gem/player/" },
                new StationResult { Title = "Premier Christian Radio", Image = "ms-appx:///Assets/Radio/premier.png", StreamUrl = "https://www.premier.plus/" },
                new StationResult { Title = "BBC Radio Derby", Image = "ms-appx:///Assets/Radio/radioderby.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_radio_derby" },

                new StationResult { Title = "Jazz FM", Image = "ms-appx:///Assets/Radio/jazzfm.png", StreamUrl = "https://planetradio.co.uk/jazz-fm/player/" },
                new StationResult { Title = "MKFM", Image = "ms-appx:///Assets/Radio/mkfm.png", StreamUrl = "https://www.mkfm.com/on-air/radioplayer/" },

                new StationResult { Title = "BBC World Service", Image = "ms-appx:///Assets/Radio/bbcworld.png", StreamUrl = "https://www.bbc.co.uk/sounds/play/live:bbc_world_service" },
                new StationResult { Title = "LBC", Image = "ms-appx:///Assets/Radio/lbc.png", StreamUrl = "https://media-ssl.musicradio.com/LBCUK" },
                new StationResult { Title = "Times Radio", Image = "ms-appx:///Assets/Radio/timesradio.png", StreamUrl = "https://timesradio.wireless.radio/stream" },
                new StationResult { Title = "Capital Dance", Image = "ms-appx:///Assets/Radio/capitaldance.png", StreamUrl = "https://media-ssl.musicradio.com/CapitalDance" },

                new StationResult { Title = "Capital Xtra", Image = "ms-appx:///Assets/Radio/capitalxtra.png", StreamUrl = "https://www.globalplayer.com/live/capitalxtra/uk/" },
                new StationResult { Title = "Radio Essex", Image = "ms-appx:///Assets/Radio/radioessex.png", StreamUrl = "https://www.radioessex.com/player/" }
            };
        }

        // ================= HELPERS =================

        private static bool ContainsAny(string? a, string? b, string? c, string q)
        {
            return ContainsAny(a, q) || ContainsAny(b, q) || ContainsAny(c, q);
        }

        // ✅ ADDED: 2-field overload (fixes calls like ContainsAny(v.Name, v.FileName, q))
        private static bool ContainsAny(string? a, string? b, string q)
        {
            return ContainsAny(a, q) || ContainsAny(b, q);
        }

        private static bool ContainsAny(string? a, string q)
        {
            if (string.IsNullOrWhiteSpace(a)) return false;
            return a.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    public sealed class SearchResults
    {
        public string Query { get; set; } = "";
        public List<SongResult> Songs { get; set; } = new();
        public List<AlbumResult> Albums { get; set; } = new();
        public List<ArtistResult> Artists { get; set; } = new();
        public List<VideoResult> Videos { get; set; } = new();
        public List<StationResult> Stations { get; set; } = new();
    }

    public sealed class SongResult
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string FilePath { get; set; } = "";
    }

    public sealed class AlbumResult
    {
        public string Name { get; set; } = "";
        public string SampleArtist { get; set; } = "";
        public int SongCount { get; set; }
    }

    public sealed class ArtistResult
    {
        public string Name { get; set; } = "";
        public int SongCount { get; set; }
    }

    public sealed class VideoResult
    {
        public string Name { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
    }

    public sealed class StationResult
    {
        public string Title { get; set; } = "";
        public string Image { get; set; } = "";
        public string StreamUrl { get; set; } = "";
    }
}
