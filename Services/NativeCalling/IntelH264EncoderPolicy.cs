using System;

namespace Zink.Services.NativeCalling
{
    internal static class IntelH264EncoderPolicy
    {
        public const string FamilyName = "Intel Quick Sync";
        public const int AdapterVendorId = 0x8086;
        public const int PreferenceScore = 1;
        public const int Safe1080pTwitchFps = 24;
        public const int Safe1080pTwitchBitrate = 2_500_000;

        public static bool MatchesHardwareText(string text)
        {
            return text.Contains("INTEL", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("QUICK SYNC", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("8086", StringComparison.OrdinalIgnoreCase);
        }

        public static bool MatchesEncoderMode(string encoderMode)
        {
            return encoderMode.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                encoderMode.Contains("Quick Sync", StringComparison.OrdinalIgnoreCase);
        }
    }
}
