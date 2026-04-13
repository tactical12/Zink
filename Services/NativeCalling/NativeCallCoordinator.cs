using System;
using System.Threading.Tasks;
using Zink.Services.Social;

namespace Zink.Services.NativeCalling
{
    public sealed class NativeCallCoordinator
    {
        public static NativeCallCoordinator Instance { get; } = new NativeCallCoordinator();

        public NativeCallSession CurrentSession { get; } = new NativeCallSession();

        public event EventHandler<NativeCallSession>? SessionChanged;

        private bool _audioHooked;
        private bool _realtimeHooked;

        private int _sentAudioChunks;
        private int _receivedAudioChunks;
        private bool _reportedSending;
        private bool _reportedReceiving;

        private NativeCallCoordinator()
        {
            EnsureRealtimeHooks();
        }

        public void Reset()
        {
            UnhookAudioCapture();
            AudioActivityService.Instance.Reset();
            AudioPlaybackService.Instance.Stop();
            _ = MicCaptureService.Instance.StopAsync();

            _sentAudioChunks = 0;
            _receivedAudioChunks = 0;
            _reportedSending = false;
            _reportedReceiving = false;

            CurrentSession.CallId = "";
            CurrentSession.TargetUserId = 0;
            CurrentSession.RemoteUserId = 0;
            CurrentSession.IsScreenShare = false;
            CurrentSession.State = NativeCallState.Idle;
            CurrentSession.StatusText = "Waiting...";
            CurrentSession.PeerText = "Preparing call...";
            CurrentSession.CreatedAtUtc = DateTimeOffset.UtcNow;

            RaiseChanged();
        }

        public void SetStatus(NativeCallState state, string status, string? peerText = null)
        {
            CurrentSession.State = state;
            CurrentSession.StatusText = status;

            if (!string.IsNullOrWhiteSpace(peerText))
                CurrentSession.PeerText = peerText!;

            RaiseChanged();
        }

        public void SetOutgoing(string callId, long targetUserId, bool isScreenShare)
        {
            _sentAudioChunks = 0;
            _receivedAudioChunks = 0;
            _reportedSending = false;
            _reportedReceiving = false;

            CurrentSession.CallId = callId;
            CurrentSession.TargetUserId = targetUserId;
            CurrentSession.RemoteUserId = targetUserId;
            CurrentSession.IsScreenShare = isScreenShare;
            CurrentSession.State = NativeCallState.Calling;
            CurrentSession.StatusText = "Calling...";
            CurrentSession.PeerText = $"Calling user {targetUserId}";
            CurrentSession.CreatedAtUtc = DateTimeOffset.UtcNow;

            RaiseChanged();
        }

        public void SetIncoming(string callId, long fromUserId, string displayName, bool isScreenShare)
        {
            _sentAudioChunks = 0;
            _receivedAudioChunks = 0;
            _reportedSending = false;
            _reportedReceiving = false;

            CurrentSession.CallId = callId;
            CurrentSession.TargetUserId = fromUserId;
            CurrentSession.RemoteUserId = fromUserId;
            CurrentSession.IsScreenShare = isScreenShare;
            CurrentSession.State = NativeCallState.Incoming;
            CurrentSession.StatusText = $"Incoming call from {displayName}.";
            CurrentSession.PeerText = $"Incoming call from {displayName}";
            CurrentSession.CreatedAtUtc = DateTimeOffset.UtcNow;

            RaiseChanged();
        }

        public async Task StartOutgoingAsync(long targetUserId, bool isScreenShare)
        {
            EnsureRealtimeHooks();

            var callId = Guid.NewGuid().ToString();

            await SocialManager.Instance.Realtime.CallUserAsync(targetUserId, callId);

            CallLaunchState.SetOutgoing(callId, targetUserId, isScreenShare);
            SetOutgoing(callId, targetUserId, isScreenShare);
        }

        public async Task AcceptIncomingAsync(long remoteUserId, string callId)
        {
            EnsureRealtimeHooks();

            await SocialManager.Instance.Realtime.AcceptCallAsync(remoteUserId, callId);

            CurrentSession.CallId = callId;
            CurrentSession.RemoteUserId = remoteUserId;
            CurrentSession.TargetUserId = remoteUserId;
            CurrentSession.State = NativeCallState.Connected;
            CurrentSession.StatusText = "Connected.";
            CurrentSession.PeerText = $"Connected with user {remoteUserId}";
            CurrentSession.CreatedAtUtc = DateTimeOffset.UtcNow;

            RaiseChanged();

            await TryStartLocalAudioAsync();
        }

        public async Task RejectIncomingAsync(long remoteUserId, string callId)
        {
            await SocialManager.Instance.Realtime.RejectCallAsync(remoteUserId, callId);

            CurrentSession.CallId = callId;
            CurrentSession.RemoteUserId = remoteUserId;
            CurrentSession.TargetUserId = remoteUserId;
            CurrentSession.State = NativeCallState.Rejected;
            CurrentSession.StatusText = "Rejected call.";

            RaiseChanged();
        }

        public async Task OnCallAnsweredAsync(string callId, long fromUserId)
        {
            EnsureRealtimeHooks();

            CurrentSession.CallId = callId;
            CurrentSession.RemoteUserId = fromUserId;
            CurrentSession.TargetUserId = fromUserId;
            CurrentSession.State = NativeCallState.Connected;
            CurrentSession.StatusText = "Connected.";
            CurrentSession.PeerText = $"Connected with user {fromUserId}";

            RaiseChanged();

            await TryStartLocalAudioAsync();
        }

        public async Task EndAsync()
        {
            if (CurrentSession.RemoteUserId > 0 && !string.IsNullOrWhiteSpace(CurrentSession.CallId))
            {
                await SocialManager.Instance.Realtime.EndCallAsync(CurrentSession.RemoteUserId, CurrentSession.CallId);
            }

            UnhookAudioCapture();
            AudioActivityService.Instance.Reset();
            AudioPlaybackService.Instance.Stop();
            await MicCaptureService.Instance.StopAsync();

            _sentAudioChunks = 0;
            _receivedAudioChunks = 0;
            _reportedSending = false;
            _reportedReceiving = false;

            CurrentSession.State = NativeCallState.Ended;
            CurrentSession.StatusText = "Call ended.";

            RaiseChanged();
        }

        private void EnsureRealtimeHooks()
        {
            if (_realtimeHooked)
                return;

            _realtimeHooked = true;

            SocialManager.Instance.Realtime.AudioChunkReceived += Realtime_AudioChunkReceived;
            SocialManager.Instance.Realtime.CallAnswered += Realtime_CallAnswered;
            SocialManager.Instance.Realtime.CallEnded += Realtime_CallEnded;
            SocialManager.Instance.Realtime.CallRejected += Realtime_CallRejected;
        }

        private async void Realtime_CallAnswered(object? sender, (string CallId, long FromUserId) e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(CurrentSession.CallId) && e.CallId != CurrentSession.CallId)
                    return;

                await OnCallAnsweredAsync(e.CallId, e.FromUserId);
            }
            catch
            {
            }
        }

        private async void Realtime_CallEnded(object? sender, (string CallId, long FromUserId) e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(CurrentSession.CallId) && e.CallId != CurrentSession.CallId)
                    return;

                UnhookAudioCapture();
                AudioActivityService.Instance.Reset();
                AudioPlaybackService.Instance.Stop();
                await MicCaptureService.Instance.StopAsync();

                _sentAudioChunks = 0;
                _receivedAudioChunks = 0;
                _reportedSending = false;
                _reportedReceiving = false;

                CurrentSession.State = NativeCallState.Ended;
                CurrentSession.StatusText = "Call ended by remote user.";
                CurrentSession.PeerText = $"Call with user {e.FromUserId} ended";

                RaiseChanged();
            }
            catch
            {
            }
        }

        private async void Realtime_CallRejected(object? sender, (string CallId, long FromUserId) e)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(CurrentSession.CallId) && e.CallId != CurrentSession.CallId)
                    return;

                UnhookAudioCapture();
                AudioActivityService.Instance.Reset();
                AudioPlaybackService.Instance.Stop();
                await MicCaptureService.Instance.StopAsync();

                _sentAudioChunks = 0;
                _receivedAudioChunks = 0;
                _reportedSending = false;
                _reportedReceiving = false;

                CurrentSession.State = NativeCallState.Rejected;
                CurrentSession.StatusText = "Call rejected.";
                CurrentSession.PeerText = $"User {e.FromUserId} rejected the call";

                RaiseChanged();
            }
            catch
            {
            }
        }

        private void Realtime_AudioChunkReceived(object? sender, AudioChunkEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(CurrentSession.CallId))
                    return;

                if (e.CallId != CurrentSession.CallId)
                    return;

                if (CurrentSession.RemoteUserId > 0 && e.FromUserId != CurrentSession.RemoteUserId)
                    return;

                AudioPlaybackService.Instance.Start();
                AudioPlaybackService.Instance.Play(e.AudioData);

                _receivedAudioChunks++;

                if (!_reportedReceiving)
                {
                    _reportedReceiving = true;
                    SetStatus(
                        CurrentSession.State,
                        $"Connected. Receiving remote audio... chunks={_receivedAudioChunks}",
                        CurrentSession.PeerText);
                }
                else if (_receivedAudioChunks % 50 == 0)
                {
                    SetStatus(
                        CurrentSession.State,
                        $"Connected. Receiving remote audio... chunks={_receivedAudioChunks}",
                        CurrentSession.PeerText);
                }
            }
            catch (Exception ex)
            {
                SetStatus(
                    CurrentSession.State,
                    $"Connected, but remote audio playback failed: {ex.Message}",
                    CurrentSession.PeerText);
            }
        }

        private async Task TryStartLocalAudioAsync()
        {
            try
            {
                await StartLocalAudioAsync();
                SetStatus(CurrentSession.State, "Connected. Audio started.", CurrentSession.PeerText);
            }
            catch (Exception ex)
            {
                UnhookAudioCapture();
                AudioActivityService.Instance.Reset();
                AudioPlaybackService.Instance.Stop();
                await MicCaptureService.Instance.StopAsync();

                SetStatus(
                    NativeCallState.Connected,
                    $"Connected, but audio failed to start: {ex.Message}",
                    CurrentSession.PeerText);
            }
        }

        private async Task StartLocalAudioAsync()
        {
            AudioPlaybackService.Instance.Start();

            if (!_audioHooked)
            {
                MicCaptureService.Instance.AudioCaptured += OnAudioCaptured;
                _audioHooked = true;
            }

            await MicCaptureService.Instance.StartAsync();
        }

        private void UnhookAudioCapture()
        {
            if (_audioHooked)
            {
                MicCaptureService.Instance.AudioCaptured -= OnAudioCaptured;
                _audioHooked = false;
            }
        }

        private async void OnAudioCaptured(byte[] data)
        {
            try
            {
                if (CurrentSession.State != NativeCallState.Connected)
                    return;

                if (CurrentSession.RemoteUserId <= 0 || string.IsNullOrWhiteSpace(CurrentSession.CallId))
                    return;

                if (data == null || data.Length == 0)
                    return;

                await SocialManager.Instance.Realtime.SendAudioChunkAsync(
                    CurrentSession.RemoteUserId,
                    CurrentSession.CallId,
                    data);

                _sentAudioChunks++;

                if (!_reportedSending)
                {
                    _reportedSending = true;
                    SetStatus(
                        CurrentSession.State,
                        $"Connected. Sending microphone audio... chunks={_sentAudioChunks}",
                        CurrentSession.PeerText);
                }
                else if (_sentAudioChunks % 50 == 0)
                {
                    SetStatus(
                        CurrentSession.State,
                        $"Connected. Sending microphone audio... chunks={_sentAudioChunks}",
                        CurrentSession.PeerText);
                }
            }
            catch (Exception ex)
            {
                SetStatus(
                    CurrentSession.State,
                    $"Connected, but sending microphone audio failed: {ex.Message}",
                    CurrentSession.PeerText);
            }
        }

        private void RaiseChanged()
        {
            SessionChanged?.Invoke(this, CurrentSession);
        }
    }
}