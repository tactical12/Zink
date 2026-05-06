using System;
using System.Collections.Generic;
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
        public string SessionType { get; init; } = "Screen share";
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
            Directory.CreateDirectory(DiagnosticLogService.ScreenShareDocumentsDirectoryPath);
            SaveLatestState(context);

            var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var deviceName = DiagnosticLogService.DeviceName;
            var reportPrefix = string.Equals(context.SessionType, "Call", StringComparison.OrdinalIgnoreCase)
                ? "zink-call"
                : "zink-screen-share";
            var outputDirectory = DiagnosticLogService.ScreenShareDocumentsDirectoryPath;
            var reportPath = Path.Combine(outputDirectory, $"{reportPrefix}-{deviceName}-{stamp}.txt");
            var bundlePath = Path.Combine(outputDirectory, $"{reportPrefix}-{deviceName}-{stamp}.zip");

            await File.WriteAllTextAsync(reportPath, await BuildReportAsync(context), Utf8NoBom);

            if (File.Exists(bundlePath))
                File.Delete(bundlePath);

            using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Create);
            AddFileIfExists(archive, reportPath, Path.GetFileName(reportPath));

            foreach (var logPath in EnumerateEvidenceFiles()
                .Where(IsSupportEvidenceFile)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(24))
            {
                AddFileIfExists(archive, logPath, "logs/" + Path.GetFileName(logPath));
            }

            return bundlePath;
        }

        public static void SaveLatestState(ScreenShareReportContext context)
        {
            try
            {
                DiagnosticLogService.InitializeFromSettings();
                Directory.CreateDirectory(DiagnosticLogService.ScreenShareDocumentsDirectoryPath);

                var statePath = Path.Combine(
                    DiagnosticLogService.ScreenShareDocumentsDirectoryPath,
                    $"zink-screen-share-state-{DiagnosticLogService.DeviceName}-latest.txt");

                File.WriteAllText(statePath, BuildStateSnapshot(context), Utf8NoBom);
            }
            catch
            {
            }
        }

        private static async Task<string> BuildReportAsync(ScreenShareReportContext context)
        {
            var userInfo = await TokenStore.Instance.GetUserInfoAsync();

            var builder = new StringBuilder();
            builder.AppendLine($"Zink {context.SessionType.ToLowerInvariant()} feedback report");
            builder.AppendLine("Generated: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
            builder.AppendLine("Device: " + Environment.MachineName);
            builder.AppendLine("Windows user: " + Environment.UserName);
            builder.AppendLine("Zink user: " + (userInfo?.displayName ?? context.LocalUser));
            builder.AppendLine("Experience: " + context.Experience);
            builder.AppendLine("Notes: " + (string.IsNullOrWhiteSpace(context.Notes) ? "-" : context.Notes.Trim()));
            builder.AppendLine();
            builder.AppendLine("Session");
            builder.AppendLine("  Type: " + context.SessionType);
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
            builder.AppendLine("  Screen-share documents folder: " + DiagnosticLogService.ScreenShareDocumentsDirectoryPath);
            builder.AppendLine("  Current log: " + DiagnosticLogService.CurrentLogPath);

            return builder.ToString();
        }

        private static string BuildStateSnapshot(ScreenShareReportContext context)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Zink screen-share latest state");
            builder.AppendLine("Generated: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
            builder.AppendLine("Device: " + Environment.MachineName);
            builder.AppendLine("Windows user: " + Environment.UserName);
            builder.AppendLine("Experience: " + context.Experience);
            builder.AppendLine("Notes: " + (string.IsNullOrWhiteSpace(context.Notes) ? "-" : context.Notes.Trim()));
            builder.AppendLine("Call id: " + context.CallId);
            builder.AppendLine("Local user: " + context.LocalUser);
            builder.AppendLine("Remote user: " + context.RemoteUser);
            builder.AppendLine("Duration: " + context.Duration);
            builder.AppendLine("Quality: " + context.Quality);
            builder.AppendLine("Sender FPS: " + context.SenderFps.ToString("0.0", CultureInfo.InvariantCulture));
            builder.AppendLine("Receiver FPS: " + context.ReceiverFps.ToString("0.0", CultureInfo.InvariantCulture));
            builder.AppendLine("Resolution: " + context.LastWidth + "x" + context.LastHeight);
            builder.AppendLine("Last encoded bytes: " + context.LastBytes);
            builder.AppendLine("Sent frames: " + context.SentFrames);
            builder.AppendLine("Received frames: " + context.ReceivedFrames);
            builder.AppendLine("Rendered frames: " + context.RenderedFrames);
            builder.AppendLine("Dropped send frames: " + context.DroppedSendFrames);
            builder.AppendLine("Dropped receive frames: " + context.DroppedReceiveFrames);
            builder.AppendLine("Decode failures: " + context.DecodeFailures);
            builder.AppendLine("Decoder resets: " + context.DecoderResets);
            builder.AppendLine("Screen-share sound device: " + context.AudioDevice);
            builder.AppendLine("Screen-share sound packets sent: " + context.AudioPacketsSent);
            builder.AppendLine("Screen-share documents folder: " + DiagnosticLogService.ScreenShareDocumentsDirectoryPath);
            builder.AppendLine("Current log: " + DiagnosticLogService.CurrentLogPath);
            return builder.ToString();
        }

        private static IEnumerable<string> EnumerateEvidenceFiles()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var directory in DiagnosticLogService.GetKnownLogDirectoryPaths()
                .Concat(new[] { DiagnosticLogService.ScreenShareDocumentsDirectoryPath }))
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                    continue;

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(directory, "*.txt").ToList();
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    string fullPath;
                    try
                    {
                        fullPath = Path.GetFullPath(file);
                    }
                    catch
                    {
                        continue;
                    }

                    if (seen.Add(fullPath))
                        yield return fullPath;
                }
            }
        }

        private static void AddFileIfExists(ZipArchive archive, string sourcePath, string entryName)
        {
            if (!File.Exists(sourcePath))
                return;

            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var destination = entry.Open();
            source.CopyTo(destination);
        }

        private static bool IsSupportEvidenceFile(string path)
        {
            var name = Path.GetFileName(path);
            return name.StartsWith("zink-diagnostics-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("zink-screen-share-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("zink-screenshare-crash-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("zink-call-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("CrashLog", StringComparison.OrdinalIgnoreCase);
        }
    }
}
