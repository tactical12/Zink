using System;

namespace Zink.Services.Recording
{
    internal sealed class RecordingSegment
    {
        public string Path { get; init; } = string.Empty;
        public TimeSpan Start { get; init; }
        public TimeSpan End { get; init; }

        public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;
    }
}