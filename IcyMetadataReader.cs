using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Zink
{
    public sealed class IcyMetadataReader
    {
        public async Task StartAsync(Uri stream, Action<string> onNowPlaying, CancellationToken ct)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.TryAddWithoutValidation("Icy-MetaData", "1");

                using var resp = await http.GetAsync(stream, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();

                if (!resp.Headers.TryGetValues("icy-metaint", out var vals) || !int.TryParse(System.Linq.Enumerable.First(vals), out int metaInt))
                    return; // no ICY

                using var s = await resp.Content.ReadAsStreamAsync(ct);
                var audioBuffer = ArrayPool<byte>.Shared.Rent(metaInt);
                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        int read = await ReadExactAsync(s, audioBuffer, 0, metaInt, ct);
                        if (read < metaInt) break;

                        int lenByte = s.ReadByte();
                        if (lenByte < 0) break;
                        int metaLen = lenByte * 16;

                        if (metaLen > 0)
                        {
                            var meta = new byte[metaLen];
                            read = await ReadExactAsync(s, meta, 0, metaLen, ct);
                            if (read == metaLen)
                            {
                                var text = System.Text.Encoding.UTF8.GetString(meta).TrimEnd('\0');
                                var now = ParseStreamTitle(text);
                                if (!string.IsNullOrWhiteSpace(now))
                                    onNowPlaying?.Invoke(now);
                            }
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(audioBuffer);
                }
            }
            catch { }
        }

        private static async Task<int> ReadExactAsync(Stream s, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int n = await s.ReadAsync(buffer.AsMemory(offset + total, count - total), ct);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        private static string ParseStreamTitle(string metadata)
        {
            const string key = "StreamTitle='";
            int idx = metadata.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                idx += key.Length;
                int end = metadata.IndexOf("';", idx, StringComparison.OrdinalIgnoreCase);
                if (end > idx) return metadata.Substring(idx, end - idx).Trim();
            }
            return null;
        }
    }
}
