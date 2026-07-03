using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace Zink.Services.Streaming
{
    public sealed class TwitchViewerCountService
    {
        public const string ChannelLoginSettingKey = "Zink.Streaming.TwitchChannelLogin";
        public const string ClientIdSettingKey = "Zink.Streaming.TwitchClientId";
        public const string AccessTokenSettingKey = "Zink.Streaming.TwitchAccessToken";
        public const string RedirectUri = "http://localhost";

        // Register Zink once in the Twitch Developer Console and put that public Client ID here for release builds.
        // Users should never need to create their own Twitch app.
        public const string ZinkTwitchClientId = "9orim957s3wno703jjnlz3inq48m8f";

        private static readonly HttpClient HttpClient = new();

        public static TwitchViewerCountService Instance { get; } = new();

        private TwitchViewerCountService()
        {
        }

        public event EventHandler<TwitchViewerCountSnapshot>? SnapshotChanged;

        public static string ChannelLogin
        {
            get => ReadSetting(ChannelLoginSettingKey);
            set => WriteSetting(ChannelLoginSettingKey, value);
        }

        public static string ClientId
        {
            get
            {
                var savedClientId = ReadSetting(ClientIdSettingKey);
                if (!string.IsNullOrWhiteSpace(savedClientId))
                    return savedClientId;

                return ZinkTwitchClientId;
            }
            set => WriteSetting(ClientIdSettingKey, value);
        }

        public static string AccessToken
        {
            get => ReadSetting(AccessTokenSettingKey);
            set => WriteSetting(AccessTokenSettingKey, value);
        }

        public static bool IsConnected =>
            !string.IsNullOrWhiteSpace(ChannelLogin) &&
            !string.IsNullOrWhiteSpace(ClientId) &&
            !string.IsNullOrWhiteSpace(AccessToken);

        public static bool HasConfiguredClientId => !string.IsNullOrWhiteSpace(ClientId);

        public static string CreateState()
        {
            Span<byte> bytes = stackalloc byte[18];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public static Uri BuildAuthorizeUri(string state)
        {
            var clientId = ClientId.Trim();
            if (string.IsNullOrWhiteSpace(clientId))
                throw new InvalidOperationException("Zink needs a Twitch Client ID configured before users can connect Twitch.");

            var url =
                "https://id.twitch.tv/oauth2/authorize" +
                "?response_type=token" +
                $"&client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                $"&scope={Uri.EscapeDataString("channel:read:stream_key")}" +
                $"&state={Uri.EscapeDataString(state)}" +
                "&force_verify=false";
            return new Uri(url);
        }

        public async Task<TwitchConnectResult> CompleteImplicitAuthAsync(Uri callbackUri, string expectedState, CancellationToken token = default)
        {
            var values = ParseUrlEncoded(callbackUri.Fragment.TrimStart('#'));
            if (values.TryGetValue("error", out var error))
            {
                values.TryGetValue("error_description", out var description);
                return new TwitchConnectResult(false, null, $"{error}: {description}");
            }

            if (!values.TryGetValue("state", out var state) ||
                !string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                return new TwitchConnectResult(false, null, "Twitch sign-in state did not match.");
            }

            if (!values.TryGetValue("access_token", out var accessToken) ||
                string.IsNullOrWhiteSpace(accessToken))
            {
                return new TwitchConnectResult(false, null, "Twitch did not return an access token.");
            }

            var validation = await ValidateAccessTokenAsync(accessToken, token);
            if (!validation.Success || string.IsNullOrWhiteSpace(validation.Login))
                return validation;

            AccessToken = accessToken;
            ChannelLogin = validation.Login;

            if (string.IsNullOrWhiteSpace(validation.UserId))
                return validation;

            var streamKeyResult = await TryGetStreamKeyAsync(accessToken, validation.UserId, token);
            if (streamKeyResult.Success)
            {
                return validation with
                {
                    StreamKey = streamKeyResult.StreamKey,
                    Status = string.IsNullOrWhiteSpace(validation.Login)
                        ? "Connected to Twitch and loaded the stream key"
                        : $"Connected as {validation.Login} and loaded the stream key"
                };
            }

            return validation with
            {
                Status = $"{validation.Status}. Stream key could not be loaded: {streamKeyResult.Status}"
            };
        }

        public void Disconnect()
        {
            AccessToken = string.Empty;
            ChannelLogin = string.Empty;
            SnapshotChanged?.Invoke(this, new TwitchViewerCountSnapshot(null, false, "Twitch disconnected"));
        }

        public async Task<TwitchViewerCountSnapshot> RefreshAsync(CancellationToken token = default)
        {
            var channelLogin = ChannelLogin.Trim().TrimStart('@');
            var clientId = ClientId.Trim();
            var accessToken = AccessToken.Trim();

            if (string.IsNullOrWhiteSpace(channelLogin) ||
                string.IsNullOrWhiteSpace(clientId) ||
                string.IsNullOrWhiteSpace(accessToken))
            {
                var missing = new TwitchViewerCountSnapshot(null, false, "Add Twitch API settings");
                SnapshotChanged?.Invoke(this, missing);
                return missing;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.twitch.tv/helix/streams?user_login={Uri.EscapeDataString(channelLogin)}");
            request.Headers.TryAddWithoutValidation("Client-Id", clientId);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");

            try
            {
                using var response = await HttpClient.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                {
                    var failed = new TwitchViewerCountSnapshot(null, false, $"Twitch API {(int)response.StatusCode}");
                    SnapshotChanged?.Invoke(this, failed);
                    return failed;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                if (!document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array ||
                    data.GetArrayLength() == 0)
                {
                    var offline = new TwitchViewerCountSnapshot(0, false, "Twitch offline");
                    SnapshotChanged?.Invoke(this, offline);
                    return offline;
                }

                var streamInfo = data[0];
                var viewers = streamInfo.TryGetProperty("viewer_count", out var viewerCount)
                    ? viewerCount.GetInt32()
                    : 0;

                var snapshot = new TwitchViewerCountSnapshot(viewers, true, "Live on Twitch");
                SnapshotChanged?.Invoke(this, snapshot);
                return snapshot;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failed = new TwitchViewerCountSnapshot(null, false, ex.Message);
                SnapshotChanged?.Invoke(this, failed);
                return failed;
            }
        }

        private static string ReadSetting(string key)
        {
            try
            {
                return ApplicationData.Current.LocalSettings.Values[key] as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void WriteSetting(string key, string value)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] = value?.Trim() ?? string.Empty;
            }
            catch
            {
            }
        }

        private async Task<TwitchConnectResult> ValidateAccessTokenAsync(string accessToken, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
            request.Headers.TryAddWithoutValidation("Authorization", $"OAuth {accessToken}");

            try
            {
                using var response = await HttpClient.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                    return new TwitchConnectResult(false, null, $"Twitch token validation failed: {(int)response.StatusCode}");

                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                var root = document.RootElement;
                var login = root.TryGetProperty("login", out var loginProperty)
                    ? loginProperty.GetString()
                    : null;
                var clientId = root.TryGetProperty("client_id", out var clientProperty)
                    ? clientProperty.GetString()
                    : null;
                var userId = root.TryGetProperty("user_id", out var userIdProperty)
                    ? userIdProperty.GetString()
                    : null;

                if (!string.Equals(clientId, ClientId, StringComparison.Ordinal))
                    return new TwitchConnectResult(false, null, "Twitch token belongs to a different app client.");

                return new TwitchConnectResult(true, login, string.IsNullOrWhiteSpace(login) ? "Connected to Twitch" : $"Connected as {login}", UserId: userId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new TwitchConnectResult(false, null, ex.Message);
            }
        }

        private async Task<TwitchStreamKeyResult> TryGetStreamKeyAsync(string accessToken, string broadcasterId, CancellationToken token)
        {
            var clientId = ClientId.Trim();
            if (string.IsNullOrWhiteSpace(clientId))
                return new TwitchStreamKeyResult(false, null, "Client ID is missing.");

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.twitch.tv/helix/streams/key?broadcaster_id={Uri.EscapeDataString(broadcasterId)}");
            request.Headers.TryAddWithoutValidation("Client-Id", clientId);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");

            try
            {
                using var response = await HttpClient.SendAsync(request, token);
                if (!response.IsSuccessStatusCode)
                    return new TwitchStreamKeyResult(false, null, $"Twitch API {(int)response.StatusCode}");

                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
                if (!document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array ||
                    data.GetArrayLength() == 0)
                {
                    return new TwitchStreamKeyResult(false, null, "Twitch did not return a stream key.");
                }

                var streamKey = data[0].TryGetProperty("stream_key", out var streamKeyProperty)
                    ? streamKeyProperty.GetString()
                    : null;
                return string.IsNullOrWhiteSpace(streamKey)
                    ? new TwitchStreamKeyResult(false, null, "Twitch returned an empty stream key.")
                    : new TwitchStreamKeyResult(true, streamKey, "Loaded stream key");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new TwitchStreamKeyResult(false, null, ex.Message);
            }
        }

        private static Dictionary<string, string> ParseUrlEncoded(string value)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(value))
                return result;

            foreach (var pair in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
                var itemValue = parts.Length > 1
                    ? Uri.UnescapeDataString(parts[1].Replace('+', ' '))
                    : string.Empty;
                result[key] = itemValue;
            }

            return result;
        }
    }

    public sealed record TwitchViewerCountSnapshot(int? ViewerCount, bool IsLive, string Status);
    public sealed record TwitchConnectResult(
        bool Success,
        string? Login,
        string Status,
        string? StreamKey = null,
        string? UserId = null);
    public sealed record TwitchStreamKeyResult(bool Success, string? StreamKey, string Status);
}
