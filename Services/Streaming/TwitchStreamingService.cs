using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Zink.Services.Streaming
{
    public sealed class TwitchStreamingService
    {
        public const int OutputWidth = 1920;
        public const int OutputHeight = 1080;
        public const int OutputFps = 60;
        public const int VideoBitrateKbps = 6000;
        public const string VideoCodecName = "H.264";
        public const string EncoderName = "libx264";
        public const string H264Profile = "high";
        public const string WindowsLoopbackAudioDeviceName = "Windows system audio (loopback)";

        public static TwitchStreamingService Instance { get; } = new();

        private readonly StreamingStats _currentStats = new();
        private Process? _ffmpegProcess;
        private StreamWriter? _logWriter;
        private string? _currentLogPath;
        private DesktopLoopbackAudioSession? _desktopLoopbackAudioSession;

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<bool>? StreamingStateChanged;
        public event EventHandler<StreamingStats>? StatsChanged;

        public bool IsStreaming => _ffmpegProcess is { HasExited: false };
        public string LastStatus { get; private set; } = "Ready.";
        public string? CurrentLogPath => _currentLogPath;
        public StreamingStats CurrentStats => _currentStats.Clone();

        private TwitchStreamingService()
        {
        }

        public async Task StartAsync(
            string streamKey,
            string serverUrl,
            string desktopAudioInput,
            double desktopAudioVolume,
            bool desktopAudioMuted,
            string microphoneInput,
            double microphoneVolume,
            bool microphoneMuted,
            bool lowLatency)
        {
            if (IsStreaming)
            {
                PublishStatus("Stream is already running.");
                return;
            }

            if (string.IsNullOrWhiteSpace(streamKey))
            {
                PublishStatus("Enter your Twitch stream key first.");
                return;
            }

            var ffmpegPath = ResolveFfmpegPath();
            if (ffmpegPath is null)
            {
                PublishStatus("ffmpeg.exe was not found. Add ffmpeg.exe to Tools or install it on PATH.");
                return;
            }

            DesktopLoopbackAudioSession? loopbackAudioSession = null;
            if (ShouldUseWindowsLoopbackAudio(desktopAudioInput, desktopAudioMuted))
            {
                loopbackAudioSession = DesktopLoopbackAudioSession.Create();
            }

            var arguments = BuildArguments(
                BuildTwitchIngestUrl(serverUrl, streamKey),
                desktopAudioInput,
                desktopAudioVolume,
                desktopAudioMuted,
                microphoneInput,
                microphoneVolume,
                microphoneMuted,
                loopbackAudioSession,
                lowLatency);

            var safeArguments = BuildArguments(
                BuildTwitchIngestUrl(serverUrl, "***stream-key-hidden***"),
                desktopAudioInput,
                desktopAudioVolume,
                desktopAudioMuted,
                microphoneInput,
                microphoneVolume,
                microphoneMuted,
                loopbackAudioSession,
                lowLatency);

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };

            try
            {
                StartLog(ffmpegPath, safeArguments);
                ResetStats();
                _desktopLoopbackAudioSession = loopbackAudioSession;

                _ffmpegProcess = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

                _ffmpegProcess.ErrorDataReceived += Process_OutputDataReceived;
                _ffmpegProcess.OutputDataReceived += Process_OutputDataReceived;
                _ffmpegProcess.Exited += Process_Exited;

                _desktopLoopbackAudioSession?.Start();

                if (!_ffmpegProcess.Start())
                {
                    _desktopLoopbackAudioSession?.Dispose();
                    _desktopLoopbackAudioSession = null;
                    WriteLog("Could not start ffmpeg process.");
                    PublishStatus("Could not start the Twitch stream.");
                    StreamingStateChanged?.Invoke(this, false);
                    return;
                }

                _ffmpegProcess.BeginErrorReadLine();
                _ffmpegProcess.BeginOutputReadLine();

                StreamingStateChanged?.Invoke(this, true);
                PublishStatus("Streaming desktop H.264 1080p60. Waiting for telemetry...");
            }
            catch (Exception ex)
            {
                loopbackAudioSession?.Dispose();
                _desktopLoopbackAudioSession = null;
                WriteLog("Start failed: " + ex);
                await StopAsync();
                PublishStatus($"Streaming failed: {ex.Message}");
            }
        }

        public Task StopAsync()
        {
            try
            {
                if (_ffmpegProcess is { HasExited: false })
                {
                    _ffmpegProcess.Kill(entireProcessTree: true);
                    _ffmpegProcess.WaitForExit(3000);
                }
            }
            catch
            {
            }
            finally
            {
                DisposeProcess();
                _desktopLoopbackAudioSession?.Dispose();
                _desktopLoopbackAudioSession = null;
                StreamingStateChanged?.Invoke(this, false);
                WriteLog("Stream stopped.");
                PublishStatus("Stream stopped.");
                CloseLog();
            }

            return Task.CompletedTask;
        }

        public static async Task<IReadOnlyList<string>> GetDirectShowAudioDevicesAsync()
        {
            var ffmpegPath = ResolveFfmpegPath();
            if (ffmpegPath is null)
                return Array.Empty<string>();

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };

            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                    return Array.Empty<string>();

                string stdout = await process.StandardOutput.ReadToEndAsync();
                string stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                return ParseDirectShowAudioDevices(stdout + Environment.NewLine + stderr);
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static string? ResolveFfmpegPath()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Tools", "ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ffmpeg", "bin", "ffmpeg.exe"),
                Path.Combine("C:\\", "ffmpeg", "bin", "ffmpeg.exe")
            };

            foreach (var candidate in candidates)
            {
                var fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                    return fullPath;
            }

            var pathValue = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathValue))
            {
                foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    try
                    {
                        var candidate = Path.Combine(directory, "ffmpeg.exe");
                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch
                    {
                    }
                }
            }

            return null;
        }

        private static string BuildTwitchIngestUrl(string serverUrl, string streamKey)
        {
            var trimmedServer = string.IsNullOrWhiteSpace(serverUrl)
                ? "rtmp://live.twitch.tv/app"
                : serverUrl.Trim().TrimEnd('/');

            return $"{trimmedServer}/{streamKey.Trim()}";
        }

        private static string BuildArguments(
            string ingestUrl,
            string desktopAudioInput,
            double desktopAudioVolume,
            bool desktopAudioMuted,
            string microphoneInput,
            double microphoneVolume,
            bool microphoneMuted,
            DesktopLoopbackAudioSession? loopbackAudioSession,
            bool lowLatency)
        {
            var builder = new StringBuilder();
            bool hasLoopbackAudio = loopbackAudioSession is not null;
            bool hasDesktopAudio = hasLoopbackAudio ||
                (!desktopAudioMuted &&
                 !string.IsNullOrWhiteSpace(desktopAudioInput) &&
                 !IsWindowsLoopbackAudio(desktopAudioInput));
            bool hasMicrophone = !string.IsNullOrWhiteSpace(microphoneInput);
            double safeDesktopVolume = desktopAudioMuted ? 0 : Math.Clamp(desktopAudioVolume, 0, 1);
            double safeMicVolume = microphoneMuted ? 0 : Math.Clamp(microphoneVolume, 0, 1);
            int nextInputIndex = 1;
            int? desktopAudioIndex = null;
            int? microphoneIndex = null;

            builder.Append("-hide_banner -loglevel info -stats -probesize 32 -analyzeduration 0 ");
            builder.Append("-thread_queue_size 1024 -rtbufsize 256M -f gdigrab -draw_mouse 1 -framerate 60 -use_wallclock_as_timestamps 1 -i desktop ");

            if (hasDesktopAudio)
            {
                desktopAudioIndex = nextInputIndex++;
                if (hasLoopbackAudio)
                {
                    builder.Append("-thread_queue_size 1024 -f ");
                    builder.Append(loopbackAudioSession!.FfmpegFormat);
                    builder.Append(" -ar ");
                    builder.Append(loopbackAudioSession.SampleRate.ToString(CultureInfo.InvariantCulture));
                    builder.Append(" -ac ");
                    builder.Append(loopbackAudioSession.Channels.ToString(CultureInfo.InvariantCulture));
                    builder.Append(" -i ");
                    builder.Append(Quote(loopbackAudioSession.PipePath));
                }
                else
                {
                    builder.Append("-thread_queue_size 512 -f dshow -i ");
                    builder.Append(Quote($"audio={desktopAudioInput.Trim()}"));
                }

                builder.Append(' ');
            }

            if (hasMicrophone)
            {
                microphoneIndex = nextInputIndex++;
                builder.Append("-thread_queue_size 512 -f dshow -i ");
                builder.Append(Quote($"audio={microphoneInput.Trim()}"));
                builder.Append(' ');
            }

            builder.Append("-map 0:v:0 ");
            if (hasDesktopAudio || hasMicrophone)
            {
                builder.Append("-filter_complex ");
                builder.Append(Quote(BuildAudioFilter(desktopAudioIndex, safeDesktopVolume, microphoneIndex, safeMicVolume)));
                builder.Append(" -map ");
                builder.Append(Quote("[mixed_audio]"));
                builder.Append(' ');
            }

            builder.Append("-c:v libx264 -profile:v high -preset ultrafast ");
            if (lowLatency)
                builder.Append("-tune zerolatency ");

            builder.Append("-vf ");
            builder.Append(Quote("fps=60,scale=1920:1080:flags=fast_bilinear:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,format=yuv420p"));
            builder.Append(' ');
            builder.Append("-r 60 -g 60 -keyint_min 60 -bf 0 -refs 1 -sc_threshold 0 ");
            builder.Append("-b:v 6000k -minrate 6000k -maxrate 6000k -bufsize 6000k ");
            builder.Append("-x264-params ");
            builder.Append(Quote("nal-hrd=cbr:force-cfr=1"));
            builder.Append(' ');

            if (hasDesktopAudio || hasMicrophone)
                builder.Append("-c:a aac -b:a 160k -ar 48000 ");
            else
                builder.Append("-an ");

            builder.Append("-flags +low_delay -fflags nobuffer -flush_packets 1 -max_interleave_delta 0 ");
            builder.Append("-progress pipe:1 -f flv ");
            builder.Append(Quote(ingestUrl));

            return builder.ToString();
        }

        private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;

            WriteLog(e.Data);
            if (TryUpdateProgress(e.Data))
                return;

            if (e.Data.Contains("frame=", StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains("bitrate=", StringComparison.OrdinalIgnoreCase))
            {
                TryUpdateStatsLine(e.Data);
                return;
            }

            if (e.Data.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                e.Data.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                PublishStatus(e.Data);
            }
        }

        private void Process_Exited(object? sender, EventArgs e)
        {
            WriteLog("ffmpeg exited.");
            DisposeProcess();
            StreamingStateChanged?.Invoke(this, false);
            PublishStatus("Stream ended.");
            CloseLog();
        }

        private void DisposeProcess()
        {
            if (_ffmpegProcess is null)
                return;

            _ffmpegProcess.ErrorDataReceived -= Process_OutputDataReceived;
            _ffmpegProcess.OutputDataReceived -= Process_OutputDataReceived;
            _ffmpegProcess.Exited -= Process_Exited;
            _ffmpegProcess.Dispose();
            _ffmpegProcess = null;
        }

        private static bool ShouldUseWindowsLoopbackAudio(string desktopAudioInput, bool desktopAudioMuted)
        {
            return !desktopAudioMuted && IsWindowsLoopbackAudio(desktopAudioInput);
        }

        private static bool IsWindowsLoopbackAudio(string desktopAudioInput)
        {
            return string.Equals(desktopAudioInput?.Trim(), WindowsLoopbackAudioDeviceName, StringComparison.OrdinalIgnoreCase);
        }

        private void ResetStats()
        {
            _currentStats.Frame = 0;
            _currentStats.Fps = 0;
            _currentStats.Bitrate = "--";
            _currentStats.Speed = "--";
            _currentStats.DroppedFrames = 0;
            _currentStats.DuplicatedFrames = 0;
            _currentStats.OutputTime = "--";
            _currentStats.LogPath = _currentLogPath ?? string.Empty;
            StatsChanged?.Invoke(this, _currentStats.Clone());
        }

        private bool TryUpdateProgress(string line)
        {
            var splitAt = line.IndexOf('=');
            if (splitAt <= 0)
                return false;

            var key = line[..splitAt].Trim();
            var value = line[(splitAt + 1)..].Trim();
            bool changed = true;

            switch (key)
            {
                case "frame":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
                        _currentStats.Frame = frame;
                    break;
                case "fps":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
                        _currentStats.Fps = fps;
                    break;
                case "bitrate":
                    _currentStats.Bitrate = string.IsNullOrWhiteSpace(value) || string.Equals(value, "N/A", StringComparison.OrdinalIgnoreCase)
                        ? "--"
                        : value;
                    break;
                case "dup_frames":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dup))
                        _currentStats.DuplicatedFrames = dup;
                    break;
                case "drop_frames":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var drop))
                        _currentStats.DroppedFrames = drop;
                    break;
                case "out_time":
                    _currentStats.OutputTime = value;
                    break;
                case "speed":
                    _currentStats.Speed = value;
                    break;
                case "progress":
                    if (string.Equals(value, "end", StringComparison.OrdinalIgnoreCase))
                        PublishStatus("Stream ended. Log: " + _currentLogPath);
                    break;
                default:
                    changed = false;
                    break;
            }

            if (!changed)
                return true;

            _currentStats.LogPath = _currentLogPath ?? string.Empty;
            StatsChanged?.Invoke(this, _currentStats.Clone());
            PublishStatus($"Live: {_currentStats.Fps:0.0} fps, {_currentStats.Bitrate}, speed {_currentStats.Speed}");
            return true;
        }

        private void TryUpdateStatsLine(string line)
        {
            var fpsMatch = Regex.Match(line, @"fps=\s*(?<value>[0-9.]+)");
            if (fpsMatch.Success &&
                double.TryParse(fpsMatch.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
            {
                _currentStats.Fps = fps;
            }

            var frameMatch = Regex.Match(line, @"frame=\s*(?<value>[0-9]+)");
            if (frameMatch.Success &&
                long.TryParse(frameMatch.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
            {
                _currentStats.Frame = frame;
            }

            var bitrateMatch = Regex.Match(line, @"bitrate=\s*(?<value>\S+)");
            if (bitrateMatch.Success)
                _currentStats.Bitrate = bitrateMatch.Groups["value"].Value;

            var speedMatch = Regex.Match(line, @"speed=\s*(?<value>\S+)");
            if (speedMatch.Success)
                _currentStats.Speed = speedMatch.Groups["value"].Value;

            _currentStats.LogPath = _currentLogPath ?? string.Empty;
            StatsChanged?.Invoke(this, _currentStats.Clone());
            PublishStatus($"Live: {_currentStats.Fps:0.0} fps, {_currentStats.Bitrate}, speed {_currentStats.Speed}");
        }

        private void PublishStatus(string message)
        {
            LastStatus = message;
            StatusChanged?.Invoke(this, message);
        }

        private void StartLog(string ffmpegPath, string safeArguments)
        {
            CloseLog();

            var logFolder = ResolveStreamingLogFolder();
            Directory.CreateDirectory(logFolder);
            _currentLogPath = Path.Combine(logFolder, $"streaming-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt");
            _logWriter = new StreamWriter(new FileStream(_currentLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
            {
                AutoFlush = true
            };

            WriteLog("Zink Streaming Log");
            WriteLog("Started: " + DateTimeOffset.Now);
            WriteLog("Output: 1920x1080 @ 60fps");
            WriteLog("Encoder: H.264 libx264 high, no B-frames, low-latency CBR");
            WriteLog("Capture: full desktop via gdigrab, scaled to 1080p");
            WriteLog("ffmpeg: " + ffmpegPath);
            WriteLog("arguments: " + safeArguments);
        }

        private void WriteLog(string message)
        {
            try
            {
                _logWriter?.WriteLine($"[{DateTimeOffset.Now:O}] {message}");
            }
            catch
            {
            }
        }

        private void CloseLog()
        {
            try
            {
                _logWriter?.Flush();
                _logWriter?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _logWriter = null;
            }
        }

        private static string ResolveStreamingLogFolder()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                var logsPath = Path.Combine(current.FullName, "Logs");
                if (Directory.Exists(logsPath))
                    return Path.Combine(logsPath, "Streaming Logs");

                current = current.Parent;
            }

            return Path.Combine(AppContext.BaseDirectory, "Logs", "Streaming Logs");
        }

        private static IReadOnlyList<string> ParseDirectShowAudioDevices(string output)
        {
            var devices = new List<string>();
            foreach (Match match in Regex.Matches(output, "\"(?<name>[^\"]+)\" \\(audio\\)", RegexOptions.IgnoreCase))
            {
                var name = match.Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(name) && !ContainsDeviceName(devices, name))
                    devices.Add(name);
            }

            return devices;
        }

        private static bool ContainsDeviceName(IEnumerable<string> devices, string name)
        {
            foreach (var device in devices)
            {
                if (string.Equals(device, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string BuildAudioFilter(
            int? desktopInputIndex,
            double desktopVolume,
            int? microphoneInputIndex,
            double microphoneVolume)
        {
            var parts = new List<string>();
            var mixInputs = new List<string>();

            if (desktopInputIndex.HasValue)
            {
                parts.Add($"[{desktopInputIndex.Value}:a]volume={desktopVolume.ToString("0.###", CultureInfo.InvariantCulture)}[desktop_audio]");
                mixInputs.Add("[desktop_audio]");
            }

            if (microphoneInputIndex.HasValue)
            {
                parts.Add($"[{microphoneInputIndex.Value}:a]volume={microphoneVolume.ToString("0.###", CultureInfo.InvariantCulture)}[mic_audio]");
                mixInputs.Add("[mic_audio]");
            }

            if (mixInputs.Count == 1)
                parts.Add($"{mixInputs[0]}anull[mixed_audio]");
            else
                parts.Add($"{string.Concat(mixInputs)}amix=inputs={mixInputs.Count}:duration=longest:dropout_transition=0[mixed_audio]");

            return string.Join(';', parts);
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        private sealed class DesktopLoopbackAudioSession : IDisposable
        {
            private readonly CancellationTokenSource _cancellationTokenSource = new();
            private readonly NamedPipeServerStream _pipe;
            private readonly WasapiLoopbackCapture _capture;
            private Task? _connectionTask;
            private bool _disposed;

            private DesktopLoopbackAudioSession(string pipeName, WasapiLoopbackCapture capture)
            {
                _capture = capture;
                PipePath = @"\\.\pipe\" + pipeName;
                SampleRate = capture.WaveFormat.SampleRate;
                Channels = capture.WaveFormat.Channels;
                FfmpegFormat = ResolveFfmpegFormat(capture.WaveFormat);
                _pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                _capture.DataAvailable += Capture_DataAvailable;
            }

            public string PipePath { get; }
            public int SampleRate { get; }
            public int Channels { get; }
            public string FfmpegFormat { get; }

            public static DesktopLoopbackAudioSession Create()
            {
                var pipeName = "zink-stream-desktop-audio-" + Guid.NewGuid().ToString("N");
                return new DesktopLoopbackAudioSession(pipeName, new WasapiLoopbackCapture());
            }

            public void Start()
            {
                _connectionTask = Task.Run(async () =>
                {
                    await _pipe.WaitForConnectionAsync(_cancellationTokenSource.Token);
                    if (!_cancellationTokenSource.IsCancellationRequested)
                        _capture.StartRecording();
                });
            }

            private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
            {
                if (_disposed || !_pipe.IsConnected || e.BytesRecorded <= 0)
                    return;

                try
                {
                    _pipe.Write(e.Buffer, 0, e.BytesRecorded);
                    _pipe.Flush();
                }
                catch
                {
                }
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                try
                {
                    _cancellationTokenSource.Cancel();
                    _capture.StopRecording();
                }
                catch
                {
                }

                _capture.DataAvailable -= Capture_DataAvailable;
                _capture.Dispose();
                _pipe.Dispose();
                _cancellationTokenSource.Dispose();
            }

            private static string ResolveFfmpegFormat(WaveFormat waveFormat)
            {
                if (waveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                    return "f32le";

                return waveFormat.BitsPerSample switch
                {
                    16 => "s16le",
                    24 => "s24le",
                    32 => "s32le",
                    _ => "s16le"
                };
            }
        }
    }

    public sealed class StreamingStats
    {
        public long Frame { get; set; }
        public double Fps { get; set; }
        public string Bitrate { get; set; } = "--";
        public string Speed { get; set; } = "--";
        public long DroppedFrames { get; set; }
        public long DuplicatedFrames { get; set; }
        public string OutputTime { get; set; } = "--";
        public string LogPath { get; set; } = string.Empty;

        public StreamingStats Clone()
        {
            return new StreamingStats
            {
                Frame = Frame,
                Fps = Fps,
                Bitrate = Bitrate,
                Speed = Speed,
                DroppedFrames = DroppedFrames,
                DuplicatedFrames = DuplicatedFrames,
                OutputTime = OutputTime,
                LogPath = LogPath
            };
        }
    }
}
