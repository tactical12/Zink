using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Zink.Services.Calling
{
    public sealed class CallSessionService : IAsyncDisposable
    {
        public static CallSessionService Instance { get; } = new CallSessionService();

        private readonly SignalingClient _signalingClient = new();

        public event EventHandler<string>? RawSignalReceived;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<Exception>? ErrorOccurred;

        public bool IsConnected => _signalingClient.IsConnected;

        private CallSessionService()
        {
            _signalingClient.MessageReceived += (_, message) =>
            {
                RawSignalReceived?.Invoke(this, message);
            };

            _signalingClient.StatusChanged += (_, status) =>
            {
                StatusChanged?.Invoke(this, status);
            };

            _signalingClient.ErrorOccurred += (_, ex) =>
            {
                ErrorOccurred?.Invoke(this, ex);
            };
        }

        public async Task<RtcConfigResponse> InitializeAsync(CancellationToken cancellationToken = default)
        {
            var token = await TokenStore.Instance.GetTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("User is not logged in.");

            var rtcConfig = await RtcConfigService.Instance.GetRtcConfigAsync(cancellationToken);
            if (!rtcConfig.Success)
                throw new InvalidOperationException(rtcConfig.Message ?? "RTC config failed.");

            await _signalingClient.ConnectAsync(token, cancellationToken);

            return rtcConfig;
        }

        public async Task SendSignalAsync(object payload, CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.Serialize(payload);
            await _signalingClient.SendTextAsync(json, cancellationToken);
        }

        public Task DisconnectAsync()
        {
            return _signalingClient.DisconnectAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _signalingClient.DisposeAsync();
        }
    }
}