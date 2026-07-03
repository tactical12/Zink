using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading.Tasks;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.System;
using Zink.Services.Gaming;
using Zink.Windows;

namespace Zink.Pages
{
    public sealed partial class FpsRecorderPage : Page
    {
        private const string SettingsPrefix = "Zink.FpsRecorder.";
        private static readonly Uri RtssDownloadUri = new("https://www.guru3d.com/download/rtss-rivatuner-statistics-server-download/");
        private readonly FpsMonitorService _monitor = FpsMonitorService.Instance;
        private bool _loaded;

        public FpsRecorderPage()
        {
            InitializeComponent();
            Loaded += FpsRecorderPage_Loaded;
            Unloaded += FpsRecorderPage_Unloaded;
        }

        private void FpsRecorderPage_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
            _monitor.SnapshotChanged += Monitor_SnapshotChanged;
            ApplySnapshot(_monitor.CurrentSnapshot);
            _loaded = true;
        }

        private void FpsRecorderPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _monitor.SnapshotChanged -= Monitor_SnapshotChanged;
            SaveSettings();
            _loaded = false;
        }

        private async void PickGameButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = ReadSettings();
            await _monitor.PickAndStartAsync(App.MainWindow.GetWindowHandle(), settings);

            if (settings.ShowWidget)
                FpsWidgetWindow.ShowSingleton(settings.WidgetOpacity, settings.WidgetTextOnly);
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _monitor.Stop();
            FpsWidgetWindow.CloseSingleton();
        }

        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_monitor.CurrentSnapshot.IsRecording)
                _monitor.StopRecording();
            else if (RecordCsvToggle.IsOn)
                _monitor.StartRecording();
        }

        private void WidgetButton_Click(object sender, RoutedEventArgs e)
        {
            if (FpsWidgetWindow.Current == null)
                FpsWidgetWindow.ShowSingleton((int)WidgetOpacitySlider.Value, WidgetTextOnlyToggle.IsOn);
            else
                FpsWidgetWindow.CloseSingleton();

            UpdateWidgetButton();
        }

        private async void InstallRtssButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                InstallRtssButton.IsEnabled = false;
                InstallRtssProgressRing.IsActive = true;
                InstallRtssProgressRing.Visibility = Visibility.Visible;
                InstallRtssButtonText.Text = "Checking...";
                InstallRtssStatusText.Text = "Checking for an existing RTSS install.";

                if (await IsRtssRunningAsync())
                {
                    InstallRtssStatusText.Text = "RTSS is already running. Start the monitor again.";
                    return;
                }

                InstallRtssButtonText.Text = "Opening...";
                var opened = await Launcher.LaunchUriAsync(RtssDownloadUri);
                InstallRtssStatusText.Text = opened
                    ? "Install RTSS from your browser, then come back and start the monitor again."
                    : "Could not open the RTSS download page.";
            }
            catch
            {
                InstallRtssStatusText.Text = "Could not open RTSS setup information.";
            }
            finally
            {
                InstallRtssProgressRing.IsActive = false;
                InstallRtssProgressRing.Visibility = Visibility.Collapsed;
                InstallRtssButtonText.Text = "Open RTSS download";
                InstallRtssButton.IsEnabled = true;
            }
        }

        private void Settings_Changed(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            ApplyWidgetSettings();
        }

        private void Settings_Changed(object sender, RangeBaseValueChangedEventArgs e)
        {
            SaveSettings();
            ApplyWidgetSettings();
        }

        private void Settings_Changed(object sender, SelectionChangedEventArgs e)
        {
            SaveSettings();
            ApplyWidgetSettings();
        }

        private void Monitor_SnapshotChanged(object? sender, FpsMonitorSnapshot e)
        {
            DispatcherQueue.TryEnqueue(() => ApplySnapshot(e));
        }

        private void ApplySnapshot(FpsMonitorSnapshot snapshot)
        {
            TargetText.Text = snapshot.TargetName;
            HeroStatusText.Text = snapshot.Status;
            HeroFpsText.Text = snapshot.CurrentFps > 0 ? $"{snapshot.CurrentFps:0} FPS" : "-- FPS";
            CurrentFpsText.Text = snapshot.CurrentFps > 0 ? snapshot.CurrentFps.ToString("0") : "--";
            LowFpsText.Text = snapshot.OnePercentLowFps > 0 ? snapshot.OnePercentLowFps.ToString("0") : "--";
            FrameTimeText.Text = snapshot.FrameTimeMs > 0 ? $"{snapshot.FrameTimeMs:0.0} ms" : "-- ms";
            AverageText.Text = snapshot.AverageFps > 0 ? $"Average: {snapshot.AverageFps:0.0} FPS" : "Average: --";
            PointOneLowText.Text = snapshot.PointOnePercentLowFps > 0 ? $"0.1% low: {snapshot.PointOnePercentLowFps:0.0} FPS" : "0.1% low: --";
            FrameCountText.Text = $"Frames: {snapshot.FrameCount:N0}";
            DurationText.Text = snapshot.IsMonitoring ? FormatDuration(snapshot.Duration) : "00:00";
            PreviewFpsText.Text = snapshot.CurrentFps > 0 ? snapshot.CurrentFps.ToString("0") : "--";
            PreviewDetailText.Text = snapshot.FrameTimeMs > 0 ? $"{snapshot.FrameTimeMs:0.0} ms" : "FPS";
            PreviewDetailText.Visibility = WidgetTextOnlyToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
            RecordButtonText.Text = snapshot.IsRecording ? "Stop recording" : "Record FPS";
            RecordingPathText.Text = snapshot.IsRecording && !string.IsNullOrWhiteSpace(snapshot.RecordingPath)
                ? snapshot.RecordingPath
                : "";
            UpdateWidgetButton();
        }

        private FpsMonitorSettings ReadSettings()
        {
            return new FpsMonitorSettings
            {
                TargetFps = ReadTargetFps(),
                IncludeCursor = IncludeCursorToggle.IsOn,
                HideCaptureBorder = HideBorderToggle.IsOn,
                ShowWidget = ShowWidgetToggle.IsOn,
                WidgetTextOnly = WidgetTextOnlyToggle.IsOn,
                WidgetOpacity = (int)Math.Round(WidgetOpacitySlider.Value),
                SampleWindowSeconds = Math.Clamp((int)Math.Round(SampleWindowSlider.Value), 1, 10),
                RecordCsv = RecordCsvToggle.IsOn
            };
        }

        private int ReadTargetFps()
        {
            if (TargetFpsComboBox.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Content?.ToString(), out var fps))
            {
                return fps;
            }

            return 60;
        }

        private void ApplyWidgetSettings()
        {
            if (!_loaded)
                return;

            if (FpsWidgetWindow.Current != null)
            {
                FpsWidgetWindow.Current.SetOpacity((int)Math.Round(WidgetOpacitySlider.Value));
                FpsWidgetWindow.Current.SetTextOnly(WidgetTextOnlyToggle.IsOn);
            }

            UpdateWidgetButton();
        }

        private void UpdateWidgetButton()
        {
            if (WidgetButtonText != null)
                WidgetButtonText.Text = FpsWidgetWindow.Current == null ? "Pop out overlay" : "Hide overlay";
        }

        private void LoadSettings()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                TargetFpsComboBox.SelectedIndex = GetInt(values, "TargetFps", 60) switch
                {
                    30 => 0,
                    45 => 1,
                    90 => 3,
                    120 => 4,
                    144 => 5,
                    _ => 2
                };
                SampleWindowSlider.Value = GetInt(values, "SampleWindowSeconds", 3);
                WidgetOpacitySlider.Value = GetInt(values, "WidgetOpacity", 92);
                RecordCsvToggle.IsOn = GetBool(values, "RecordCsv", true);
                ShowWidgetToggle.IsOn = GetBool(values, "ShowWidget", true);
                WidgetTextOnlyToggle.IsOn = GetBool(values, "WidgetTextOnly", false);
                IncludeCursorToggle.IsOn = GetBool(values, "IncludeCursor", false);
                HideBorderToggle.IsOn = GetBool(values, "HideCaptureBorder", true);
            }
            catch { }
        }

        private void SaveSettings()
        {
            if (!_loaded)
                return;

            try
            {
                var settings = ReadSettings();
                var values = ApplicationData.Current.LocalSettings.Values;
                values[SettingsPrefix + "TargetFps"] = settings.TargetFps;
                values[SettingsPrefix + "SampleWindowSeconds"] = settings.SampleWindowSeconds;
                values[SettingsPrefix + "WidgetOpacity"] = settings.WidgetOpacity;
                values[SettingsPrefix + "RecordCsv"] = settings.RecordCsv;
                values[SettingsPrefix + "ShowWidget"] = settings.ShowWidget;
                values[SettingsPrefix + "WidgetTextOnly"] = settings.WidgetTextOnly;
                values[SettingsPrefix + "IncludeCursor"] = settings.IncludeCursor;
                values[SettingsPrefix + "HideCaptureBorder"] = settings.HideCaptureBorder;
            }
            catch { }
        }

        private static int GetInt(IPropertySet values, string key, int fallback)
        {
            return values.TryGetValue(SettingsPrefix + key, out var value) && value is int number
                ? number
                : fallback;
        }

        private static bool GetBool(IPropertySet values, string key, bool fallback)
        {
            return values.TryGetValue(SettingsPrefix + key, out var value) && value is bool flag
                ? flag
                : fallback;
        }

        private static async Task<bool> IsRtssRunningAsync()
        {
            for (var i = 0; i < 2; i++)
            {
                try
                {
                    using var mappedFile = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.Read);
                    return true;
                }
                catch
                {
                    await Task.Delay(100);
                }
            }

            return false;
        }

        private static async Task WaitForRtssSharedMemoryAsync()
        {
            for (var i = 0; i < 40; i++)
            {
                try
                {
                    using var mappedFile = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.Read);
                    return;
                }
                catch
                {
                    await Task.Delay(250);
                }
            }
        }

        private static void DismissRtssUpdatePrompt()
        {
            Task.Run(async () =>
            {
                for (var i = 0; i < 40; i++)
                {
                    try
                    {
                        EnumWindows((hwnd, _) =>
                        {
                            var title = GetWindowText(hwnd);
                            if (title.Contains("RivaTunerStatisticsServer", StringComparison.OrdinalIgnoreCase))
                            {
                                var body = GetDialogText(hwnd);
                                if (body.Contains("available for download", StringComparison.OrdinalIgnoreCase))
                                {
                                    SendMessage(hwnd, WM_COMMAND, IDCANCEL, 0);
                                    return false;
                                }
                            }

                            return true;
                        }, IntPtr.Zero);
                    }
                    catch
                    {
                    }

                    await Task.Delay(250);
                }
            });
        }

        private static string FormatDuration(TimeSpan duration)
        {
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
                : $"{duration.Minutes:00}:{duration.Seconds:00}";
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        private const int WM_COMMAND = 0x0111;
        private const int IDCANCEL = 2;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int message, int wParam, int lParam);

        private static string GetWindowText(IntPtr hwnd)
        {
            var length = GetWindowTextLength(hwnd);
            if (length <= 0)
                return string.Empty;

            var builder = new System.Text.StringBuilder(length + 1);
            GetWindowText(hwnd, builder, builder.Capacity);
            return builder.ToString();
        }

        private static string GetDialogText(IntPtr hwnd)
        {
            var text = string.Empty;
            EnumChildWindows(hwnd, (child, _) =>
            {
                text += " " + GetWindowText(child);
                return true;
            }, IntPtr.Zero);
            return text;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc enumProc, IntPtr lParam);
    }
}
