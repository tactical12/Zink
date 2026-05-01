using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation.Collections;
using Windows.Storage;

namespace Zink.Pages.Social
{
    public sealed partial class DeveloperSettingsPage : Page
    {
        private const string OfflineModeKey = "DeveloperSettings.OfflineMode";
        private const string MockCallsKey = "DeveloperSettings.MockCalls";
        private const string DisplayNameKey = "DeveloperSettings.DisplayName";
        private const string UsernameKey = "DeveloperSettings.Username";
        private const string CallServerKey = "DeveloperSettings.CallServer";

        private bool _isLoading;

        public DeveloperSettingsPage()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            _isLoading = true;

            var settings = ApplicationData.Current.LocalSettings.Values;
            OfflineModeToggle.IsOn = ReadBool(settings, OfflineModeKey);
            MockCallsToggle.IsOn = ReadBool(settings, MockCallsKey);
            DisplayNameBox.Text = ReadString(settings, DisplayNameKey, "Local tester");
            UsernameBox.Text = ReadString(settings, UsernameKey, "zink.local");
            CallServerBox.Text = ReadString(settings, CallServerKey, "http://localhost:5000");

            _isLoading = false;
        }

        private static bool ReadBool(IPropertySet settings, string key)
        {
            return settings.TryGetValue(key, out var value) && value is bool enabled && enabled;
        }

        private static string ReadString(IPropertySet settings, string key, string fallback)
        {
            return settings.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text)
                ? text
                : fallback;
        }

        private void SettingChanged(object sender, RoutedEventArgs e)
        {
            if (_isLoading)
                return;

            SaveSettings();
        }

        private void TextSettingChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoading)
                return;

            SaveSettings();
        }

        private void SaveSettings()
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            settings[OfflineModeKey] = OfflineModeToggle.IsOn;
            settings[MockCallsKey] = MockCallsToggle.IsOn;
            settings[DisplayNameKey] = DisplayNameBox.Text.Trim();
            settings[UsernameKey] = UsernameBox.Text.Trim();
            settings[CallServerKey] = CallServerBox.Text.Trim();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(RegisterPage));
        }
    }
}
