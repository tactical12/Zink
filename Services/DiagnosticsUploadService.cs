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
        public string LocalBundlePath { get; init; } = "";
        public int StatusCode { get; init; }
        public string Error { get; init; } = "";
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

            DiagnosticLogService.WriteLine($"[DiagnosticsUpload] Upload starting url={CallServerConfig.DiagnosticsUploadUrl}; bundle={bundlePath}; bytes={fileInfo.Length}; device={DiagnosticLogService.DeviceName}");

            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2)
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("X-Zink-App-Base", AppContext.BaseDirectory);
            request.Headers.TryAddWithoutValidation("X-Zink-Log-Path", DiagnosticLogService.CurrentLogPath);

            await using var stream = File.OpenRead(bundlePath);
            request.Content = new StreamContent(stream);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            request.Content.Headers.ContentLength = fileInfo.Length;

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            DiagnosticLogService.WriteLine($"[DiagnosticsUpload] Upload response status={(int)response.StatusCode}; reason={response.ReasonPhrase}; body={TrimForLog(responseText, 1000)}");

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Diagnostics upload failed. Status {(int)response.StatusCode}: {responseText}");

            var result = JsonSerializer.Deserialize<DiagnosticsUploadResult>(responseText, JsonOptions);
            if (result != null)
            {
                return new DiagnosticsUploadResult
                {
                    Success = result.Success,
                    ReportId = result.ReportId,
                    DownloadUrl = result.DownloadUrl,
                    Message = result.Message,
                    LocalBundlePath = bundlePath,
                    StatusCode = (int)response.StatusCode
                };
            }

            return new DiagnosticsUploadResult
            {
                Success = false,
                Message = "The diagnostics upload completed but the server returned no result.",
                LocalBundlePath = bundlePath,
                StatusCode = (int)response.StatusCode
            };
        }

        public static async Task<DiagnosticsUploadResult> TryUploadSupportBundleAsync(
            string bundlePath,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await UploadSupportBundleAsync(bundlePath, cancellationToken);
                return new DiagnosticsUploadResult
                {
                    Success = result.Success,
                    ReportId = result.ReportId,
                    DownloadUrl = result.DownloadUrl,
                    Message = string.IsNullOrWhiteSpace(result.Message) ? "Diagnostics uploaded." : result.Message,
                    LocalBundlePath = bundlePath,
                    StatusCode = result.StatusCode
                };
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine($"[DiagnosticsUpload] Upload failed; bundle={bundlePath}; error={ex}");
                DiagnosticLogService.Flush();

                return new DiagnosticsUploadResult
                {
                    Success = false,
                    Message = "Upload failed, but the support bundle was saved locally.",
                    Error = ex.Message,
                    LocalBundlePath = bundlePath
                };
            }
        }

        private static string TrimForLog(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            var normalized = value.Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ');

            return normalized.Length <= maxLength
                ? normalized
                : normalized[..maxLength] + "...";
        }
    }
}
