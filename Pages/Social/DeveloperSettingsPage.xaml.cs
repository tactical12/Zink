using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Foundation.Collections;
using Windows.Storage;
using Zink.Services.Social;

namespace Zink.Pages.Social
{
    public sealed partial class DeveloperSettingsPage : Page
    {
        private const string DeveloperSettingsPassword = "++773317";
        private const string OfflineModeKey = "DeveloperSettings.OfflineMode";
        private const string MockCallsKey = "DeveloperSettings.MockCalls";
        private const string DisplayNameKey = "DeveloperSettings.DisplayName";
        private const string UsernameKey = "DeveloperSettings.Username";
        private const string EmailKey = "DeveloperSettings.Email";
        private const string CallServerKey = "DeveloperSettings.CallServer";

        private bool _isLoading;
        private bool _isUnlocked;
        private bool _passwordPromptOpen;

        public DeveloperSettingsPage()
        {
            InitializeComponent();
            Loaded += DeveloperSettingsPage_Loaded;
        }

        private async void DeveloperSettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isUnlocked || _passwordPromptOpen)
                return;

            _passwordPromptOpen = true;

            try
            {
                var passwordBox = new PasswordBox
                {
                    Header = "Developer password",
                    PlaceholderText = "Enter password"
                };

                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Developer settings locked",
                    Content = passwordBox,
                    PrimaryButtonText = "Unlock",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary &&
                    string.Equals(passwordBox.Password, DeveloperSettingsPassword, StringComparison.Ordinal))
                {
                    _isUnlocked = true;
                    LoadSettings();
                    return;
                }

                Frame.Navigate(typeof(RegisterPage));
            }
            finally
            {
                _passwordPromptOpen = false;
            }
        }

        private void LoadSettings()
        {
            _isLoading = true;

            var settings = ApplicationData.Current.LocalSettings.Values;
            OfflineModeToggle.IsOn = ReadBool(settings, OfflineModeKey);
            MockCallsToggle.IsOn = ReadBool(settings, MockCallsKey);
            DisplayNameBox.Text = ReadString(settings, DisplayNameKey, "Local tester");
            UsernameBox.Text = ReadString(settings, UsernameKey, "zink.local");
            EmailBox.Text = ReadString(settings, EmailKey, "tester@zink.local");
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
            settings[EmailKey] = EmailBox.Text.Trim();
            settings[CallServerKey] = CallServerBox.Text.Trim();
        }

        private async void RegisterDeveloperAccountButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetRegisterBusy(true);
                RegisterStatusText.Text = "";
                SaveSettings();

                var username = UsernameBox.Text.Trim();
                var displayName = DisplayNameBox.Text.Trim();
                var email = EmailBox.Text.Trim();
                var password = PasswordBox.Password;

                if (string.IsNullOrWhiteSpace(username))
                    throw new InvalidOperationException("Enter a test username.");

                if (string.IsNullOrWhiteSpace(password))
                    throw new InvalidOperationException("Enter a test password.");

                var auth = await SocialManager.Instance.Api.RegisterAsync(
                    new RegisterRequest(email, password, username, displayName));

                try
                {
                    await SocialManager.Instance.Realtime.ConnectAsync();
                    RegisterStatusText.Text = $"Registered and connected as {auth.Username}.";
                }
                catch
                {
                    RegisterStatusText.Text = $"Registered as {auth.Username}. Realtime connection will retry when available.";
                }
            }
            catch (Exception ex)
            {
                RegisterStatusText.Text = ex.Message;
            }
            finally
            {
                SetRegisterBusy(false);
            }
        }

        private void SetRegisterBusy(bool isBusy)
        {
            RegisterBusyRing.IsActive = isBusy;
            RegisterBusyRing.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(RegisterPage));
        }
    }
}
