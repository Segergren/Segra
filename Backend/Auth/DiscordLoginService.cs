using Serilog;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Segra.Backend.Api;
using Segra.Backend.App;
using Segra.Backend.Platform;

namespace Segra.Backend.Auth
{
    // Discord sign-in happens in the user's default browser instead of the app webview, so the
    // page is verifiable and the user can back out. segra.tv sends the browser back to the local
    // ContentServer, which hands the session to the frontend over the websocket.
    internal static class DiscordLoginService
    {
        private const string ApiBase = "https://segra.tv/api";
        private const string CallbackPath = "/auth/callback";
        private static readonly TimeSpan PendingTimeout = TimeSpan.FromMinutes(5);

        private static readonly Lock _lock = new();
        private static string? _pendingState;
        private static CancellationTokenSource? _pendingTimeout;

        public static bool IsCallbackPath(string path) =>
            path.Equals(CallbackPath, StringComparison.OrdinalIgnoreCase);

        public static void Begin()
        {
            string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

            lock (_lock)
            {
                _pendingTimeout?.Cancel();
                _pendingState = state;
                _pendingTimeout = new CancellationTokenSource();
                _ = ExpirePendingAsync(state, _pendingTimeout.Token);
            }

            string redirect = $"{ContentServer.Prefix.TrimEnd('/')}{CallbackPath}?state={state}";
            string url = $"{ApiBase}/auth/login/discord?desktop_redirect={Uri.EscapeDataString(redirect)}";

            Log.Information("Opening Discord login in the default browser");
            PlatformServices.Dialogs.OpenUrl(url);
        }

        public static void Cancel()
        {
            lock (_lock)
            {
                if (_pendingState == null)
                    return;

                _pendingState = null;
                _pendingTimeout?.Cancel();
                _pendingTimeout = null;
            }

            Log.Information("Discord login cancelled from the app");
        }

        public static async Task HandleCallbackAsync(HttpListenerContext context)
        {
            var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty);
            string? state = query["state"];
            string? accessToken = query["access_token"];
            string? refreshToken = query["refresh_token"];

            // The listener is reachable by anything on this machine, so an unrecognized state is
            // treated as a forged callback and never reaches the frontend.
            bool accepted = false;
            lock (_lock)
            {
                if (_pendingState != null && string.Equals(_pendingState, state, StringComparison.Ordinal))
                {
                    accepted = true;
                    _pendingState = null;
                    _pendingTimeout?.Cancel();
                    _pendingTimeout = null;
                }
            }

            if (!accepted)
            {
                Log.Warning("Discord login callback rejected: no matching pending login");
                await RespondAsync(context, "Sign-in link expired", "Head back to Segra and start the Discord sign-in again.");
                return;
            }

            if (string.IsNullOrEmpty(accessToken) || string.IsNullOrEmpty(refreshToken))
            {
                Log.Information("Discord login callback returned without a session");
                await MessageService.SendFrontendMessage("DiscordLoginResult", new { status = "cancelled" });
                await RespondAsync(context, "Sign-in cancelled", "Nothing was changed. You can close this tab.");
                return;
            }

            Log.Information("Discord login callback completed");
            await MessageService.SendFrontendMessage("DiscordLoginResult", new
            {
                status = "success",
                accessToken,
                refreshToken
            });
            await RespondAsync(context, "You're signed in", "You can close this tab and return to Segra.");

            Program.BringWindowToFront();
        }

        private static async Task ExpirePendingAsync(string state, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(PendingTimeout, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            bool expired = false;
            lock (_lock)
            {
                if (string.Equals(_pendingState, state, StringComparison.Ordinal))
                {
                    expired = true;
                    _pendingState = null;
                    _pendingTimeout = null;
                }
            }

            if (expired)
            {
                Log.Information("Discord login timed out waiting for the browser");
                await MessageService.SendFrontendMessage("DiscordLoginResult", new { status = "expired" });
            }
        }

        // No auto-close: browsers refuse close() for tabs a script did not open, so the tab is the
        // user's to dismiss. Segra pulls itself to the foreground instead.
        private static async Task RespondAsync(HttpListenerContext context, string title, string message)
        {
            string safeTitle = WebUtility.HtmlEncode(title);
            string safeMessage = WebUtility.HtmlEncode(message);

            string html = $$"""
                <!doctype html>
                <html lang="en">
                <head>
                  <meta charset="utf-8">
                  <meta name="viewport" content="width=device-width, initial-scale=1">
                  <title>{{safeTitle}} - Segra</title>
                  <style>
                    html, body { height: 100%; margin: 0; }
                    body {
                      display: flex; align-items: center; justify-content: center;
                      background: #17191c; color: #e9eaec;
                      font-family: system-ui, -apple-system, "Segoe UI", sans-serif;
                      text-align: center; padding: 24px;
                    }
                    h1 { font-size: 1.5rem; margin: 0 0 12px; }
                    p { margin: 0; color: #a5a9b0; line-height: 1.5; }
                  </style>
                </head>
                <body>
                  <main>
                    <h1>{{safeTitle}}</h1>
                    <p>{{safeMessage}}</p>
                  </main>
                </body>
                </html>
                """;

            byte[] buffer = Encoding.UTF8.GetBytes(html);
            var response = context.Response;
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
        }
    }
}
