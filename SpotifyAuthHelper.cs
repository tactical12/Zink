using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.System;

namespace Zink
{
    public static class SpotifyAuthHelper
    {
        private const string ClientId = "2e88dcd486ec48aaaf54ac86e8c266a2";
        private const string ClientSecret = "1a0b102c111c4893a71a57a73c3423ee";
        private const string RedirectUri = "https://example.com/callback";

        private static readonly string AppFolder = ApplicationData.Current.LocalFolder.Path;
        private static readonly string TokenFile = Path.Combine(AppFolder, "spotify_token.txt");
        private static readonly string RefreshTokenFile = Path.Combine(AppFolder, "spotify_refresh_token.txt");
        private static readonly string CookieFile = Path.Combine(AppFolder, "spotify_cookies.txt");

        public static string AccessToken { get; private set; }

        public readonly struct SpotifyTrackMatch
        {
            public SpotifyTrackMatch(string trackId, string trackUrl)
            {
                TrackId = trackId ?? "";
                TrackUrl = trackUrl ?? "";
            }

            public string TrackId { get; }
            public string TrackUrl { get; }
        }

        public static async Task InitializeWebView2Async(WebView2 webView)
        {
            var env = await CoreWebView2Environment.CreateAsync();
            await webView.EnsureCoreWebView2Async(env);
        }

        public static async Task ExchangeCodeForTokenAsync(string code)
        {
            using var http = new HttpClient();

            var content = new StringContent(
                $"grant_type=authorization_code&code={Uri.EscapeDataString(code)}&redirect_uri={Uri.EscapeDataString(RedirectUri)}",
                Encoding.UTF8,
                "application/x-www-form-urlencoded");

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            var response = await http.PostAsync("https://accounts.spotify.com/api/token", content);
            response.EnsureSuccessStatusCode();

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            AccessToken = json.TryGetProperty("access_token", out var accessEl) ? accessEl.GetString() : null;
            string refreshToken = json.TryGetProperty("refresh_token", out var refreshEl) ? refreshEl.GetString() : null;

            Directory.CreateDirectory(AppFolder);

            if (!string.IsNullOrWhiteSpace(AccessToken))
                await File.WriteAllTextAsync(TokenFile, AccessToken);

            if (!string.IsNullOrWhiteSpace(refreshToken))
                await File.WriteAllTextAsync(RefreshTokenFile, refreshToken);
        }

        public static async Task LoadStoredTokenAsync()
        {
            try
            {
                Directory.CreateDirectory(AppFolder);

                if (File.Exists(TokenFile))
                    AccessToken = await File.ReadAllTextAsync(TokenFile);
            }
            catch { }
        }

        public static async Task RefreshAccessTokenAsync()
        {
            if (!File.Exists(RefreshTokenFile))
                return;

            string refreshToken = await File.ReadAllTextAsync(RefreshTokenFile);
            if (string.IsNullOrWhiteSpace(refreshToken))
                return;

            using var http = new HttpClient();

            var content = new StringContent(
                $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refreshToken)}",
                Encoding.UTF8,
                "application/x-www-form-urlencoded");

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            var response = await http.PostAsync("https://accounts.spotify.com/api/token", content);
            response.EnsureSuccessStatusCode();

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            AccessToken = json.TryGetProperty("access_token", out var accessEl) ? accessEl.GetString() : null;

            if (!string.IsNullOrWhiteSpace(AccessToken))
                await File.WriteAllTextAsync(TokenFile, AccessToken);
        }

        public static async Task<bool> EnsureAccessTokenAsync()
        {
            if (!string.IsNullOrWhiteSpace(AccessToken))
                return true;

            await LoadStoredTokenAsync();

            if (!string.IsNullOrWhiteSpace(AccessToken))
                return true;

            try
            {
                await RefreshAccessTokenAsync();
            }
            catch { }

            return !string.IsNullOrWhiteSpace(AccessToken);
        }

        public static async Task SaveCookiesAsync(WebView2 webView)
        {
            var cookieList = await webView.CoreWebView2.CookieManager.GetCookiesAsync("https://spotify.com");
            Directory.CreateDirectory(AppFolder);

            using var writer = new StreamWriter(CookieFile, false, Encoding.UTF8);

            foreach (var cookie in cookieList)
            {
                writer.WriteLine($"{cookie.Name}={cookie.Value}; Domain={cookie.Domain}; Path={cookie.Path}");
            }
        }

        public static async Task LoadCookiesAsync(WebView2 webView)
        {
            if (!File.Exists(CookieFile))
                return;

            using var reader = new StreamReader(CookieFile, Encoding.UTF8);
            while (!reader.EndOfStream)
            {
                var cookieData = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(cookieData))
                    continue;

                var cookieParts = cookieData.Split(';');
                if (cookieParts.Length < 3)
                    continue;

                var nameValue = cookieParts[0].Split('=', 2);
                if (nameValue.Length < 2)
                    continue;

                string name = nameValue[0].Trim();
                string value = nameValue[1].Trim();
                string domain = cookieParts[1].Replace("Domain=", "", StringComparison.OrdinalIgnoreCase).Trim();
                string path = cookieParts[2].Replace("Path=", "", StringComparison.OrdinalIgnoreCase).Trim();

                var cookie = webView.CoreWebView2.CookieManager.CreateCookie(name, value, domain, path);
                webView.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
            }
        }

        public static async Task<SpotifyTrackMatch?> SearchBestTrackAsync(string artist, string title, string album)
        {
            if (!await EnsureAccessTokenAsync())
                return null;

            using var http = CreateAuthorizedClient();

            string query = BuildSearchQuery(artist, title, album);
            string url = $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit=1";

            var response = await http.GetAsync(url);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return null;

                using var retryHttp = CreateAuthorizedClient();
                response = await retryHttp.GetAsync(url);
            }

            if (!response.IsSuccessStatusCode)
                return null;

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            if (!json.TryGetProperty("tracks", out var tracksObj))
                return null;

            if (!tracksObj.TryGetProperty("items", out var items))
                return null;

            if (items.GetArrayLength() == 0)
                return null;

            var first = items[0];

            string trackId = first.TryGetProperty("id", out var idEl) ? idEl.GetString() : "";
            string trackUrl = "";

            if (first.TryGetProperty("external_urls", out var extUrls) &&
                extUrls.TryGetProperty("spotify", out var spotifyUrlEl))
            {
                trackUrl = spotifyUrlEl.GetString() ?? "";
            }

            if (string.IsNullOrWhiteSpace(trackId))
                return null;

            return new SpotifyTrackMatch(trackId, trackUrl);
        }

        public static async Task<bool> AddTrackToLikedSongsAsync(string trackId)
        {
            if (string.IsNullOrWhiteSpace(trackId))
                return false;

            if (!await EnsureAccessTokenAsync())
                return false;

            string url = $"https://api.spotify.com/v1/me/tracks?ids={Uri.EscapeDataString(trackId)}";

            using var http = CreateAuthorizedClient();
            var response = await http.PutAsync(url, new StringContent(""));

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await RefreshAccessTokenAsync();
                if (string.IsNullOrWhiteSpace(AccessToken))
                    return false;

                using var retryHttp = CreateAuthorizedClient();
                response = await retryHttp.PutAsync(url, new StringContent(""));
            }

            return response.IsSuccessStatusCode;
        }

        public static async Task<bool> OpenTrackInSpotifyAsync(string artist, string title, string album)
        {
            var match = await SearchBestTrackAsync(artist, title, album);
            if (match == null || string.IsNullOrWhiteSpace(match.Value.TrackId))
                return false;

            var spotifyUri = new Uri($"spotify:track:{match.Value.TrackId}");

            try
            {
                var launchedSpotifyApp = await Launcher.LaunchUriAsync(spotifyUri);
                if (launchedSpotifyApp)
                    return true;
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(match.Value.TrackUrl))
            {
                try
                {
                    return await Launcher.LaunchUriAsync(new Uri(match.Value.TrackUrl));
                }
                catch { }
            }

            return false;
        }

        private static string BuildSearchQuery(string artist, string title, string album)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(title))
                sb.Append(title.Trim());

            if (!string.IsNullOrWhiteSpace(artist))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(artist.Trim());
            }

            if (!string.IsNullOrWhiteSpace(album))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(album.Trim());
            }

            return sb.ToString().Trim();
        }

        private static HttpClient CreateAuthorizedClient()
        {
            var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
            return http;
        }
    }
}