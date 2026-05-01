using System;

namespace Zink.Services.Recording
{
    public sealed class AudioPacket
    {
        public TimeSpan Timestamp { get; set; }
        public byte[] PcmData { get; set; } = Array.Empty<byte>();
        public int SampleRate { get; set; } = 48000;
        public int Channels { get; set; } = 2;
        public int BitsPerSample { get; set; } = 16;
        public ushort FormatTag { get; set; } = 1;
        public bool IsFloatFormat { get; set; }
    }
}
