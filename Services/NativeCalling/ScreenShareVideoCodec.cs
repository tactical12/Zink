using System;

namespace Zink.Services.NativeCalling
{
    public enum ScreenShareVideoCodec
    {
        Auto,
        H264,
        AV1X
    }

    public enum ScreenShareH264EncoderFamily
    {
        Auto,
        Nvidia,
        Intel
    }

    internal static class ScreenShareCodecNames
    {
        public const string H264 = "h264";
        public const string Av1 = "av1";
        public const string AV1XDisplayName = "AV1X";

        public static bool IsAv1(string? codec)
        {
            return string.Equals(codec, Av1, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(codec, "av1x", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsH264(string? codec)
        {
            return string.IsNullOrWhiteSpace(codec) ||
                string.Equals(codec, H264, StringComparison.OrdinalIgnoreCase);
        }
    }
}
