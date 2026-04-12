using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Zink.Services.Calling
{
    public sealed class RegisterRequest
    {
        [JsonPropertyName("email")]
        public string Email { get; set; } = "";

        [JsonPropertyName("username")]
        public string Username { get; set; } = "";

        [JsonPropertyName("password")]
        public string Password { get; set; } = "";

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";
    }

    public sealed class RegisterResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public sealed class LoginRequest
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = "";

        [JsonPropertyName("password")]
        public string Password { get; set; } = "";
    }

    public sealed class LoginResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("userId")]
        public int UserId { get; set; }

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public sealed class FriendDto
    {
        [JsonPropertyName("userId")]
        public int UserId { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; } = "";

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";
    }

    public sealed class FriendsResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("friends")]
        public List<FriendDto> Friends { get; set; } = new();

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public sealed class AddFriendRequest
    {
        [JsonPropertyName("target")]
        public string Target { get; set; } = "";
    }

    public sealed class ApiMessageResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    public sealed class IceServerDto
    {
        [JsonPropertyName("urls")]
        public string[] Urls { get; set; } = [];

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("credential")]
        public string? Credential { get; set; }
    }

    public sealed class RtcConfigResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("iceServers")]
        public List<IceServerDto> IceServers { get; set; } = new();

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}