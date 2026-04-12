using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using Zink.Services.NativeCalling;
using Zink.Services.Social;

namespace Zink.Pages.Social
{
    public sealed partial class CallPage : Page
    {
        private long _targetUserId;
        private bool _isScreenShare;

        private bool _isMicEnabled = true;
        private bool _isMuted = false;
        private bool _isDeafened = false;
        private bool _isSharingScreen = false;
        private string _selectedAudioOutput = "Default";

        private DispatcherTimer? _callTimer;
        private DateTimeOffset? _connectedAtUtc;

        public CallPage()
        {
            this.InitializeComponent();
            Loaded += CallPage_Loaded;
            Unloaded += CallPage_Unloaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is CallPageArgs args)
            {
                _targetUserId = args.TargetUserId;
                _isScreenShare = args.IsScreenShare;
            }

            _isSharingScreen = _isScreenShare;

            AvatarInitialText.Text = _isScreenShare ? "S" : "V";
            ModeText.Text = _isScreenShare ? "Mode: 4K Screen Share + Voice" : "Mode: Voice Call";
            TargetUserText.Text = $"Target user: {_targetUserId}";
            RemoteUserText.Text = "Remote user: -";
            PeerText.Text = _targetUserId > 0 ? $"Preparing call with user {_targetUserId}" : "Preparing call...";

            if (CallLaunchState.TryConsumeOutgoing(_targetUserId, _isScreenShare, out var consumedCallId))
            {
                NativeCallCoordinator.Instance.CurrentSession.CallId = consumedCallId;
                NativeCallCoordinator.Instance.CurrentSession.TargetUserId = _targetUserId;
                NativeCallCoordinator.Instance.CurrentSession.RemoteUserId = _targetUserId;
                NativeCallCoordinator.Instance.CurrentSession.IsScreenShare = _isScreenShare;
                NativeCallCoordinator.Instance.CurrentSession.State = NativeCallState.Calling;
                NativeCallCoordinator.Instance.CurrentSession.StatusText = "Calling...";
                NativeCallCoordinator.Instance.CurrentSession.PeerText = $"Calling user {_targetUserId}";
            }

            ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
            UpdateDockVisualStates();
            UpdateSpeakingIndicators(NativeCallCoordinator.Instance.CurrentSession, AudioActivityService.Instance.Current);
        }

        private void CallPage_Loaded(object sender, RoutedEventArgs e)
        {
            NativeSignalingBridge.Instance.EnsureHooked();

            NativeCallCoordinator.Instance.SessionChanged += NativeCallCoordinator_SessionChanged;
            NativeSignalingBridge.Instance.IncomingCallReceived += NativeSignalingBridge_IncomingCallReceived;
            NativeSignalingBridge.Instance.CallAnsweredReceived += NativeSignalingBridge_CallAnsweredReceived;
            NativeSignalingBridge.Instance.CallRejectedReceived += NativeSignalingBridge_CallRejectedReceived;
            NativeSignalingBridge.Instance.CallEndedReceived += NativeSignalingBridge_CallEndedReceived;

            AudioActivityService.Instance.ActivityChanged += AudioActivityService_ActivityChanged;

            if (string.IsNullOrWhiteSpace(TokenStateText.Text) || TokenStateText.Text == "Token: not loaded")
                TokenStateText.Text = "Token: loaded";

            RealtimeStateText.Text = "Realtime: connected";

            EnsureTimerCreated();
            UpdateCallTimerText();
        }

        private void CallPage_Unloaded(object sender, RoutedEventArgs e)
        {
            NativeCallCoordinator.Instance.SessionChanged -= NativeCallCoordinator_SessionChanged;
            NativeSignalingBridge.Instance.IncomingCallReceived -= NativeSignalingBridge_IncomingCallReceived;
            NativeSignalingBridge.Instance.CallAnsweredReceived -= NativeSignalingBridge_CallAnsweredReceived;
            NativeSignalingBridge.Instance.CallRejectedReceived -= NativeSignalingBridge_CallRejectedReceived;
            NativeSignalingBridge.Instance.CallEndedReceived -= NativeSignalingBridge_CallEndedReceived;

            AudioActivityService.Instance.ActivityChanged -= AudioActivityService_ActivityChanged;

            StopCallTimer();
        }

        private void AudioActivityService_ActivityChanged(object? sender, AudioActivityState e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateSpeakingIndicators(NativeCallCoordinator.Instance.CurrentSession, e);
            });
        }

        private void EnsureTimerCreated()
        {
            if (_callTimer != null)
                return;

            _callTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _callTimer.Tick += CallTimer_Tick;
        }

        private void CallTimer_Tick(object? sender, object e)
        {
            UpdateCallTimerText();
        }

        private void StartCallTimerIfNeeded()
        {
            EnsureTimerCreated();

            if (_connectedAtUtc == null)
                _connectedAtUtc = DateTimeOffset.UtcNow;

            if (_callTimer != null && !_callTimer.IsEnabled)
                _callTimer.Start();

            UpdateCallTimerText();
        }

        private void StopCallTimer()
        {
            if (_callTimer != null && _callTimer.IsEnabled)
                _callTimer.Stop();
        }

        private void ResetCallTimer()
        {
            StopCallTimer();
            _connectedAtUtc = null;
            UpdateCallTimerText();
        }

        private void UpdateCallTimerText()
        {
            if (_connectedAtUtc == null)
            {
                CallTimerText.Text = "Duration: 00:00";
                return;
            }

            var elapsed = DateTimeOffset.UtcNow - _connectedAtUtc.Value;
            var totalHours = (int)elapsed.TotalHours;

            CallTimerText.Text = totalHours > 0
                ? $"Duration: {totalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"Duration: {elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }

        private void NativeCallCoordinator_SessionChanged(object? sender, NativeCallSession e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ApplySessionToUi(e);
            });
        }

        private void NativeSignalingBridge_IncomingCallReceived(object? sender, IncomingCallEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Keep this only for enabling receiver UI when landing on the page.
                if (NativeCallCoordinator.Instance.CurrentSession.State == NativeCallState.Incoming ||
                    string.IsNullOrWhiteSpace(NativeCallCoordinator.Instance.CurrentSession.CallId))
                {
                    NativeCallCoordinator.Instance.SetIncoming(
                        e.CallId,
                        e.FromUserId,
                        string.IsNullOrWhiteSpace(e.FromDisplayName) ? e.FromUsername : e.FromDisplayName,
                        false);
                }

                AcceptButton.IsEnabled = true;
                RejectButton.IsEnabled = true;

                ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
            });
        }

        private void NativeSignalingBridge_CallAnsweredReceived(object? sender, (string CallId, long FromUserId) e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Do NOT overwrite coordinator status here.
                // Coordinator now owns connected/audio-start/send/receive status.
                ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
            });
        }

        private void NativeSignalingBridge_CallRejectedReceived(object? sender, (string CallId, long FromUserId) e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Do NOT overwrite coordinator status here.
                ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
            });
        }

        private void NativeSignalingBridge_CallEndedReceived(object? sender, (string CallId, long FromUserId) e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Do NOT overwrite coordinator status here.
                ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
            });
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await NativeCallCoordinator.Instance.StartOutgoingAsync(_targetUserId, _isScreenShare);
            }
            catch (Exception ex)
            {
                NativeCallCoordinator.Instance.SetStatus(NativeCallState.Failed, ex.Message);
            }
        }

        private async void AcceptButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (session.RemoteUserId <= 0 || string.IsNullOrWhiteSpace(session.CallId))
                    return;

                await NativeCallCoordinator.Instance.AcceptIncomingAsync(session.RemoteUserId, session.CallId);

                AcceptButton.IsEnabled = false;
                RejectButton.IsEnabled = false;
            }
            catch (Exception ex)
            {
                NativeCallCoordinator.Instance.SetStatus(NativeCallState.Failed, ex.Message);
            }
        }

        private async void RejectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var session = NativeCallCoordinator.Instance.CurrentSession;
                if (session.RemoteUserId <= 0 || string.IsNullOrWhiteSpace(session.CallId))
                    return;

                await NativeCallCoordinator.Instance.RejectIncomingAsync(session.RemoteUserId, session.CallId);

                AcceptButton.IsEnabled = false;
                RejectButton.IsEnabled = false;
            }
            catch (Exception ex)
            {
                NativeCallCoordinator.Instance.SetStatus(NativeCallState.Failed, ex.Message);
            }
        }

        private async void EndButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await NativeCallCoordinator.Instance.EndAsync();
                AudioActivityService.Instance.Reset();
            }
            catch (Exception ex)
            {
                NativeCallCoordinator.Instance.SetStatus(NativeCallState.Failed, ex.Message);
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private void MicToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isMicEnabled = !_isMicEnabled;

            if (!_isMicEnabled)
                _isMuted = true;

            if (!_isMicEnabled)
                AudioActivityService.Instance.UpdateLocalLevel(0);

            NativeCallCoordinator.Instance.SetStatus(
                NativeCallCoordinator.Instance.CurrentSession.State,
                _isMicEnabled ? "Microphone enabled." : "Microphone disabled.");

            UpdateDockVisualStates();
            UpdateSpeakingIndicators(NativeCallCoordinator.Instance.CurrentSession, AudioActivityService.Instance.Current);
        }

        private void HeadphonesButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();

            AddAudioOutputMenuItem(flyout, "Default");
            AddAudioOutputMenuItem(flyout, "Headphones");
            AddAudioOutputMenuItem(flyout, "Speakers");

            flyout.ShowAt(HeadphonesButton);
        }

        private void AddAudioOutputMenuItem(MenuFlyout flyout, string name)
        {
            var item = new MenuFlyoutItem
            {
                Text = name
            };

            item.Click += (_, __) =>
            {
                _selectedAudioOutput = name;

                NativeCallCoordinator.Instance.SetStatus(
                    NativeCallCoordinator.Instance.CurrentSession.State,
                    $"Audio output set to {_selectedAudioOutput}.");
            };

            flyout.Items.Add(item);
        }

        private void MuteToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isMuted = !_isMuted;
            _isMicEnabled = !_isMuted;

            if (_isMuted)
                AudioActivityService.Instance.UpdateLocalLevel(0);

            NativeCallCoordinator.Instance.SetStatus(
                NativeCallCoordinator.Instance.CurrentSession.State,
                _isMuted ? "Microphone muted." : "Microphone unmuted.");

            UpdateDockVisualStates();
            UpdateSpeakingIndicators(NativeCallCoordinator.Instance.CurrentSession, AudioActivityService.Instance.Current);
        }

        private void DeafenToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isDeafened = !_isDeafened;

            if (_isDeafened)
                AudioActivityService.Instance.UpdateRemoteLevel(0);

            NativeCallCoordinator.Instance.SetStatus(
                NativeCallCoordinator.Instance.CurrentSession.State,
                _isDeafened ? "Incoming audio deafened." : "Incoming audio restored.");

            UpdateDockVisualStates();
            UpdateSpeakingIndicators(NativeCallCoordinator.Instance.CurrentSession, AudioActivityService.Instance.Current);
        }

        private void ScreenShareToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isSharingScreen = !_isSharingScreen;
            _isScreenShare = _isSharingScreen;
            NativeCallCoordinator.Instance.CurrentSession.IsScreenShare = _isSharingScreen;

            ModeText.Text = _isSharingScreen ? "Mode: 4K Screen Share + Voice" : "Mode: Voice Call";

            NativeCallCoordinator.Instance.SetStatus(
                NativeCallCoordinator.Instance.CurrentSession.State,
                _isSharingScreen ? "Screen share enabled." : "Screen share disabled.");

            UpdateDockVisualStates();
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            FlyoutBase.ShowAttachedFlyout((FrameworkElement)sender);
        }

        private void MoreBack_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private void MoreReconnect_Click(object sender, RoutedEventArgs e)
        {
            NativeCallCoordinator.Instance.SetStatus(
                NativeCallCoordinator.Instance.CurrentSession.State,
                "Reconnect requested.");
        }

        private void MoreReset_Click(object sender, RoutedEventArgs e)
        {
            NativeCallCoordinator.Instance.Reset();
            _isMicEnabled = true;
            _isMuted = false;
            _isDeafened = false;
            _isSharingScreen = false;
            ResetCallTimer();
            AudioActivityService.Instance.Reset();
            UpdateDockVisualStates();
            ApplySessionToUi(NativeCallCoordinator.Instance.CurrentSession);
        }

        private void ApplySessionToUi(NativeCallSession session)
        {
            CallIdText.Text = $"Call ID: {(string.IsNullOrWhiteSpace(session.CallId) ? "-" : session.CallId)}";
            TargetUserText.Text = $"Target user: {(session.TargetUserId > 0 ? session.TargetUserId.ToString() : "-")}";
            RemoteUserText.Text = $"Remote user: {(session.RemoteUserId > 0 ? session.RemoteUserId.ToString() : "-")}";
            PeerText.Text = string.IsNullOrWhiteSpace(session.PeerText) ? "Preparing call..." : session.PeerText;
            StatusText.Text = string.IsNullOrWhiteSpace(session.StatusText) ? "Waiting..." : session.StatusText;
            MediaOverlayText.Text = session.State.ToString();

            if (session.State == NativeCallState.Connected)
            {
                StartCallTimerIfNeeded();
            }
            else if (session.State == NativeCallState.Ended ||
                     session.State == NativeCallState.Rejected ||
                     session.State == NativeCallState.Failed ||
                     session.State == NativeCallState.Idle)
            {
                ResetCallTimer();
            }

            UpdateMembersPanel(session);
            UpdateSpeakingIndicators(session, AudioActivityService.Instance.Current);
            SetConnectionState(session.State);
        }

        private void UpdateMembersPanel(NativeCallSession session)
        {
            RemoteMemberTitleText.Text = session.RemoteUserId > 0 ? $"User {session.RemoteUserId}" : "Remote user";
            RemoteAvatarText.Text = session.RemoteUserId > 0 ? session.RemoteUserId.ToString()[0].ToString() : "R";

            MembersSummaryText.Text = session.RemoteUserId > 0 ? "In call - 2" : "In call - 1";

            YouMemberStateText.Text = session.State switch
            {
                NativeCallState.Calling => "Calling",
                NativeCallState.Incoming => "Ringing",
                NativeCallState.Accepted => "Accepted",
                NativeCallState.Connected => "Connected",
                NativeCallState.Ended => "Ended",
                NativeCallState.Rejected => "Rejected",
                NativeCallState.Failed => "Failed",
                _ => "Ready"
            };

            RemoteMemberStateText.Text = session.State switch
            {
                NativeCallState.Calling => "Ringing",
                NativeCallState.Incoming => "Calling you",
                NativeCallState.Accepted => "Joining",
                NativeCallState.Connected => "Connected",
                NativeCallState.Ended => "Left call",
                NativeCallState.Rejected => "Declined",
                NativeCallState.Failed => "Unavailable",
                _ => session.RemoteUserId > 0 ? "Waiting" : "Not connected"
            };
        }

        private void UpdateSpeakingIndicators(NativeCallSession session, AudioActivityState activity)
        {
            var youSpeaking = activity.LocalSpeaking && _isMicEnabled && !_isMuted && session.State == NativeCallState.Connected;
            var remoteSpeaking = activity.RemoteSpeaking && !_isDeafened && session.State == NativeCallState.Connected;

            YouSpeakingBadgeText.Text = youSpeaking ? $"Speaking {activity.LocalLevel:P0}" : (_isMuted ? "Muted" : "Idle");
            RemoteSpeakingBadgeText.Text = remoteSpeaking ? $"Active {activity.RemoteLevel:P0}" : "Idle";

            YouSpeakingBadge.Background = new SolidColorBrush(
                youSpeaking
                    ? ColorHelper.FromArgb(255, 36, 88, 62)
                    : (_isMuted
                        ? ColorHelper.FromArgb(255, 95, 40, 40)
                        : ColorHelper.FromArgb(255, 45, 49, 56)));

            RemoteSpeakingBadge.Background = new SolidColorBrush(
                remoteSpeaking
                    ? ColorHelper.FromArgb(255, 36, 88, 62)
                    : ColorHelper.FromArgb(255, 45, 49, 56));

            YouMemberCard.BorderThickness = new Thickness(1);
            RemoteMemberCard.BorderThickness = new Thickness(1);

            YouMemberCard.BorderBrush = new SolidColorBrush(
                youSpeaking
                    ? ColorHelper.FromArgb(255, 60, 140, 90)
                    : ColorHelper.FromArgb(255, 38, 255, 255));

            RemoteMemberCard.BorderBrush = new SolidColorBrush(
                remoteSpeaking
                    ? ColorHelper.FromArgb(255, 60, 140, 90)
                    : ColorHelper.FromArgb(255, 38, 255, 255));

            if (session.State == NativeCallState.Connected)
            {
                if (youSpeaking && remoteSpeaking)
                {
                    PeerText.Text = $"Connected with user {session.RemoteUserId} - both speaking";
                }
                else if (youSpeaking)
                {
                    PeerText.Text = $"Connected with user {session.RemoteUserId} - you are speaking";
                }
                else if (remoteSpeaking)
                {
                    PeerText.Text = $"Connected with user {session.RemoteUserId} - remote is speaking";
                }
            }
        }

        private void SetConnectionState(NativeCallState state)
        {
            ConnectionBadgeText.Text = state.ToString();

            switch (state)
            {
                case NativeCallState.Ready:
                case NativeCallState.Connected:
                    ConnectionBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 36, 64, 36));
                    break;

                case NativeCallState.Calling:
                case NativeCallState.Incoming:
                case NativeCallState.Accepted:
                case NativeCallState.Negotiating:
                    ConnectionBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 72, 56, 24));
                    break;

                case NativeCallState.Rejected:
                case NativeCallState.Ended:
                case NativeCallState.Failed:
                    ConnectionBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 80, 28, 28));
                    break;

                default:
                    ConnectionBadge.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 46, 46, 46));
                    break;
            }
        }

        private void UpdateDockVisualStates()
        {
            MicToggleButton.Background = _isMicEnabled
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 95, 40, 40));

            MuteToggleButton.Background = _isMuted
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 95, 40, 40))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));

            DeafenToggleButton.Background = _isDeafened
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 95, 40, 40))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));

            ScreenShareToggleButton.Background = _isSharingScreen
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 36, 88, 62))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));

            HeadphonesButton.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));
            MoreButton.Background = new SolidColorBrush(ColorHelper.FromArgb(255, 23, 26, 32));

            MuteIcon.Glyph = _isMuted ? "\uE74F" : "\uE720";
        }
    }
}