using System;
using System.Threading;
using System.Threading.Tasks;

namespace Zink.Services.Calling
{
    public sealed class RtcConfigService
    {
        public static RtcConfigService Instance { get; } = new RtcConfigService();

        private RtcConfigService()
        {
        }

        public async Task<RtcConfigResponse> GetRtcConfigAsync(CancellationToken cancellationToken = default)
        {
            var token = await TokenStore.Instance.GetTokenAsync();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("User is not logged in.");

            using var api = new CallApiClient();
            var response = await api.GetAsync<RtcConfigResponse>(
                CallServerConfig.RtcConfigUrl,
                token,
                cancellationToken);

            return response ?? new RtcConfigResponse
            {
                Success = false,
                Message = "No response from server."
            };
        }
    }
}