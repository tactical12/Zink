using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Zink.Services.NativeCalling;

namespace Zink.Services.Streaming
{
    public sealed class ObsStyleStreamingPipelineOptions
    {
        public int Width { get; init; } = NativeTwitchStreamingService.OutputWidth;
        public int Height { get; init; } = NativeTwitchStreamingService.OutputHeight;
        public int Fps { get; init; } = NativeTwitchStreamingService.OutputFps;
        public int VideoBitrateKbps { get; init; } = NativeTwitchStreamingService.VideoBitrateKbps;
        public string VideoEncoder { get; init; } = "NVENC H.264";
        public string CaptureMode { get; init; } = "Strict GPU desktop capture";
        public string OutputProtocol { get; init; } = "RTMP";
        public bool UseDedicatedRenderLoop { get; init; } = true;
        public bool UseDirectNvenc { get; init; }
        public bool UseGameCaptureHook { get; init; }
    }

    public sealed class ObsStyleStreamingStage
    {
        public ObsStyleStreamingStage(string name, string backend, bool active, string notes)
        {
            Name = name;
            Backend = backend;
            Active = active;
            Notes = notes;
        }

        public string Name { get; }
        public string Backend { get; }
        public bool Active { get; }
        public string Notes { get; }
    }

    public sealed class ObsStyleStreamingPipeline
    {
        private readonly IReadOnlyList<ObsStyleStreamingStage> _stages;

        public ObsStyleStreamingPipeline(ObsStyleStreamingPipelineOptions options)
        {
            Options = options;
            _stages = BuildStages(options);
        }

        public ObsStyleStreamingPipelineOptions Options { get; }
        public IReadOnlyList<ObsStyleStreamingStage> Stages => _stages;

        public string Describe()
        {
            return
                $"OBS-style pipeline: {Options.Width}x{Options.Height}@{Options.Fps}, {Options.VideoEncoder}, {Options.VideoBitrateKbps} kbps, {Options.OutputProtocol}.";
        }

        private static IReadOnlyList<ObsStyleStreamingStage> BuildStages(ObsStyleStreamingPipelineOptions options)
        {
            return new[]
            {
                new ObsStyleStreamingStage(
                    "Video source",
                    options.UseGameCaptureHook ? "Game capture hook" : "Windows Graphics Capture desktop source",
                    active: true,
                    options.UseGameCaptureHook
                        ? "OBS-style game capture is selected."
                        : "Current backend captures the desktop through WGC with bitmap readback disabled."),
                new ObsStyleStreamingStage(
                    "GPU compositor",
                    options.UseDedicatedRenderLoop ? "Dedicated 60 FPS render clock" : "Capture-driven render clock",
                    active: options.UseDedicatedRenderLoop,
                    "Separates output pacing from source timing so the stream can keep a stable cadence."),
                new ObsStyleStreamingStage(
                    "Video encoder",
                    options.UseDirectNvenc ? "Direct NVIDIA NVENC SDK" : "Media Foundation GPU H.264 MFT",
                    active: true,
                    options.UseDirectNvenc
                        ? "Direct NVENC path is selected."
                        : "Current backend uses the preferred available GPU H.264 Media Foundation encoder."),
                new ObsStyleStreamingStage(
                    "Audio mixer",
                    "Windows loopback + microphone AAC",
                    active: true,
                    "Desktop and microphone audio are mixed separately from video capture."),
                new ObsStyleStreamingStage(
                    "Publisher",
                    "Native RTMP publisher",
                    active: true,
                    "Publishes H.264/AAC to Twitch with monotonic RTMP timestamps.")
            };
        }
    }

    public interface IObsStyleVideoSource : IAsyncDisposable
    {
        string Name { get; }
        ValueTask StartAsync(CancellationToken cancellationToken);
        ValueTask StopAsync();
    }

    public interface IObsStyleVideoCompositor : IAsyncDisposable
    {
        int Width { get; }
        int Height { get; }
        int Fps { get; }
        ValueTask StartAsync(CancellationToken cancellationToken);
        ValueTask StopAsync();
    }

    public interface IObsStyleVideoEncoder : IAsyncDisposable
    {
        string Name { get; }
        bool IsHardwareAccelerated { get; }
        ValueTask StartAsync(CancellationToken cancellationToken);
        ValueTask StopAsync();
    }

    public interface IObsStyleStreamPublisher : IAsyncDisposable
    {
        string Protocol { get; }
        ValueTask ConnectAsync(string ingestUrl, CancellationToken cancellationToken);
        ValueTask StopAsync();
    }
}
