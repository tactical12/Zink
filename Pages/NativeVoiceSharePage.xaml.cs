using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Zink.Models;
using Zink.Services;

namespace Zink.Pages
{
    public sealed partial class NativeVoiceSharePage : Page
    {
        private NativeRtcCoordinator? _rtc;
        private readonly DispatcherQueue _dispatcher;

        public NativeVoiceSharePage()
        {
            this.InitializeComponent();
            _dispatcher = DispatcherQueue.GetForCurrentThread();
        }

        private async void JoinButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureCoordinator();

                await _rtc!.ConnectSignalingAsync(
                    ServerUrlBox.Text.Trim(),
                    RoomIdBox.Text.Trim(),
                    UserIdBox.Text.Trim());

                SetStatus("Joined signaling room.");
            }
            catch (Exception ex)
            {
                SetStatus("Join failed: " + ex.Message);
            }
        }

        private async void StartCallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureCoordinator();
                await _rtc!.StartOutgoingCallAsync();
            }
            catch (Exception ex)
            {
                SetStatus("Start call failed: " + ex.Message);
            }
        }

        private async void AnswerButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureCoordinator();
                await _rtc!.AnswerIncomingCallAsync();
            }
            catch (Exception ex)
            {
                SetStatus("Answer failed: " + ex.Message);
            }
        }

        private async void HangupButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_rtc != null)
                {
                    await _rtc.HangUpAsync();
                }
            }
            catch (Exception ex)
            {
                SetStatus("Hang up failed: " + ex.Message);
            }
        }

        private async void StartShareButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureCoordinator();

                var require4k = Require4kCheckBox.IsChecked == true;
                var stats = await _rtc!.StartScreenShareAsync(require4k);
                ShareStatsText.Text = stats.ToString();
            }
            catch (Exception ex)
            {
                SetStatus("Start share failed: " + ex.Message);
            }
        }

        private async void StopShareButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_rtc != null)
                {
                    await _rtc.StopScreenShareAsync();
                    ShareStatsText.Text = "No share active.";
                }
            }
            catch (Exception ex)
            {
                SetStatus("Stop share failed: " + ex.Message);
            }
        }

        private async void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_rtc != null)
                {
                    await _rtc.ToggleMuteAsync();
                }
            }
            catch (Exception ex)
            {
                SetStatus("Mute failed: " + ex.Message);
            }
        }

        private void EnsureCoordinator()
        {
            if (_rtc != null)
                return;

            _rtc = new NativeRtcCoordinator();

            _rtc.StatusChanged += message =>
            {
                _dispatcher.TryEnqueue(() => SetStatus(message));
            };

            _rtc.CallStateChanged += state =>
            {
                _dispatcher.TryEnqueue(() => CallStateText.Text = state.ToString());
            };

            _rtc.RemoteInfoChanged += info =>
            {
                _dispatcher.TryEnqueue(() => RemoteInfoText.Text = info);
            };

            _rtc.ShareStatsChanged += stats =>
            {
                _dispatcher.TryEnqueue(() => ShareStatsText.Text = stats.ToString());
            };
        }

        private void SetStatus(string message)
        {
            StatusText.Text = message;
        }
    }
}