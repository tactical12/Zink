using System;
using System.Threading;
using System.Threading.Tasks;

namespace Zink.Services.Calling
{
    public sealed class AuthService
    {
        public static AuthService Instance { get; } = new AuthService();

        private AuthService()
        {
        }

        public async Task<RegisterResponse> RegisterAsync(string email, string username, string password, string displayName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(email))
                throw new ArgumentException("Email is required.", nameof(email));

            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username is required.", nameof(username));

            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Password is required.", nameof(password));

            var request = new RegisterRequest
            {
                Email = email.Trim(),
                Username = username.Trim(),
                Password = password,
                DisplayName = displayName?.Trim() ?? ""
            };

            using var api = new CallApiClient();
            var response = await api.PostAsync<RegisterRequest, RegisterResponse>(
                CallServerConfig.RegisterUrl,
                request,
                null,
                cancellationToken);

            return response ?? new RegisterResponse
            {
                Success = false,
                Message = "No response from server."
            };
        }

        public async Task<LoginResponse> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username is required.", nameof(username));

            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Password is required.", nameof(password));

            var request = new LoginRequest
            {
                Username = username.Trim(),
                Password = password
            };

            using var api = new CallApiClient();
            var response = await api.PostAsync<LoginRequest, LoginResponse>(
                CallServerConfig.LoginUrl,
                request,
                null,
                cancellationToken);

            if (response == null)
            {
                return new LoginResponse
                {
                    Success = false,
                    Message = "No response from server."
                };
            }

            if (response.Success && !string.IsNullOrWhiteSpace(response.Token))
            {
                await TokenStore.Instance.SaveAsync(response);
            }

            return response;
        }

        public Task LogoutAsync()
        {
            return TokenStore.Instance.ClearAsync();
        }

        public Task<string?> GetSavedTokenAsync()
        {
            return TokenStore.Instance.GetTokenAsync();
        }
    }
}