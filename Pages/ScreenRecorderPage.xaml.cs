using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zink.Models;
using Zink.Services.Recording;

namespace Zink.Pages
{
    public sealed partial class ScreenRecorderPage : Page
    {
        private readonly RecordingManager _manager = RecordingManager.Instance;

        public ScreenRecorderPage()
        {
            InitializeComponent();
            Loaded += ScreenRecorderPage_Loaded;
            Unloaded += ScreenRecorderPage_Unloaded;
        }

        private async void ScreenRecorderPage_Loaded(object sender, RoutedEventArgs e)
        {
            _manager.StatusChanged += Manager_StatusChanged;
            _manager.CaptureTargetChanged += Manager_CaptureTargetChanged;
            _manager.ManualRecordingStateChanged += Manager_ManualRecordingStateChanged;
            _manager.BackgroundRecordingStateChanged += Manager_BackgroundRecordingStateChanged;

            StatusText.Text = _manager.LastStatus;
            TargetText.Text = _manager.LastCaptureTargetText;
            ManualStateText.Text = $"Manual recording: {(_manager.IsManualRecording ? "Running" : "Stopped")}";
            BackgroundStateText.Text = $"Background clipping: {(_manager.IsBackgroundClipping ? "Running" : "Stopped")}";
            UpdateLiveRecordingIndicator();
            UpdateBackgroundReplayControls();

            var renderDevices = await AudioDeviceService.GetRenderDevicesAsync();
            RenderDevicesComboBox.ItemsSource = renderDevices;
            RenderDevicesComboBox.SelectedItem = renderDevices.FirstOrDefault();

            var micDevices = await AudioDeviceService.GetCaptureDevicesAsync();
            MicDevicesComboBox.ItemsSource = micDevices;
            MicDevicesComboBox.SelectedItem = micDevices.FirstOrDefault();
        }

        private void ScreenRecorderPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _manager.StatusChanged -= Manager_StatusChanged;
            _manager.CaptureTargetChanged -= Manager_CaptureTargetChanged;
            _manager.ManualRecordingStateChanged -= Manager_ManualRecordingStateChanged;
            _manager.BackgroundRecordingStateChanged -= Manager_BackgroundRecordingStateChanged;
        }

        private async void PickSourceButton_Click(object sender, RoutedEventArgs e)
        {
            await _manager.PickCaptureTargetAsync(
                App.MainWindow.GetWindowHandle(),
                IncludeSystemAudioToggle.IsOn,
                IncludeMicrophoneToggle.IsOn,
                (RenderDevicesComboBox.SelectedItem as RecorderDeviceItem)?.Id,
                (MicDevicesComboBox.SelectedItem as RecorderDeviceItem)?.Id);
        }

        private async void StartRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            await _manager.StartManualRecordingAsync(
                IncludeSystemAudioToggle.IsOn,
                IncludeMicrophoneToggle.IsOn,
                (RenderDevicesComboBox.SelectedItem as RecorderDeviceItem)?.Id,
                (MicDevicesComboBox.SelectedItem as RecorderDeviceItem)?.Id);
        }

        private async void StopRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            await _manager.StopManualRecordingAsync();
        }

        private async void StartBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            if (!RecordingPreferences.IsGamingBackgroundReplayEnabled)
            {
                StatusText.Text = "Background replay for gaming is turned off in Settings.";
                UpdateBackgroundReplayControls();
                return;
            }

            await _manager.StartBackgroundClippingAsync(
                IncludeSystemAudioToggle.IsOn,
                IncludeMicrophoneToggle.IsOn,
                (RenderDevicesComboBox.SelectedItem as RecorderDeviceItem)?.Id,
                (MicDevicesComboBox.SelectedItem as RecorderDeviceItem)?.Id);
        }

        private async void StopBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            await _manager.StopBackgroundClippingAsync();
        }

        private async void SaveLast45SecondsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!RecordingPreferences.IsGamingBackgroundReplayEnabled)
            {
                StatusText.Text = "Background replay for gaming is turned off in Settings.";
                UpdateBackgroundReplayControls();
                return;
            }

            await _manager.SaveLast45SecondsAsync();
        }

        private async void RunAtStartupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            await StartupService.SetStartupEnabledAsync(RunAtStartupToggle.IsOn);
        }

        private async void Manager_StatusChanged(object? sender, string e)
        {
            await DispatcherQueue.EnqueueAsync(() =>
            {
                StatusText.Text = e;
            });
        }

        private async void Manager_CaptureTargetChanged(object? sender, string e)
        {
            await DispatcherQueue.EnqueueAsync(() =>
            {
                TargetText.Text = e;
            });
        }

        private async void Manager_ManualRecordingStateChanged(object? sender, bool isRunning)
        {
            await DispatcherQueue.EnqueueAsync(() =>
            {
                ManualStateText.Text = $"Manual recording: {(isRunning ? "Running" : "Stopped")}";
                UpdateLiveRecordingIndicator();
            });
        }

        private async void Manager_BackgroundRecordingStateChanged(object? sender, bool isRunning)
        {
            await DispatcherQueue.EnqueueAsync(() =>
            {
                BackgroundStateText.Text = $"Background clipping: {(isRunning ? "Running" : "Stopped")}";
                UpdateLiveRecordingIndicator();
                UpdateBackgroundReplayControls();
            });
        }

        private void UpdateBackgroundReplayControls()
        {
            bool replayEnabled = RecordingPreferences.IsGamingBackgroundReplayEnabled;
            StartBackgroundButton.IsEnabled = replayEnabled;
            SaveLast45SecondsButton.IsEnabled = replayEnabled;

            if (!replayEnabled)
            {
                BackgroundStateText.Text = "Background clipping: Off in Settings";
            }
        }

        private void UpdateLiveRecordingIndicator()
        {
            LiveRecordingPill.Visibility = _manager.IsManualRecording
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }
}
