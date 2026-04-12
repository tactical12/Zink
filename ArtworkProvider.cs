using System;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

namespace Zink
{
    public static class ArtworkProvider
    {
        public static async Task<Uri> TryFindAsync(string title, string artist)
        {
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
                return null;

            var term = $"{artist} {title}".Trim();
            var url = $"https://itunes.apple.com/search?media=music&limit=1&term={UrlEncoder.Default.Encode(term)}";

            using var http = new HttpClient();
            var json = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var obj = results[0];
                if (obj.TryGetProperty("artworkUrl100", out var art))
                {
                    var str = art.GetString()?.Replace("100x100", "300x300");
                    if (Uri.TryCreate(str, UriKind.Absolute, out var uri)) return uri;
                }
            }
            return null;
        }
    }
}
