using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace Zink.Services.Calling
{
    public sealed class TokenStore
    {
        private const string FileName = "call_auth.json";

        public static TokenStore Instance { get; } = new TokenStore();

        private TokenStore()
        {
        }

        private sealed class TokenData
        {
            public string Token { get; set; } = "";
            public int UserId { get; set; }
            public string Username { get; set; } = "";
            public string DisplayName { get; set; } = "";
        }

        private string GetFilePath()
        {
            return Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
        }

        public async Task SaveAsync(LoginResponse login)
        {
            if (login == null)
                throw new ArgumentNullException(nameof(login));

            var data = new TokenData
            {
                Token = login.Token ?? "",
                UserId = login.UserId,
                Username = login.Username ?? "",
                DisplayName = login.DisplayName ?? ""
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(GetFilePath(), json);
        }

        public async Task<string?> GetTokenAsync()
        {
            var filePath = GetFilePath();
            if (!File.Exists(filePath))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var data = JsonSerializer.Deserialize<TokenData>(json);
                return string.IsNullOrWhiteSpace(data?.Token) ? null : data.Token;
            }
            catch
            {
                return null;
            }
        }

        public async Task<(int userId, string username, string displayName)?> GetUserInfoAsync()
        {
            var filePath = GetFilePath();
            if (!File.Exists(filePath))
                return null;

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var data = JsonSerializer.Deserialize<TokenData>(json);
                if (data == null)
                    return null;

                return (data.UserId, data.Username, data.DisplayName);
            }
            catch
            {
                return null;
            }
        }

        public Task ClearAsync()
        {
            var filePath = GetFilePath();
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            return Task.CompletedTask;
        }
    }
}