using System;

namespace Zink.Services.NativeCalling
{
    internal static class NvidiaH264EncoderPolicy
    {
        public const string FamilyName = "NVENC";
        public const int AdapterVendorId = 0x10DE;
        public const int PreferenceScore = 0;

        public static bool MatchesHardwareText(string text)
        {
            return text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("NVENC", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("10DE", StringComparison.OrdinalIgnoreCase);
        }

        public static bool MatchesEncoderMode(string encoderMode)
        {
            return encoderMode.Contains("NVENC", StringComparison.OrdinalIgnoreCase) ||
                encoderMode.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        }
    }
}
