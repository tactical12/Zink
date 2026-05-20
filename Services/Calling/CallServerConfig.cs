using System;

namespace Zink.Services.Calling
{
    public static class CallServerConfig
    {
        public const string BaseHttpUrl = "https://34-73-152-179.nip.io";
        public const string BaseWsUrl = "wss://34-73-152-179.nip.io/ws";

        public static string RegisterUrl => $"{BaseHttpUrl}/api/register";
        public static string LoginUrl => $"{BaseHttpUrl}/api/login";
        public static string FriendsUrl => $"{BaseHttpUrl}/api/friends";
        public static string AddFriendUrl => $"{BaseHttpUrl}/api/friends/add";
        public static string RtcConfigUrl => $"{BaseHttpUrl}/api/rtc-config";
        public static string DiagnosticsUploadUrl => $"{BaseHttpUrl}/api/diagnostics/upload";

        public static string GetWebSocketUrl(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("Token cannot be null or empty.", nameof(token));

            return $"{BaseWsUrl}?token={Uri.EscapeDataString(token)}";
        }
    }
}
