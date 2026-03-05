using System.Text;
using System.Text.Json;
using Serilog;

namespace Segra.Backend.Services
{
    public static class AuthService
    {
        private const string ApiBase = "https://segra.tv/api";
        private static readonly HttpClient _httpClient = new();
        private static readonly SemaphoreSlim _refreshSemaphore = new(1, 1);

        private static Core.Models.Auth Auth => Core.Models.Settings.Instance.Auth;

        public static void Login(string jwt, string refreshToken)
        {
            if (string.IsNullOrEmpty(jwt) || string.IsNullOrEmpty(refreshToken))
            {
                Log.Warning("Login attempt with empty JWT or refresh token");
                return;
            }

            Auth.Jwt = jwt;
            Auth.RefreshToken = refreshToken;
            Log.Information("Login successful");
        }

        public static void Logout()
        {
            Log.Information("Logged out user");
            Auth.Jwt = string.Empty;
            Auth.RefreshToken = string.Empty;
        }

        public static bool IsAuthenticated() => Auth.HasCredentials();

        public static async Task<string> GetJwtAsync()
        {
            var jwt = Auth.Jwt;
            if (string.IsNullOrEmpty(jwt)) return string.Empty;

            await _refreshSemaphore.WaitAsync();
            try
            {
                if (IsTokenExpired(Auth.Jwt))
                {
                    var content = new StringContent(
                        JsonSerializer.Serialize(new { refresh_token = Auth.RefreshToken }),
                        Encoding.UTF8, "application/json");

                    var response = await _httpClient.PostAsync($"{ApiBase}/auth/token/refresh", content);
                    if (response.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        Auth.Jwt = doc.RootElement.GetProperty("access_token").GetString() ?? string.Empty;
                        Auth.RefreshToken = doc.RootElement.GetProperty("refresh_token").GetString() ?? string.Empty;
                        Log.Debug("Backend refreshed expired token");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Backend token refresh failed: {ex.Message}");
            }
            finally
            {
                _refreshSemaphore.Release();
            }

            return Auth.Jwt;
        }

        private static bool IsTokenExpired(string jwt)
        {
            try
            {
                var payload = jwt.Split('.')[1];
                payload = payload.Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                }
                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                var exp = doc.RootElement.GetProperty("exp").GetInt64();
                return DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= exp - 30;
            }
            catch { return false; }
        }
    }
}
