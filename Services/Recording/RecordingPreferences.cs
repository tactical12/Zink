using Windows.Storage;

namespace Zink.Services.Recording
{
    public static class RecordingPreferences
    {
        private const string GamingBackgroundReplayEnabledKey = "ZinkGamingBackgroundReplayEnabled";

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
    }
}
