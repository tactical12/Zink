using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Zink.Services.Calling;

namespace Zink.Services
{
    public sealed class ScreenShareReportContext
    {
        public string Experience { get; init; } = "";
        public string Notes { get; init; } = "";
        public string CallId { get; init; } = "";
        public string Quality { get; init; } = "";
        public string LocalUser { get; init; } = "";
        public string RemoteUser { get; init; } = "";
        public string Duration { get; init; } = "";
        public double SenderFps { get; init; }
        public double ReceiverFps { get; init; }
        public int LastWidth { get; init; }
        public int LastHeight { get; init; }
        public long LastBytes { get; init; }
        public int SentFrames { get; init; }
        public long ReceivedFrames { get; init; }
        public long RenderedFrames { get; init; }
        public int DroppedSendFrames { get; init; }
        public int DroppedReceiveFrames { get; init; }
        public int DecodeFailures { get; init; }
        public int DecoderResets { get; init; }
        public string AudioDevice { get; init; } = "";
        public int AudioPacketsSent { get; init; }
    }

    public static class ScreenShareReportService
    {
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        public static async Task<string> CreateBundleAsync(ScreenShareReportContext context)
        {
            DiagnosticLogService.InitializeFromSettings();
            DiagnosticLogService.Flush();

            Directory.CreateDirectory(DiagnosticLogService.LogDirectoryPath);

            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var deviceName = DiagnosticLogService.DeviceName;
            var reportPath = Path.Combine(DiagnosticLogService.LogDirectoryPath, $"zink-screen-share-{deviceName}-{stamp}.txt");
            var bundlePath = Path.Combine(DiagnosticLogService.LogDirectoryPath, $"zink-screen-share-{deviceName}-{stamp}.zip");

            await File.WriteAllTextAsync(reportPath, await BuildReportAsync(context), Utf8NoBom);

            if (File.Exists(bundlePath))
                File.Delete(bundlePath);

            using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Create);
            AddFileIfExists(archive, reportPath, Path.GetFileName(reportPath));

            foreach (var logPath in Directory.EnumerateFiles(DiagnosticLogService.LogDirectoryPath, "zink-diagnostics-*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(8))
            {
                AddFileIfExists(archive, logPath, "logs/" + Path.GetFileName(logPath));
            }

            return bundlePath;
        }

        private static async Task<string> BuildReportAsync(ScreenShareReportContext context)
        {
            var userInfo = await TokenStore.Instance.GetUserInfoAsync();

            var builder = new StringBuilder();
            builder.AppendLine("Zink screen-share feedback report");
            builder.AppendLine("Generated: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
            builder.AppendLine("Device: " + Environment.MachineName);
            builder.AppendLine("Windows user: " + Environment.UserName);
            builder.AppendLine("Zink user: " + (userInfo?.displayName ?? context.LocalUser));
            builder.AppendLine("Experience: " + context.Experience);
            builder.AppendLine("Notes: " + (string.IsNullOrWhiteSpace(context.Notes) ? "-" : context.Notes.Trim()));
            builder.AppendLine();
            builder.AppendLine("Session");
            builder.AppendLine("  Call id: " + context.CallId);
            builder.AppendLine("  Local user: " + context.LocalUser);
            builder.AppendLine("  Remote user: " + context.RemoteUser);
            builder.AppendLine("  Duration: " + context.Duration);
            builder.AppendLine("  Quality: " + context.Quality);
            builder.AppendLine("  Last resolution: " + context.LastWidth + "x" + context.LastHeight);
            builder.AppendLine("  Last encoded bytes: " + context.LastBytes);
            builder.AppendLine();
            builder.AppendLine("Performance");
            builder.AppendLine("  Sender FPS: " + context.SenderFps.ToString("0.0", CultureInfo.InvariantCulture));
            builder.AppendLine("  Receiver FPS: " + context.ReceiverFps.ToString("0.0", CultureInfo.InvariantCulture));
            builder.AppendLine("  Sent frames: " + context.SentFrames);
            builder.AppendLine("  Received frames: " + context.ReceivedFrames);
            builder.AppendLine("  Rendered frames: " + context.RenderedFrames);
            builder.AppendLine("  Dropped send frames: " + context.DroppedSendFrames);
            builder.AppendLine("  Dropped receive frames: " + context.DroppedReceiveFrames);
            builder.AppendLine("  Decode failures: " + context.DecodeFailures);
            builder.AppendLine("  Decoder resets: " + context.DecoderResets);
            builder.AppendLine();
            builder.AppendLine("Audio");
            builder.AppendLine("  Screen-share sound device: " + context.AudioDevice);
            builder.AppendLine("  Screen-share sound packets sent: " + context.AudioPacketsSent);
            builder.AppendLine();
            builder.AppendLine("Paths");
            builder.AppendLine("  Log directory: " + DiagnosticLogService.LogDirectoryPath);
            builder.AppendLine("  Current log: " + DiagnosticLogService.CurrentLogPath);

            return builder.ToString();
        }

        private static void AddFileIfExists(ZipArchive archive, string sourcePath, string entryName)
        {
            if (!File.Exists(sourcePath))
                return;

            archive.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Fastest);
        }
    }
}
