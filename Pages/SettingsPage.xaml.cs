using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.IO;
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
        private bool _isLoadingAppUpdatesState;
        private bool _isLoadingBackgroundNotificationsState;
        private bool _isLoadingLowResourceBackgroundState;
        private bool _isLoadingDiscordRichPresenceState;
        private string? _latestHealthReportPath;
        private string? _latestSupportBundlePath;

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
            LoadBackgroundNotificationSettingState();
            LoadLowResourceBackgroundSettingState();
            LoadReplaySettingState();
            LoadDiscordRichPresenceSettingState();
            LoadDiagnosticLogSettingState();
            LoadAppUpdatesSettingState();
        }

        private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadStartupTaskStateAsync();
            LoadBackgroundNotificationSettingState();
            LoadLowResourceBackgroundSettingState();
            LoadReplaySettingState();
            LoadDiscordRichPresenceSettingState();
            LoadDiagnosticLogSettingState();
            LoadAppUpdatesSettingState();
        }

        private async System.Threading.Tasks.Task LoadStartupTaskStateAsync()
        {
            _isLoadingStartupState = true;

            try
            {
                bool backgroundRunEnabled = BackgroundModePreferences.IsBackgroundRunEnabled;

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
                    BackgroundModePreferences.SetBackgroundRunEnabled(true);

                    var newState = await startupTask.RequestEnableAsync();

                    switch (newState)
                    {
                        case StartupTaskState.Enabled:
                        case StartupTaskState.EnabledByPolicy:
                            StartupToggle.IsOn = true;
                            StartupStatusText.Text = "Zink background startup is enabled.";
                            break;

                        case StartupTaskState.DisabledByUser:
                            BackgroundModePreferences.SetBackgroundRunEnabled(false);
                            StartupToggle.IsOn = false;
                            StartupStatusText.Text = "Startup is disabled by the user in Windows. Re-enable it in Task Manager > Startup apps.";
                            break;

                        case StartupTaskState.DisabledByPolicy:
                            BackgroundModePreferences.SetBackgroundRunEnabled(false);
                            StartupToggle.IsOn = false;
                            StartupStatusText.Text = "Startup is disabled by system policy.";
                            break;

                        default:
                            BackgroundModePreferences.SetBackgroundRunEnabled(false);
                            StartupToggle.IsOn = false;
                            StartupStatusText.Text = $"Unable to enable startup. Current state: {newState}";
                            break;
                    }
                }
                else
                {
                    BackgroundModePreferences.SetBackgroundRunEnabled(false);
                    startupTask.Disable();
                    StartupToggle.IsOn = false;
                    StartupStatusText.Text = "Zink background startup is disabled.";
                }

                await ZinkBackgroundModeService.Instance.ApplyAsync();
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

                BackgroundModePreferences.SetBackgroundRunEnabled(true);
                BackgroundModePreferences.SetBackgroundNotificationsEnabled(true);
                BackgroundModePreferences.SetLowResourceBackgroundModeEnabled(true);
                BackgroundModePreferences.SetAppUpdateChecksEnabled(true);
                DiscordPresenceService.Instance.SetEnabled(true);
                RecordingPreferences.SetGamingBackgroundReplayEnabled(false);
                DiagnosticLogService.SetEnabled(true);
                LoadBackgroundNotificationSettingState();
                LoadLowResourceBackgroundSettingState();
                LoadReplaySettingState();
                LoadDiscordRichPresenceSettingState();
                LoadDiagnosticLogSettingState();
                LoadAppUpdatesSettingState();
                await ZinkBackgroundModeService.Instance.ApplyAsync();

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

        private void LoadAppUpdatesSettingState()
        {
            _isLoadingAppUpdatesState = true;

            try
            {
                var enabled = BackgroundModePreferences.AreAppUpdateChecksEnabled;
                AppUpdatesToggle.IsOn = enabled;
                CheckForUpdatesButton.IsEnabled = enabled;
                AppUpdatesStatusText.Text = enabled
                    ? "Manual Store update checks are enabled."
                    : "App update checks are disabled.";
            }
            finally
            {
                _isLoadingAppUpdatesState = false;
            }
        }

        private void AppUpdatesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingAppUpdatesState)
                return;

            var enabled = AppUpdatesToggle.IsOn;
            BackgroundModePreferences.SetAppUpdateChecksEnabled(enabled);
            CheckForUpdatesButton.IsEnabled = enabled;
            AppUpdatesStatusText.Text = enabled
                ? "Manual Store update checks are enabled."
                : "App update checks are disabled.";
            StatusText.Text = enabled
                ? "App update checks enabled."
                : "App update checks disabled.";
            _ = ZinkBackgroundModeService.Instance.ApplyAsync();
        }

        private void LoadDiscordRichPresenceSettingState()
        {
            _isLoadingDiscordRichPresenceState = true;

            try
            {
                var enabled = DiscordPresenceService.GetEnabledSetting();
                DiscordRichPresenceToggle.IsOn = enabled;
                DiscordRichPresenceStatusText.Text = enabled
                    ? "Zink activity can appear on your Discord profile."
                    : "Zink will not show activity on your Discord profile.";
            }
            finally
            {
                _isLoadingDiscordRichPresenceState = false;
            }
        }

        private void DiscordRichPresenceToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingDiscordRichPresenceState)
                return;

            var enabled = DiscordRichPresenceToggle.IsOn;
            DiscordPresenceService.Instance.SetEnabled(enabled);
            DiscordRichPresenceStatusText.Text = enabled
                ? "Zink activity can appear on your Discord profile."
                : "Zink will not show activity on your Discord profile.";
            StatusText.Text = enabled
                ? "Discord Rich Presence enabled."
                : "Discord Rich Presence disabled.";
        }

        private void LoadBackgroundNotificationSettingState()
        {
            _isLoadingBackgroundNotificationsState = true;

            try
            {
                var enabled = BackgroundModePreferences.AreBackgroundNotificationsEnabled;
                BackgroundNotificationsToggle.IsOn = enabled;
                BackgroundNotificationsStatusText.Text = enabled
                    ? "Closing Zink keeps it running for message, call, friend request, and app update notifications."
                    : "Background notifications are off. Zink will not keep its lightweight listener running.";
                LowResourceBackgroundToggle.IsEnabled = enabled;
            }
            finally
            {
                _isLoadingBackgroundNotificationsState = false;
            }
        }

        private async void BackgroundNotificationsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingBackgroundNotificationsState)
                return;

            var enabled = BackgroundNotificationsToggle.IsOn;
            BackgroundModePreferences.SetBackgroundNotificationsEnabled(enabled);
            LowResourceBackgroundToggle.IsEnabled = enabled;
            BackgroundNotificationsStatusText.Text = enabled
                ? "Closing Zink keeps it running for message, call, friend request, and app update notifications."
                : "Background notifications are off. Zink will not keep its lightweight listener running.";
            StatusText.Text = enabled
                ? "Background notifications enabled."
                : "Background notifications disabled.";

            await ZinkBackgroundModeService.Instance.ApplyAsync();
        }

        private void LoadLowResourceBackgroundSettingState()
        {
            _isLoadingLowResourceBackgroundState = true;

            try
            {
                var enabled = BackgroundModePreferences.IsLowResourceBackgroundModeEnabled;
                LowResourceBackgroundToggle.IsOn = enabled;
                LowResourceBackgroundStatusText.Text = enabled
                    ? "Zink uses longer background polling intervals and a lower process priority."
                    : "Zink uses normal background responsiveness.";
            }
            finally
            {
                _isLoadingLowResourceBackgroundState = false;
            }
        }

        private async void LowResourceBackgroundToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isLoadingLowResourceBackgroundState)
                return;

            var enabled = LowResourceBackgroundToggle.IsOn;
            BackgroundModePreferences.SetLowResourceBackgroundModeEnabled(enabled);
            LowResourceBackgroundStatusText.Text = enabled
                ? "Zink uses longer background polling intervals and a lower process priority."
                : "Zink uses normal background responsiveness.";
            StatusText.Text = enabled
                ? "Low resource background mode enabled."
                : "Low resource background mode disabled.";

            await ZinkBackgroundModeService.Instance.ApplyAsync();
        }

        private void LoadDiagnosticLogSettingState()
        {
            _isLoadingDiagnosticLogState = true;

            try
            {
                DiagnosticLogToggle.IsOn = DiagnosticLogService.GetEnabledSetting();
                DiagnosticLogStatusText.Text = $"Logging to {DiagnosticLogService.CurrentLogPath}";
                _latestHealthReportPath = Path.Combine(
                    DiagnosticLogService.LogDirectoryPath,
                    $"zink-health-{DiagnosticLogService.DeviceName}-latest.txt");
                _latestSupportBundlePath = FindLatestSupportBundlePath();

                if (File.Exists(_latestHealthReportPath))
                    HealthCheckStatusText.Text = $"Latest report: {_latestHealthReportPath}";
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

        private async void RunHealthCheckButton_Click(object sender, RoutedEventArgs e)
        {
            RunHealthCheckButton.IsEnabled = false;
            OpenHealthReportButton.IsEnabled = false;
            HealthCheckStatusText.Text = "Running Zink health check...";
            StatusText.Text = "Running Zink health check...";

            try
            {
                var report = await ZinkHealthCheckService.RunAsync();
                _latestHealthReportPath = report.ReportPath;
                _latestSupportBundlePath = report.BundlePath;
                HealthCheckStatusText.Text = $"Health check complete: {report.Summary}. Report: {report.ReportPath}. Bundle: {report.BundlePath}";
                StatusText.Text = report.Failed == 0
                    ? "Health check complete."
                    : $"Health check found {report.Failed} failed check(s).";
            }
            catch (Exception ex)
            {
                HealthCheckStatusText.Text = $"Health check failed: {ex.Message}";
                StatusText.Text = $"Health check failed: {ex.Message}";
            }
            finally
            {
                RunHealthCheckButton.IsEnabled = true;
                OpenHealthReportButton.IsEnabled = true;
            }
        }

        private async void OpenHealthReportButton_Click(object sender, RoutedEventArgs e)
        {
            OpenHealthReportButton.IsEnabled = false;

            try
            {
                if (string.IsNullOrWhiteSpace(_latestHealthReportPath) || !File.Exists(_latestHealthReportPath))
                {
                    _latestHealthReportPath = Path.Combine(
                        DiagnosticLogService.LogDirectoryPath,
                        $"zink-health-{DiagnosticLogService.DeviceName}-latest.txt");
                }

                if (!File.Exists(_latestHealthReportPath))
                {
                    HealthCheckStatusText.Text = "No health report exists yet. Run a health check first.";
                    StatusText.Text = "No health report exists yet.";
                    return;
                }

                var file = await StorageFile.GetFileFromPathAsync(_latestHealthReportPath);
                await Launcher.LaunchFileAsync(file);
                StatusText.Text = "Health report opened.";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error opening health report: {ex.Message}";
            }
            finally
            {
                OpenHealthReportButton.IsEnabled = true;
            }
        }

        private async void UploadHealthReportButton_Click(object sender, RoutedEventArgs e)
        {
            UploadHealthReportButton.IsEnabled = false;
            HealthCheckStatusText.Text = "Uploading diagnostics support bundle...";
            StatusText.Text = "Uploading diagnostics support bundle...";

            try
            {
                if (string.IsNullOrWhiteSpace(_latestSupportBundlePath) || !File.Exists(_latestSupportBundlePath))
                    _latestSupportBundlePath = FindLatestSupportBundlePath();

                if (string.IsNullOrWhiteSpace(_latestSupportBundlePath) || !File.Exists(_latestSupportBundlePath))
                {
                    var report = await ZinkHealthCheckService.RunAsync();
                    _latestHealthReportPath = report.ReportPath;
                    _latestSupportBundlePath = report.BundlePath;
                }

                var result = await DiagnosticsUploadService.UploadSupportBundleAsync(_latestSupportBundlePath);
                HealthCheckStatusText.Text = $"Diagnostics uploaded. Report id: {result.ReportId}. Download: {result.DownloadUrl}";
                StatusText.Text = "Diagnostics uploaded.";
            }
            catch (Exception ex)
            {
                HealthCheckStatusText.Text = $"Diagnostics upload failed: {ex.Message}";
                StatusText.Text = $"Diagnostics upload failed: {ex.Message}";
            }
            finally
            {
                UploadHealthReportButton.IsEnabled = true;
            }
        }

        private static string? FindLatestSupportBundlePath()
        {
            try
            {
                var directory = DiagnosticLogService.LogDirectoryPath;
                if (!Directory.Exists(directory))
                    return null;

                var pattern = $"zink-support-{DiagnosticLogService.DeviceName}-*.zip";
                string? latestPath = null;
                DateTime latestWrite = DateTime.MinValue;

                foreach (var path in Directory.EnumerateFiles(directory, pattern))
                {
                    var write = File.GetLastWriteTimeUtc(path);
                    if (write <= latestWrite)
                        continue;

                    latestWrite = write;
                    latestPath = path;
                }

                return latestPath;
            }
            catch
            {
                return null;
            }
        }

        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            if (!BackgroundModePreferences.AreAppUpdateChecksEnabled)
            {
                CheckForUpdatesButton.IsEnabled = false;
                AppUpdatesStatusText.Text = "App update checks are disabled.";
                StatusText.Text = "Turn on app update checks before checking for updates.";
                return;
            }

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
