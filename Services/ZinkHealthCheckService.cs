using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Networking.Connectivity;

namespace Zink.Services
{
    public sealed class ZinkHealthCheckReport
    {
        public string ReportPath { get; init; } = string.Empty;
        public string BundlePath { get; init; } = string.Empty;
        public int Passed { get; init; }
        public int Warnings { get; init; }
        public int Failed { get; init; }

        public string Summary => $"{Passed} passed, {Warnings} warnings, {Failed} failed";
    }

    public static class ZinkHealthCheckService
    {
        private const string ConnectivityProbeUrl = "https://www.msftconnecttest.com/connecttest.txt";
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        public static Task<ZinkHealthCheckReport> RunAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(async () =>
            {
                DiagnosticLogService.InitializeFromSettings();
                DiagnosticLogService.WriteLine("[HealthCheck] Starting Zink health check.");

                Directory.CreateDirectory(DiagnosticLogService.LogDirectoryPath);

                var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var deviceName = DiagnosticLogService.DeviceName;
                var reportPath = Path.Combine(DiagnosticLogService.LogDirectoryPath, $"zink-health-{deviceName}-{stamp}.txt");
                var latestReportPath = Path.Combine(DiagnosticLogService.LogDirectoryPath, $"zink-health-{deviceName}-latest.txt");
                var bundlePath = Path.Combine(DiagnosticLogService.LogDirectoryPath, $"zink-support-{deviceName}-{stamp}.zip");

                var writer = new HealthReportWriter();
                writer.Line("Zink health check report");
                writer.Line("Generated: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
                writer.Line("Device: " + Environment.MachineName);
                writer.Line("User: " + Environment.UserName);
                writer.Line("OS: " + Environment.OSVersion);
                writer.Line("Process architecture: " + RuntimeInformationSafe());
                writer.Line("App base: " + AppContext.BaseDirectory);
                writer.Line("Log directory: " + DiagnosticLogService.LogDirectoryPath);
                writer.Line("Current device log: " + DiagnosticLogService.CurrentLogPath);
                writer.Line("");

                await Check("Diagnostic logging", writer, () =>
                {
                    DiagnosticLogService.SetEnabled(true);
                    DiagnosticLogService.WriteLine("[HealthCheck] Diagnostic logging write probe.");
                    DiagnosticLogService.Flush();
                    return File.Exists(DiagnosticLogService.CurrentLogPath)
                        ? "Diagnostic file logging is enabled and the latest log exists."
                        : throw new FileNotFoundException("The latest diagnostic log file was not created.", DiagnosticLogService.CurrentLogPath);
                });

                await Check("Log folder write access", writer, () =>
                {
                    var probePath = Path.Combine(DiagnosticLogService.LogDirectoryPath, $"zink-write-probe-{Guid.NewGuid():N}.tmp");
                    File.WriteAllText(probePath, "zink log write probe", Utf8NoBom);
                    File.Delete(probePath);
                    return "Log folder can create and delete files.";
                });

                await Check("Package identity", writer, () =>
                {
                    try
                    {
                        var id = Package.Current.Id;
                        var version = id.Version;
                        return $"{id.Name} {version.Major}.{version.Minor}.{version.Build}.{version.Revision} publisher={id.Publisher}";
                    }
                    catch (Exception ex)
                    {
                        return "Package identity is unavailable in this launch mode: " + ex.Message;
                    }
                }, warningOnly: true);

                await Check("App assembly", writer, () =>
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    var name = assembly.GetName();
                    return $"{name.Name} {name.Version}";
                });

                await Check("Network profile", writer, () =>
                {
                    var profile = NetworkInformation.GetInternetConnectionProfile();
                    if (profile == null)
                        throw new InvalidOperationException("Windows reports no active internet connection profile.");

                    return $"Connectivity={profile.GetNetworkConnectivityLevel()}, cost={profile.GetConnectionCost().NetworkCostType}";
                }, warningOnly: true);

                await CheckAsync("Internet probe", writer, async () =>
                {
                    using var client = new HttpClient
                    {
                        Timeout = TimeSpan.FromSeconds(5)
                    };

                    using var response = await client.GetAsync(ConnectivityProbeUrl, cancellationToken);
                    return $"GET {ConnectivityProbeUrl} -> {(int)response.StatusCode} {response.ReasonPhrase}";
                }, warningOnly: true);

                await Check("WebView2 dependency", writer, () =>
                {
                    var loaded = AppDomain.CurrentDomain.GetAssemblies()
                        .Any(assembly => assembly.GetName().Name?.Contains("WebView2", StringComparison.OrdinalIgnoreCase) == true);

                    return loaded
                        ? "WebView2 assemblies are loaded by the app."
                        : "WebView2 assemblies are not loaded yet; this is normal until a WebView page opens.";
                }, warningOnly: true);

                await Check("Screen-share diagnostics readiness", writer, () =>
                {
                    return "Native screen-share/GPU encoder probes are intentionally non-invasive. The health check inspects logs instead of creating a Media Foundation encoder, so the diagnostics button cannot crash the app while testing GPU drivers.";
                });

                await Check("Recent screen-share log signals", writer, () =>
                {
                    var currentLogPath = DiagnosticLogService.CurrentLogPath;
                    if (!File.Exists(currentLogPath))
                        throw new FileNotFoundException("The latest diagnostic log does not exist yet.", currentLogPath);

                    var recentLines = ReadRecentLines(currentLogPath, 2500);
                    var screenShareLines = recentLines
                        .Where(line =>
                            line.Contains("[ScreenShare:", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("[Call]", StringComparison.OrdinalIgnoreCase))
                        .TakeLast(80)
                        .ToList();

                    if (screenShareLines.Count == 0)
                        return "No recent screen-share/call log lines were found. Start a call or screen share, then run the health check again.";

                    var failures = screenShareLines.Count(line =>
                        line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("stutter", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("freeze", StringComparison.OrdinalIgnoreCase));

                    return $"Found {screenShareLines.Count} recent call/screen-share log lines; flagged {failures} possible failure lines. Recent signals: " +
                        string.Join(" | ", screenShareLines.TakeLast(12));
                }, warningOnly: true);

                await Check("Latest diagnostic logs", writer, () =>
                {
                    var logs = Directory.EnumerateFiles(DiagnosticLogService.LogDirectoryPath, "zink-diagnostics-*.txt")
                        .Select(path => new FileInfo(path))
                        .OrderByDescending(file => file.LastWriteTimeUtc)
                        .Take(8)
                        .ToList();

                    if (logs.Count == 0)
                        throw new FileNotFoundException("No diagnostic logs were found.");

                    return string.Join("; ", logs.Select(file => $"{file.Name} ({file.Length / 1024} KB, {file.LastWriteTime:yyyy-MM-dd HH:mm:ss})"));
                }, warningOnly: true);

                writer.Line("");
                writer.Line($"Summary: {writer.Passed} OK, {writer.Warnings} warning, {writer.Failed} failed");
                writer.Line("Upload status: No remote support endpoint is configured. Use the support bundle path below, or configure a secure HTTPS endpoint before enabling uploads.");
                writer.Line("Support bundle: " + bundlePath);

                File.WriteAllText(reportPath, writer.ToString(), Utf8NoBom);
                File.Copy(reportPath, latestReportPath, overwrite: true);
                CreateSupportBundle(bundlePath, reportPath);
                DiagnosticLogService.Flush();
                DiagnosticLogService.WriteLine("[HealthCheck] Completed Zink health check: " + writer.Passed + " OK, " + writer.Warnings + " warning, " + writer.Failed + " failed. Report: " + reportPath);

                return new ZinkHealthCheckReport
                {
                    ReportPath = reportPath,
                    BundlePath = bundlePath,
                    Passed = writer.Passed,
                    Warnings = writer.Warnings,
                    Failed = writer.Failed
                };
            }, cancellationToken);
        }

        private static async Task Check(string name, HealthReportWriter writer, Func<string> check, bool warningOnly = false)
        {
            await CheckAsync(name, writer, () => Task.FromResult(check()), warningOnly);
        }

        private static async Task CheckAsync(string name, HealthReportWriter writer, Func<Task<string>> check, bool warningOnly = false)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var detail = await check();
                stopwatch.Stop();
                writer.Ok(name, detail, stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                if (warningOnly)
                    writer.Warn(name, ex, stopwatch.Elapsed);
                else
                    writer.Fail(name, ex, stopwatch.Elapsed);
            }
        }

        private static void CreateSupportBundle(string bundlePath, string reportPath)
        {
            try
            {
                if (File.Exists(bundlePath))
                    File.Delete(bundlePath);

                using var archive = ZipFile.Open(bundlePath, ZipArchiveMode.Create);
                AddFileIfExists(archive, reportPath, Path.GetFileName(reportPath));

                foreach (var logPath in Directory.EnumerateFiles(DiagnosticLogService.LogDirectoryPath, "zink-diagnostics-*.txt")
                    .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                    .Take(10))
                {
                    AddFileIfExists(archive, logPath, "logs/" + Path.GetFileName(logPath));
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogService.WriteLine("[HealthCheck] Support bundle creation failed: " + ex);
            }
        }

        private static void AddFileIfExists(ZipArchive archive, string sourcePath, string entryName)
        {
            if (!File.Exists(sourcePath))
                return;

            archive.CreateEntryFromFile(sourcePath, entryName, CompressionLevel.Fastest);
        }

        private static List<string> ReadRecentLines(string path, int maxLines)
        {
            var queue = new Queue<string>(Math.Max(1, maxLines));

            foreach (var line in File.ReadLines(path))
            {
                if (queue.Count >= maxLines)
                    queue.Dequeue();

                queue.Enqueue(line);
            }

            return queue.ToList();
        }

        private static string RuntimeInformationSafe()
        {
            try
            {
                return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
            }
            catch
            {
                return Environment.Is64BitProcess ? "x64" : "x86";
            }
        }

        private sealed class HealthReportWriter
        {
            private readonly StringBuilder _builder = new();

            public int Passed { get; private set; }
            public int Warnings { get; private set; }
            public int Failed { get; private set; }

            public void Line(string value) => _builder.AppendLine(value);

            public void Ok(string name, string detail, TimeSpan elapsed)
            {
                Passed++;
                Write("OK", name, detail, elapsed);
            }

            public void Warn(string name, Exception exception, TimeSpan elapsed)
            {
                Warnings++;
                Write("WARN", name, FormatException(exception), elapsed);
            }

            public void Fail(string name, Exception exception, TimeSpan elapsed)
            {
                Failed++;
                Write("FAIL", name, FormatException(exception), elapsed);
            }

            public override string ToString() => _builder.ToString();

            private void Write(string status, string name, string detail, TimeSpan elapsed)
            {
                _builder.AppendLine($"[{status}] {name} ({elapsed.TotalMilliseconds:0} ms)");
                _builder.AppendLine("  " + detail.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\n  ", StringComparison.Ordinal));
                _builder.AppendLine();
            }

            private static string FormatException(Exception exception)
            {
                return $"{exception.GetType().Name}: {exception.Message}";
            }
        }
    }
}
