using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Zink.Services.Calling;

namespace Zink.Services
{
    public sealed class DiagnosticsUploadResult
    {
        public bool Success { get; init; }
        public string ReportId { get; init; } = "";
        public string DownloadUrl { get; init; } = "";
        public string Message { get; init; } = "";
    }

    public static class DiagnosticsUploadService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static async Task<DiagnosticsUploadResult> UploadSupportBundleAsync(
            string bundlePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
                throw new FileNotFoundException("Support bundle was not found.", bundlePath);

            var token = await TokenStore.Instance.GetTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("You need to log in to Zink before uploading diagnostics.");

            var fileInfo = new FileInfo(bundlePath);
            var uploadUrl = CallServerConfig.DiagnosticsUploadUrl +
                "?deviceName=" + Uri.EscapeDataString(DiagnosticLogService.DeviceName) +
                "&fileName=" + Uri.EscapeDataString(fileInfo.Name);

            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2)
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Zink-App-Base", AppContext.BaseDirectory);
            request.Headers.Add("X-Zink-Log-Path", DiagnosticLogService.CurrentLogPath);

            await using var stream = File.OpenRead(bundlePath);
            request.Content = new StreamContent(stream);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            request.Content.Headers.ContentLength = fileInfo.Length;

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Diagnostics upload failed. Status {(int)response.StatusCode}: {responseText}");

            var result = JsonSerializer.Deserialize<DiagnosticsUploadResult>(responseText, JsonOptions);
            return result ?? new DiagnosticsUploadResult
            {
                Success = false,
                Message = "The diagnostics upload completed but the server returned no result."
            };
        }
    }
}
