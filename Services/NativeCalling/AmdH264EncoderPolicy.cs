using System;

namespace Zink.Services.NativeCalling
{
    internal static class AmdH264EncoderPolicy
    {
        public const string FamilyName = "AMD AMF";
        public const int AdapterVendorId = 0x1002;
        public const int PreferenceScore = 2;
        public const int Safe1080pTwitchFps = 24;
        public const int Safe1080pTwitchBitrate = 2_500_000;

        public static bool MatchesHardwareText(string text)
        {
            return text.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("ADVANCED MICRO DEVICES", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("AMF", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("1002", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("1022", StringComparison.OrdinalIgnoreCase);
        }

        public static bool MatchesEncoderMode(string encoderMode)
        {
            return encoderMode.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                encoderMode.Contains("AMF", StringComparison.OrdinalIgnoreCase);
        }
    }
}
