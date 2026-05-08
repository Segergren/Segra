using System.Net.Http.Headers;
using System.Text.Json;
using Segra.Backend.Core.Models;
using Serilog;

namespace Segra.Backend.Services
{
    public static class StreamingService
    {
        private const string ApiBase = "https://segra.tv/api";
        private static readonly HttpClient _httpClient = new();
        private static readonly SemaphoreSlim _fetchSemaphore = new(1, 1);

        /// <summary>
        /// Fetches the user's stream key from segra-web if not already cached.
        /// Idempotent — safe to call repeatedly. Caches the key + ingest URL into Settings.
        /// </summary>
        public static async Task EnsureStreamKeyAsync()
        {
            if (!string.IsNullOrEmpty(Settings.Instance.StreamKey) &&
                !string.IsNullOrEmpty(Settings.Instance.StreamIngestUrl))
            {
                return;
            }

            await _fetchSemaphore.WaitAsync();
            try
            {
                if (!string.IsNullOrEmpty(Settings.Instance.StreamKey) &&
                    !string.IsNullOrEmpty(Settings.Instance.StreamIngestUrl))
                {
                    return;
                }

                var jwt = await AuthService.GetJwtAsync();
                if (string.IsNullOrEmpty(jwt))
                {
                    Log.Warning("Cannot fetch stream key — not authenticated");
                    return;
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/streams/key");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

                var response = await _httpClient.SendAsync(req);
                if (!response.IsSuccessStatusCode)
                {
                    Log.Warning($"Stream key fetch failed: HTTP {(int)response.StatusCode}");
                    return;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Settings.Instance.StreamKey = doc.RootElement.GetProperty("streamKey").GetString() ?? string.Empty;
                Settings.Instance.StreamIngestUrl = doc.RootElement.GetProperty("ingestUrl").GetString() ?? string.Empty;
                Log.Information("Stream key fetched and cached");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Stream key fetch failed");
            }
            finally
            {
                _fetchSemaphore.Release();
            }
        }
    }
}
