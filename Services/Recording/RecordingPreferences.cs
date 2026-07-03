using Windows.Storage;

namespace Zink.Services.Recording
{
    public static class RecordingPreferences
    {
        private const string GamingBackgroundReplayEnabledKey = "ZinkGamingBackgroundReplayEnabled";
        private const string HotkeyGameClipRecordingEnabledKey = "ZinkHotkeyGameClipRecordingEnabled";

        public static bool IsGamingBackgroundReplayEnabled
        {
            get
            {
                try
                {
                    object value = ApplicationData.Current.LocalSettings.Values[GamingBackgroundReplayEnabledKey];
                    if (value is bool enabled)
                        return enabled;
                }
                catch
                {
                }

                return false;
            }
        }

        public static void SetGamingBackgroundReplayEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[GamingBackgroundReplayEnabledKey] = enabled;
        }

        public static bool IsHotkeyGameClipRecordingEnabled
        {
            get
            {
                try
                {
                    object value = ApplicationData.Current.LocalSettings.Values[HotkeyGameClipRecordingEnabledKey];
                    if (value is bool enabled)
                        return enabled;
                }
                catch
                {
                }

                return true;
            }
        }

        public static void SetHotkeyGameClipRecordingEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[HotkeyGameClipRecordingEnabledKey] = enabled;
        }
    }
}
