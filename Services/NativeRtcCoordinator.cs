using System;
using System.Threading.Tasks;
using Zink.Models;

namespace Zink.Services
{
    public sealed class NativeRtcCoordinator : IAsyncDisposable
    {
        private readonly NativeSignalingClient _signaling = new();
        private readonly IMicrophoneSource _microphone = new WasapiMicrophoneSource();
        private readonly INativeScreenShareSource _shareSource = new WindowsGraphicsCaptureShareSource();

        private bool _isConnectedToSignaling;
        private bool _isMuted;
        private bool _isInCall;
        private bool _isSharing;
        private bool _hasPendingIncomingCall;

        private string _roomId = "";
        private string _userId = "";
        private string _lastRemoteUser = "";

        public event Action<string>? StatusChanged;
        public event Action<NativeCallState>? CallStateChanged;
        public event Action<string>? RemoteInfoChanged;
        public event Action<ShareStats>? ShareStatsChanged;

        public NativeRtcCoordinator()
        {
            _signaling.StatusChanged += OnSignalingStatusChanged;
            _signaling.MessageReceived += OnSignalReceived;

            _shareSource.FrameReady += OnShareFrameReady;
            _microphone.PcmFrameReady += OnMicPcmReady;
        }

        public async Task ConnectSignalingAsync(string wsUrl, string roomId, string userId)
        {
            if (string.IsNullOrWhiteSpace(wsUrl))
                throw new InvalidOperationException("Server URL is required.");

            if (string.IsNullOrWhiteSpace(roomId))
                throw new InvalidOperationException("Room ID is required.");

            if (string.IsNullOrWhiteSpace(userId))
                throw new InvalidOperationException("User ID is required.");

            _roomId = roomId;
            _userId = userId;

            await _signaling.ConnectAsync(wsUrl, roomId, userId);
            _isConnectedToSignaling = true;

            StatusChanged?.Invoke("Joined signaling room.");
            RemoteInfoChanged?.Invoke("Waiting for another user to join.");
            CallStateChanged?.Invoke(NativeCallState.Idle);
        }

        public async Task StartOutgoingCallAsync()
        {
            EnsureConnected();

            await _microphone.StartAsync();

            _isInCall = true;
            _hasPendingIncomingCall = false;

            await _signaling.SendAsync(new SignalEnvelope
            {
                Type = "offer",
                Message = "voice-call-offer"
            });

            StatusChanged?.Invoke("Calling remote user...");
            RemoteInfoChanged?.Invoke("Outgoing call started. Waiting for answer.");
            CallStateChanged?.Invoke(NativeCallState.Connecting);
        }

        public async Task AnswerIncomingCallAsync()
        {
            EnsureConnected();

            if (!_hasPendingIncomingCall)
                throw new InvalidOperationException("There is no incoming call to answer.");

            await _microphone.StartAsync();

            _isInCall = true;
            _hasPendingIncomingCall = false;

            await _signaling.SendAsync(new SignalEnvelope
            {
                Type = "answer",
                Message = "voice-call-answer"
            });

            StatusChanged?.Invoke("Call answered.");
            RemoteInfoChanged?.Invoke(string.IsNullOrWhiteSpace(_lastRemoteUser)
                ? "Connected."
                : $"Connected to {_lastRemoteUser}");
            CallStateChanged?.Invoke(NativeCallState.Connected);
        }

        public async Task HangUpAsync()
        {
            if (!_isConnectedToSignaling)
                return;

            if (_isSharing)
            {
                await StopScreenShareAsync();
            }

            if (_isInCall || _hasPendingIncomingCall)
            {
                await _signaling.SendAsync(new SignalEnvelope
                {
                    Type = "call-ended",
                    Message = "hangup"
                });
            }

            await _microphone.StopAsync();

            _isInCall = false;
            _isSharing = false;
            _hasPendingIncomingCall = false;

            StatusChanged?.Invoke("Call ended.");
            RemoteInfoChanged?.Invoke("No remote peer yet.");
            CallStateChanged?.Invoke(NativeCallState.Ended);
        }

        public async Task<ShareStats> StartScreenShareAsync(bool require4k)
        {
            EnsureConnected();

            if (!_isInCall)
                throw new InvalidOperationException("Start a call before starting screen share.");

            var stats = await _shareSource.StartAsync(require4k);
            _isSharing = true;

            ShareStatsChanged?.Invoke(stats);
            StatusChanged?.Invoke("Screen sharing started.");
            RemoteInfoChanged?.Invoke(string.IsNullOrWhiteSpace(_lastRemoteUser)
                ? "Screen sharing is active."
                : $"Screen sharing with {_lastRemoteUser}");
            CallStateChanged?.Invoke(NativeCallState.Sharing);

            await _signaling.SendAsync(new SignalEnvelope
            {
                Type = "share-started",
                Message = stats.ToString()
            });

            return stats;
        }

        public async Task StopScreenShareAsync()
        {
            if (!_isSharing)
                return;

            await _shareSource.StopAsync();
            _isSharing = false;

            StatusChanged?.Invoke("Screen sharing stopped.");
            CallStateChanged?.Invoke(_isInCall ? NativeCallState.Connected : NativeCallState.Idle);

            if (_isConnectedToSignaling)
            {
                await _signaling.SendAsync(new SignalEnvelope
                {
                    Type = "share-stopped",
                    Message = "Screen share stopped."
                });
            }
        }

        public async Task ToggleMuteAsync()
        {
            if (!_isInCall)
                throw new InvalidOperationException("You are not in a call.");

            await _microphone.ToggleMuteAsync();
            _isMuted = !_isMuted;

            StatusChanged?.Invoke(_isMuted ? "Microphone muted." : "Microphone unmuted.");
        }

        private void OnSignalingStatusChanged(string message)
        {
            StatusChanged?.Invoke(message);
        }

        private void OnSignalReceived(SignalEnvelope msg)
        {
            if (msg == null)
                return;

            if (!string.IsNullOrWhiteSpace(msg.FromUser) &&
                !string.Equals(msg.FromUser, _userId, StringComparison.OrdinalIgnoreCase))
            {
                _lastRemoteUser = msg.FromUser;
            }

            switch (msg.Type)
            {
                case "peer-joined":
                    RemoteInfoChanged?.Invoke(string.IsNullOrWhiteSpace(_lastRemoteUser)
                        ? "Remote peer joined the room."
                        : $"{_lastRemoteUser} joined the room.");
                    StatusChanged?.Invoke("Remote peer joined.");
                    break;

                case "peer-left":
                    _isInCall = false;
                    _isSharing = false;
                    _hasPendingIncomingCall = false;

                    RemoteInfoChanged?.Invoke(string.IsNullOrWhiteSpace(_lastRemoteUser)
                        ? "Remote peer left the room."
                        : $"{_lastRemoteUser} left the room.");
                    StatusChanged?.Invoke("Remote peer left.");
                    CallStateChanged?.Invoke(NativeCallState.Ended);
                    break;

                case "offer":
                    _hasPendingIncomingCall = true;
                    _isInCall = false;

                    RemoteInfoChanged?.Invoke(string.IsNullOrWhiteSpace(_lastRemoteUser)
                        ? "Incoming call."
                        : $"Incoming call from {_lastRemoteUser}");
                    StatusChanged?.Invoke("Incoming call received.");
                    CallStateChanged?.Invoke(NativeCallState.Ringing);
                    break;

                case "answer":
                    _hasPendingIncomingCall = false;
                    _isInCall = true;

                    RemoteInfoChanged?.Invoke(string.IsNullOrWhiteSpace(_lastRemoteUser)
                        ? "Call connected."
                        : $"Connected to {_lastRemoteUser}");
                    StatusChanged?.Invoke("Remote user answered.");
                    CallStateChanged?.Invoke(NativeCallState.Connected);
                    break;

                case "call-ended":
                    _isInCall = false;
                    _isSharing = false;
                    _hasPendingIncomingCall = false;

                    RemoteInfoChanged?.Invoke(string.IsNullOrWhiteSpace(_lastRemoteUser)
                        ? "Remote user ended the call."
                        : $"{_lastRemoteUser} ended the call.");
                    StatusChanged?.Invoke("Remote hang up.");
                    CallStateChanged?.Invoke(NativeCallState.Ended);
                    break;

                case "share-started":
                    RemoteInfoChanged?.Invoke(string.IsNullOrWhiteSpace(_lastRemoteUser)
                        ? "Remote screen share started."
                        : $"{_lastRemoteUser} started screen sharing.");
                    StatusChanged?.Invoke("Remote screen sharing started.");
                    break;

                case "share-stopped":
                    RemoteInfoChanged?.Invoke(string.IsNullOrWhiteSpace(_lastRemoteUser)
                        ? "Remote screen share stopped."
                        : $"{_lastRemoteUser} stopped screen sharing.");
                    StatusChanged?.Invoke("Remote screen sharing stopped.");
                    break;

                case "error":
                    StatusChanged?.Invoke(string.IsNullOrWhiteSpace(msg.Message)
                        ? "Signaling error."
                        : msg.Message);
                    CallStateChanged?.Invoke(NativeCallState.Failed);
                    break;
            }
        }

        private void OnMicPcmReady(byte[] pcm, int sampleRate, int channels)
        {
        }

        private void OnShareFrameReady(byte[] frame, int width, int height, long timestamp)
        {
        }

        private void EnsureConnected()
        {
            if (!_isConnectedToSignaling)
                throw new InvalidOperationException("Join a signaling room first.");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _shareSource.DisposeAsync();
            }
            catch
            {
            }

            try
            {
                await _microphone.DisposeAsync();
            }
            catch
            {
            }

            try
            {
                await _signaling.DisposeAsync();
            }
            catch
            {
            }
        }
    }
}