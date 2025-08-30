using Serilog;
using Supabase.Gotrue;

namespace Segra.Backend.Services
{

    public static class AuthService
    {
        public static Session? Session { get; set; }
        private const string Url = "https://ponthqrnesnanivsatps.supabase.co";
        private const string PublicApiKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InBvbnRocXJuZXNuYW5pdnNhdHBzIiwicm9sZSI6ImFub24iLCJpYXQiOjE3Mzc2NzMzMjgsImV4cCI6MjA1MzI0OTMyOH0.k8pLDkDgKV0ZLjZjAZ6eUHa40rot5qWa7iJDQKWy1FA";
        private static readonly Supabase.Client _client = new(Url, PublicApiKey);

        // Try to login with stored credentials on startup
        public static async Task TryAutoLogin()
        {
            try
            {
                var auth = Models.Settings.Instance.Auth;
                if (auth.HasCredentials())
                {
                    Log.Information("Attempting to login with stored credentials");
                    await Login(auth.Jwt, auth.RefreshToken, isAutoLogin: true);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Auto login failed: {ex.Message}");
                Session = null;
            }
        }

        public static async Task Login(string jwt, string refreshToken, bool isAutoLogin = false)
        {
            try
            {
                Log.Debug($"Login attempt starting - JWT length: {jwt?.Length ?? 0}, RefreshToken length: {refreshToken?.Length ?? 0}, IsAutoLogin: {isAutoLogin}");

                if (string.IsNullOrEmpty(jwt) || string.IsNullOrEmpty(refreshToken))
                {
                    Log.Warning("Login attempt with empty JWT or refresh token");
                    return;
                }

                if (!IsAuthenticated() || Session == null || Session.Expired())
                {
                    Log.Debug("Current session is null, expired, or not authenticated. Setting new session...");
                    Session = await _client.Auth.SetSession(jwt, refreshToken);
                    Log.Debug($"SetSession completed. Session is {(Session == null ? "null" : "valid")}");

                    Log.Debug("Refreshing session...");
                    Session = await _client.Auth.RefreshSession();
                    Log.Debug($"RefreshSession completed. Session is {(Session == null ? "null" : "valid")}");

                    // Save the updated tokens to settings
                    if (Session != null)
                    {
                        Log.Debug($"Saving tokens to settings. AccessToken length: {Session.AccessToken?.Length ?? 0}, RefreshToken length: {Session.RefreshToken?.Length ?? 0}");
                        Models.Settings.Instance.Auth.Jwt = Session?.AccessToken ?? string.Empty;
                        Models.Settings.Instance.Auth.RefreshToken = Session?.RefreshToken ?? string.Empty;

                        if (isAutoLogin)
                        {
                            Log.Information($"Auto login successful for user {Session?.User?.Id}");
                        }
                        else
                        {
                            Log.Information($"Manual login successful for user {Session?.User?.Id}");
                        }
                    }
                    else
                    {
                        Log.Warning("Session is null after refresh attempt");
                    }
                }
                else
                {
                    Log.Debug("User already authenticated with valid session");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Login failed: {ex.Message}");
                Log.Debug($"Login exception details: {ex}");
                Session = null;
            }
        }

        public static Task Logout()
        {
            if (Session != null)
            {
                try
                {
                    string userId = Session.User?.Id ?? "unknown";

                    // We do not need to call the sign out method because the session is already removed since frontend called it
                    //await _client.Auth.SignOut();

                    Log.Information($"Logged out user: {userId}");
                    Session = null;

                    // Clear stored credentials
                    Models.Settings.Instance.Auth.Jwt = string.Empty;
                    Models.Settings.Instance.Auth.RefreshToken = string.Empty;
                }
                catch (Exception ex)
                {
                    Log.Error($"Logout failed: {ex.Message}");

                    // Clear stored credentials
                    Models.Settings.Instance.Auth.Jwt = string.Empty;
                    Models.Settings.Instance.Auth.RefreshToken = string.Empty;
                }
            }

            return Task.CompletedTask;
        }

        public static bool IsAuthenticated()
        {
            bool isAuthenticated = Session != null && !string.IsNullOrEmpty(Session.AccessToken);
            Log.Debug($"IsAuthenticated check: {isAuthenticated}, Session is {(Session == null ? "null" : "not null")}, AccessToken is {(string.IsNullOrEmpty(Session?.AccessToken) ? "empty/null" : "present")}");

            if (Session != null && Session.Expired())
            {
                Log.Debug("Session exists but is expired");
            }

            return isAuthenticated;
        }

        public static async Task<string> GetJwtAsync()
        {
            Log.Debug($"GetJwtAsync called. Session is {(Session == null ? "null" : "not null")}");

            if (Session == null)
            {
                Log.Debug("Session is null, attempting to refresh");
            }
            else if (Session.Expired())
            {
                Log.Debug($"Session is expired (Expiry: {Session.ExpiresAt}), attempting to refresh");
            }

            if (Session == null || Session.Expired() == true)
            {
                try
                {
                    Log.Debug("Refreshing session...");
                    Session = await _client.Auth.RefreshSession();
                    Log.Debug($"RefreshSession completed. Session is {(Session == null ? "null" : "valid")}");

                    // Update stored tokens when refreshed
                    if (Session != null)
                    {
                        Log.Debug($"Refreshed tokens. New AccessToken length: {Session.AccessToken?.Length ?? 0}, RefreshToken length: {Session.RefreshToken?.Length ?? 0}");
                        Models.Settings.Instance.Auth.Jwt = Session.AccessToken ?? string.Empty;
                        Models.Settings.Instance.Auth.RefreshToken = Session.RefreshToken ?? string.Empty;
                    }
                    else
                    {
                        Log.Warning("Session is null after refresh attempt in GetJwtAsync");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to refresh session: {ex.Message}");
                    Log.Debug($"Session refresh exception details: {ex}");
                }
            }
            else
            {
                Log.Debug($"Using existing valid session token (Expiry: {Session.ExpiresAt()})");
            }

            string token = Session?.AccessToken ?? string.Empty;
            Log.Debug($"Returning JWT token of length: {token.Length}");
            return token;
        }
    }
}
