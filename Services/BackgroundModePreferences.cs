using Windows.Storage;

namespace Zink.Services
{
    public static class BackgroundModePreferences
    {
        public const string BackgroundRunSettingKey = "ZinkBackgroundRunEnabled";
        public const string BackgroundNotificationsEnabledSettingKey = "ZinkBackgroundNotificationsEnabled";
        public const string LowResourceBackgroundModeEnabledSettingKey = "ZinkLowResourceBackgroundModeEnabled";
        public const string AppUpdateChecksEnabledSettingKey = "ZinkAppUpdateChecksEnabled";

        public static bool IsBackgroundRunEnabled => GetBoolean(BackgroundRunSettingKey, true);
        public static bool AreBackgroundNotificationsEnabled => GetBoolean(BackgroundNotificationsEnabledSettingKey, true);
        public static bool IsLowResourceBackgroundModeEnabled => GetBoolean(LowResourceBackgroundModeEnabledSettingKey, true);
        public static bool AreAppUpdateChecksEnabled => GetBoolean(AppUpdateChecksEnabledSettingKey, true);

        public static void SetBackgroundRunEnabled(bool enabled)
        {
            SetBoolean(BackgroundRunSettingKey, enabled);
        }

        public static void SetBackgroundNotificationsEnabled(bool enabled)
        {
            SetBoolean(BackgroundNotificationsEnabledSettingKey, enabled);
        }

        public static void SetLowResourceBackgroundModeEnabled(bool enabled)
        {
            SetBoolean(LowResourceBackgroundModeEnabledSettingKey, enabled);
        }

        public static void SetAppUpdateChecksEnabled(bool enabled)
        {
            SetBoolean(AppUpdateChecksEnabledSettingKey, enabled);
        }

        private static bool GetBoolean(string key, bool defaultValue)
        {
            try
            {
                object value = ApplicationData.Current.LocalSettings.Values[key];
                if (value is bool boolValue)
                    return boolValue;
            }
            catch
            {
            }

            return defaultValue;
        }

        private static void SetBoolean(string key, bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[key] = enabled;
        }
    }
}
