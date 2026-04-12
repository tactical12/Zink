using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Zink.Models;
using Zink.Services.Recording;

namespace Zink.Pages
{
    public sealed partial class RecorderPage : Page
    {
        private readonly RecordingManager _manager = RecordingManager.Instance;

        public RecorderPage()
        {
            InitializeComponent();
            Loaded += RecorderPage_Loaded;
            Unloaded += RecorderPage_Unloaded;
        }

        private async void RecorderPage_Loaded(object sender, RoutedEventArgs e)
        {
            _manager.StatusChanged += Manager_StatusChanged;
            _manager.CaptureTargetChanged += Manager_CaptureTargetChanged;
            _manager.ManualRecordingStateChanged += Manager_ManualRecordingStateChanged;
            _manager.BackgroundRecordingStateChanged += Manager_BackgroundRecordingStateChanged;

            StatusText.Text = _manager.LastStatus;
            TargetText.Text = _manager.LastCaptureTargetText;
            ManualStateText.Text = $"Manual recording: {(_manager.IsManualRecording ? "Running" : "Stopped")}";
            BackgroundStateText.Text = $"Background clipping: {(_manager.IsBackgroundClipping ? "Running" : "Stopped")}";

            var renderDevices = await AudioDeviceService.GetRenderDevicesAsync();
            RenderDevicesComboBox.ItemsSource = renderDevices;
            RenderDevicesComboBox.SelectedItem = renderDevices.FirstOrDefault();

            var micDevices = await AudioDeviceService.GetCaptureDevicesAsync();
            MicDevicesComboBox.ItemsSource = micDevices;
            MicDevicesComboBox.SelectedItem = micDevices.FirstOrDefault();
        }

        private void RecorderPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _manager.StatusChanged -= Manager_StatusChanged;
            _manager.CaptureTargetChanged -= Manager_CaptureTargetChanged;
            _manager.ManualRecordingStateChanged -= Manager_ManualRecordingStateChanged;
            _manager.BackgroundRecordingStateChanged -= Manager_BackgroundRecordingStateChanged;
        }

        private async void PickTargetButton_Click(object sender, RoutedEventArgs e)
        {
            await _manager.PickCaptureTargetAsync(
                App.MainWindow.GetWindowHandle(),
                IncludeSystemAudioToggle.IsOn,
                IncludeMicrophoneToggle.IsOn,
                (RenderDevicesComboBox.SelectedItem as RecorderDeviceItem)?.Id,
                (MicDevicesComboBox.SelectedItem as RecorderDeviceItem)?.Id);
        }

        private async void StartManualRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            await _manager.StartManualRecordingAsync(
                IncludeSystemAudioToggle.IsOn,
                IncludeMicrophoneToggle.IsOn,
                (RenderDevicesComboBox.SelectedItem as RecorderDeviceItem)?.Id,
                (MicDevicesComboBox.SelectedItem as RecorderDeviceItem)?.Id);
        }

        private async void StopManualRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            await _manager.StopManualRecordingAsync();
        }

        private async void StartBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
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
            await _manager.SaveLast45SecondsAsync();
        }

        private async void RunAtStartupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            await StartupService.SetStartupEnabledAsync(RunAtStartupToggle.IsOn);
        }

        private async void Manager_StatusChanged(object? sender, string e)
        {
            await DispatcherQueue.EnqueueAsync(() => StatusText.Text = e);
        }

        private async void Manager_CaptureTargetChanged(object? sender, string e)
        {
            await DispatcherQueue.EnqueueAsync(() => TargetText.Text = e);
        }

        private async void Manager_ManualRecordingStateChanged(object? sender, bool isRunning)
        {
            await DispatcherQueue.EnqueueAsync(() =>
                ManualStateText.Text = $"Manual recording: {(isRunning ? "Running" : "Stopped")}");
        }

        private async void Manager_BackgroundRecordingStateChanged(object? sender, bool isRunning)
        {
            await DispatcherQueue.EnqueueAsync(() =>
                BackgroundStateText.Text = $"Background clipping: {(isRunning ? "Running" : "Stopped")}");
        }
    }
}