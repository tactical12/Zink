using System;
using System.Collections.Generic;
using System.Threading.Tasks;

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

        public event EventHandler<string>? StatusChanged;
        public event EventHandler<bool>? StreamingStateChanged;
        public event EventHandler<StreamingStats>? StatsChanged;

        public bool IsStreaming => false;
        public string LastStatus { get; private set; } = "Ready.";
        public string? CurrentLogPath => null;
        public StreamingStats CurrentStats => _currentStats.Clone();

        private TwitchStreamingService()
        {
        }

        public Task StartAsync(
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
            PublishStatus("External ffmpeg streaming is not available in the Microsoft Store build.");
            StreamingStateChanged?.Invoke(this, false);
            StatsChanged?.Invoke(this, _currentStats.Clone());
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            PublishStatus("Stream stopped.");
            StreamingStateChanged?.Invoke(this, false);
            return Task.CompletedTask;
        }

        public static Task<IReadOnlyList<string>> GetDirectShowAudioDevicesAsync()
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        private void PublishStatus(string status)
        {
            LastStatus = status;
            StatusChanged?.Invoke(this, status);
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
