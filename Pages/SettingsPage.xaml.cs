using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using Windows.ApplicationModel;
using Windows.Services.Store;
using Windows.Storage;
using Windows.System;
using WinRT.Interop; // <-- needed for WindowNative & InitializeWithWindow
using Zink.Services;
using Zink.Services.Recording;

namespace Zink.Pages
{
    public sealed partial class SettingsPage : Page
    {
        private readonly StoreContext _store;
        private bool _isLoadingStartupState;
        private bool _isLoadingReplayState;
        private bool _isLoadingDiagnosticLogState;

        private const string BackgroundRunSettingKey = "ZinkBackgroundRunEnabled";

        public SettingsPage()
        {
            this.InitializeComponent();

            // Create the StoreContext
            _store = StoreContext.GetDefault();

            // IMPORTANT: attach the StoreContext to your main WinUI 3 window
            try
            {
                // App.MainWindow is your main WinUI 3 window (you already use it elsewhere)
                IntPtr hwnd = WindowNative.GetWindowHandle(App.MainWindow);
                InitializeWithWindow.Initialize(_store, hwnd);
            }
            catch (Exception ex)
            {
                // Optional: show a one-time init error (won’t stop the app)
                if (StatusText != null)
                {
                    StatusText.Text = $"Error initialising update system: {ex.Message}";
                }
            }

            Loaded += SettingsPage_Loaded;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await LoadStartupTaskStateAsync();
            LoadReplaySettingState();
            LoadDiagnosticLogSettingState();
        }

        private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadStartupTaskStateAsync();
            LoadReplaySettingState();
            LoadDiagnosticLogSettingState();
        }

        private async System.Threading.Tasks.Task LoadStartupTaskStateAsync()
        {
            _isLoadingStartupState = true;

            try
            {
                bool backgroundRunEnabled = GetBackgroundRunEnabledSetting();

                var startupTask = await StartupTask.GetAsync("ZinkStartupTask");

                switch (startupTask.State)
                {
                    case StartupTaskState.Enabled:
                    case StartupTaskState.EnabledByPolicy:
                        StartupToggle.IsOn = backgroundRunEnabled;
                        StartupStatusText.Text = backgroundRunEnabled
                            ? "Zink background startup is enabled."
                            : "Windows startup is enabled, but background startup is turned off in app settings.";
                        break;

                    case StartupTaskState.Disabled:
                        StartupToggle.IsOn = false;
                        StartupStatusText.Text = "Zink background startup is disabled.";
                        break;

                    case StartupTaskState.DisabledByUser:
                        StartupToggle.IsOn = false;
                        StartupStatusText.Text = "Startup is disabled by the user in Windows.";
                        break;

                    case StartupTaskState.DisabledByPolicy:
                        StartupToggle.IsOn = false;
                        StartupStatusText.Text = "Startup is disabled by system policy.";
                        break;

                    default:
                        StartupToggle.IsOn = false;
                        StartupStatusText.Text = $"Startup status: {startupTask.State}";
                        break;
                }
            }
            catch (Exception ex)
            {
                StartupToggle.IsOn = false;
                StartupStatusText.Text = $"Error loading startup setting: {ex.Message}";
            }
            finally
            {
                _isLoadingStartupState = false;
            }
        }

        private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingStartupState)
                return;

            try
            {
                var startupTask = await StartupTask.GetAsync("ZinkStartupTask");

                if (StartupToggle.IsOn)
                {
                    SetBackgroundRunEnabledSetting(true);

                    var newState = await startupTask.RequestEnableAsync();

                    switch (newState)
                    {
                        case StartupTaskState.Enabled:
                        case StartupTaskState.EnabledByPolicy:
                            StartupToggle.IsOn = true;
                            StartupStatusText.Text = "Zink background startup is enabled.";
                            break;

                        case StartupTaskState.DisabledByUser:
                            SetBackgroundRunEnabledSetting(false);
                            StartupToggle.IsOn = false;
                            StartupStatusText.Text = "Startup is disabled by the user in Windows. Re-enable it in Task Manager > Startup apps.";
                            break;

                        case StartupTaskState.DisabledByPolicy:
                            SetBackgroundRunEnabledSetting(false);
                            StartupToggle.IsOn = false;
                            StartupStatusText.Text = "Startup is disabled by system policy.";
                            break;

                        default:
                            SetBackgroundRunEnabledSetting(false);
                            StartupToggle.IsOn = false;
                            StartupStatusText.Text = $"Unable to enable startup. Current state: {newState}";
                            break;
                    }
                }
                else
                {
                    SetBackgroundRunEnabledSetting(false);
                    startupTask.Disable();
                    StartupToggle.IsOn = false;
                    StartupStatusText.Text = "Zink background startup is disabled.";
                }

                await LoadStartupTaskStateAsync();
            }
            catch (Exception ex)
            {
                StartupStatusText.Text = $"Error changing startup setting: {ex.Message}";
            }
        }

        private void LoadReplaySettingState()
        {
            _isLoadingReplayState = true;

            try
            {
                bool enabled = RecordingPreferences.IsGamingBackgroundReplayEnabled;
                BackgroundReplayOffToggle.IsOn = !enabled;
                BackgroundReplayStatusText.Text = enabled
                    ? "Background replay buffer is allowed for gaming clips."
                    : "Background replay buffer is off and will not start automatically.";
            }
            finally
            {
                _isLoadingReplayState = false;
            }
        }

        private async void BackgroundReplayOffToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingReplayState)
                return;

            bool enabled = !BackgroundReplayOffToggle.IsOn;
            RecordingPreferences.SetGamingBackgroundReplayEnabled(enabled);

            if (enabled)
            {
                BackgroundReplayStatusText.Text = "Background replay buffer is allowed for gaming clips.";
                StatusText.Text = "Background replay setting saved.";
                return;
            }

            BackgroundReplayStatusText.Text = "Background replay buffer is off and will not start automatically.";

            try
            {
                var service = ManualRecordingService.Instance;
                if (service.IsReplayBufferRunning)
                {
                    await service.StopAsync();

                    if (Application.Current is App app)
                    {
                        app.NotifyReplayBufferStopped();
                    }

                    StatusText.Text = "Background replay buffer stopped and setting saved.";
                }
                else
                {
                    StatusText.Text = "Background replay setting saved.";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Background replay setting saved, but stopping the active buffer failed: {ex.Message}";
            }
        }

        private async void ResetDefaultsButton_Click(object sender, RoutedEventArgs e)
        {
            ResetDefaultsButton.IsEnabled = false;

            try
            {
                StatusText.Text = "Resetting settings to defaults...";

                SetBackgroundRunEnabledSetting(true);
                RecordingPreferences.SetGamingBackgroundReplayEnabled(true);
                DiagnosticLogService.SetEnabled(true);
                LoadReplaySettingState();
                LoadDiagnosticLogSettingState();

                var startupTask = await StartupTask.GetAsync("ZinkStartupTask");
                var newState = await startupTask.RequestEnableAsync();

                switch (newState)
                {
                    case StartupTaskState.Enabled:
                    case StartupTaskState.EnabledByPolicy:
                        StartupToggle.IsOn = true;
                        StartupStatusText.Text = "Zink background startup is enabled.";
                        StatusText.Text = "Settings reset to defaults.";
                        break;

                    case StartupTaskState.DisabledByUser:
                        StartupToggle.IsOn = false;
                        StartupStatusText.Text = "Startup is disabled by the user in Windows. Re-enable it in Task Manager > Startup apps.";
                        StatusText.Text = "Startup was reset, but Windows is still blocking startup.";
                        break;

                    case StartupTaskState.DisabledByPolicy:
                        StartupToggle.IsOn = false;
                        StartupStatusText.Text = "Startup is disabled by system policy.";
                        StatusText.Text = "Startup was reset, but startup is blocked by system policy.";
                        break;

                    default:
                        StartupToggle.IsOn = false;
                        StartupStatusText.Text = $"Unable to enable startup. Current state: {newState}";
                        StatusText.Text = "Defaults were partially reset, but Windows startup could not be enabled.";
                        break;
                }

                await LoadStartupTaskStateAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error resetting settings: {ex.Message}";
            }
            finally
            {
                ResetDefaultsButton.IsEnabled = true;
            }
        }

        private static bool GetBackgroundRunEnabledSetting()
        {
            try
            {
                object value = ApplicationData.Current.LocalSettings.Values[BackgroundRunSettingKey];
                if (value is bool boolValue)
                    return boolValue;
            }
            catch
            {
            }

            return true;
        }

        private static void SetBackgroundRunEnabledSetting(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[BackgroundRunSettingKey] = enabled;
        }

        private void LoadDiagnosticLogSettingState()
        {
            _isLoadingDiagnosticLogState = true;

            try
            {
                DiagnosticLogToggle.IsOn = DiagnosticLogService.GetEnabledSetting();
                DiagnosticLogStatusText.Text = $"Logging to {DiagnosticLogService.CurrentLogPath}";
            }
            finally
            {
                _isLoadingDiagnosticLogState = false;
            }
        }

        private void DiagnosticLogToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingDiagnosticLogState)
                return;

            if (!DiagnosticLogToggle.IsOn)
                DiagnosticLogToggle.IsOn = true;

            DiagnosticLogService.SetEnabled(true);
            LoadDiagnosticLogSettingState();
            StatusText.Text = "Diagnostic file logging stays enabled while stream diagnostics are active.";
        }

        private async void OpenDiagnosticLogFolderButton_Click(object sender, RoutedEventArgs e)
        {
            OpenDiagnosticLogFolderButton.IsEnabled = false;

            try
            {
                var folder = await DiagnosticLogService.GetLogFolderAsync();
                await Launcher.LaunchFolderAsync(folder);
                StatusText.Text = "Diagnostic log folder opened.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error opening log folder: {ex.Message}";
            }
            finally
            {
                OpenDiagnosticLogFolderButton.IsEnabled = true;
            }
        }

        private void ClearDiagnosticLogButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DiagnosticLogService.ClearCurrentLog();
                LoadDiagnosticLogSettingState();
                StatusText.Text = "Diagnostic log cleared.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error clearing diagnostic log: {ex.Message}";
            }
        }

        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            CheckForUpdatesButton.IsEnabled = false;
            StatusText.Text = "Checking for updates…";

            try
            {
                // 1) get list of available updates
                var updates = await _store.GetAppAndOptionalStorePackageUpdatesAsync();

                if (updates.Count == 0)
                {
                    StatusText.Text = "Your app is up to date.";
                }
                else
                {
                    StatusText.Text = $"{updates.Count} update(s) available. Downloading…";

                    // 2) download & install them
                    var result = await _store.RequestDownloadAndInstallStorePackageUpdatesAsync(updates);

                    // 3) examine the result
                    if (result.OverallState == StorePackageUpdateState.Completed)
                    {
                        StatusText.Text = "Update installed. Restart your app to apply changes.";
                    }
                    else
                    {
                        StatusText.Text = $"Update failed: {result.OverallState}";
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error checking for updates: {ex.Message}";
            }
            finally
            {
                CheckForUpdatesButton.IsEnabled = true;
            }
        }
    }
}
