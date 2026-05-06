using System;
using System.IO;
using Zink.Services;

namespace Zink.Services.NativeCalling
{
    internal static class ScreenShareCrashBreadcrumb
    {
        private static readonly object SyncRoot = new();

        public static void Mark(string stage)
        {
            if (string.IsNullOrWhiteSpace(stage))
                return;

            var line = $"{DateTimeOffset.Now:O} [{Environment.MachineName}] {stage}{Environment.NewLine}";

            try
            {
                DiagnosticLogService.EnsureLogFile("screen-share crash breadcrumb");
                DiagnosticLogService.WriteLine("[ScreenShare:CRASHMARK] " + stage);
                DiagnosticLogService.Flush();
            }
            catch
            {
            }

            lock (SyncRoot)
            {
                foreach (var directory in DiagnosticLogService.GetKnownLogDirectoryPaths())
                {
                    try
                    {
                        Directory.CreateDirectory(directory);
                        var latestPath = Path.Combine(directory, $"zink-screenshare-crash-{DiagnosticLogService.DeviceName}-latest.txt");
                        var lastStagePath = Path.Combine(directory, $"zink-screenshare-crash-{DiagnosticLogService.DeviceName}-last-stage.txt");

                        File.AppendAllText(latestPath, line);
                        File.WriteAllText(lastStagePath, line);
                    }
                    catch
                    {
                    }
                }
            }
        }
    }
}
