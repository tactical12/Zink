using System;

namespace Zink.Services.Gaming
{
    public sealed class FpsMonitorSnapshot
    {
        public double CurrentFps { get; init; }
        public double AverageFps { get; init; }
        public double OnePercentLowFps { get; init; }
        public double PointOnePercentLowFps { get; init; }
        public double FrameTimeMs { get; init; }
        public double CpuUsagePercent { get; init; }
        public string FpsSource { get; init; } = "Capture";
        public long FrameCount { get; init; }
        public bool IsMonitoring { get; init; }
        public bool IsRecording { get; init; }
        public string TargetName { get; init; } = "No game selected";
        public string Status { get; init; } = "Idle";
        public TimeSpan Duration { get; init; }
        public string? RecordingPath { get; init; }
    }
}
