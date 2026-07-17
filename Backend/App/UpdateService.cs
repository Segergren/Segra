using Serilog;
using Velopack;
using System.Text;
using System.Text.Json;
using Velopack.Sources;
using Segra.Backend.Recorder;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace Segra.Backend.App
{
    public static class UpdateService
    {
        public static UpdateInfo? LatestUpdateInfo { get; private set; } = null;
        public static GithubSource Source = new("https://github.com/Segergren/Segra", null, false);
        public static GithubSource BetaSource = new("https://github.com/Segergren/Segra", null, true);
        public static UpdateManager UpdateManager { get; private set; } = new(Source);

        // Serializes Velopack operations that share the on-disk .velopack_lock.
        private static readonly SemaphoreSlim _updateGate = new(1, 1);

        // 1-hour cache for automatic update checks. Manual checks and startup pass forceCheck=true to bypass.
        private static readonly TimeSpan UpdateCheckCacheTtl = TimeSpan.FromHours(1);
        private static DateTime _lastUpdateCheckUtc = DateTime.MinValue;
        private static DateTime _lastReleaseNotesFetchUtc = DateTime.MinValue;
        private static List<object>? _cachedReleaseNotesList = null;
        private static readonly object _updateProgressLock = new();
        private static object? _currentUpdateProgress = null;

        private static object SetCurrentUpdateProgress(string version, int progress, string status, string message)
        {
            var updateProgress = new
            {
                version,
                progress,
                status,
                message
            };

            lock (_updateProgressLock)
            {
                _currentUpdateProgress = updateProgress;
            }

            return updateProgress;
        }

        public static async Task SendCurrentUpdateProgressToFrontend()
        {
            object? updateProgress;
            lock (_updateProgressLock)
            {
                updateProgress = _currentUpdateProgress;
            }

            if (updateProgress != null)
            {
                await MessageService.SendFrontendMessage("UpdateProgress", updateProgress);
            }
        }

        public static async Task<bool> UpdateAppIfNecessary(bool forceCheck = false)
        {
            if (!forceCheck && DateTime.UtcNow - _lastUpdateCheckUtc < UpdateCheckCacheTtl)
            {
                Log.Information($"Skipping update check: cached result from {_lastUpdateCheckUtc:O} is still valid (TTL {UpdateCheckCacheTtl.TotalHours}h)");
                return false;
            }

            DateTime waitStartedUtc = DateTime.UtcNow;
            await _updateGate.WaitAsync();
            try
            {
                // Reuse the result if another check finished while we were waiting on the gate.
                if (_lastUpdateCheckUtc >= waitStartedUtc)
                {
                    Log.Information("Skipping update check: another check completed while waiting for the update lock");
                    return false;
                }

                Core.Models.AppState.Instance.IsCheckingForUpdates = true;

                bool useBetaChannel = Core.Models.Settings.Instance.ReceiveBetaUpdates;
                if (useBetaChannel)
                {
                    UpdateManager = new UpdateManager(BetaSource);
                    Log.Information("Using beta update channel");
                }
                else
                {
                    UpdateManager = new UpdateManager(Source);
                    Log.Information("Using stable update channel");
                }

                if (!UpdateManager.IsInstalled)
                {
                    Log.Information("Skipping update check: app is not installed (running from dev/portable build)");
                    Core.Models.AppState.Instance.IsCheckingForUpdates = false;
                    return false;
                }

                Log.Information("Checking if update is necessary");
                UpdateInfo? newVersion = await UpdateManager.CheckForUpdatesAsync();
                _lastUpdateCheckUtc = DateTime.UtcNow;

                Core.Models.AppState.Instance.IsCheckingForUpdates = false;

                if (newVersion == null)
                {
                    Log.Information("No update available");
                    return false;
                }

                LatestUpdateInfo = newVersion;

                // Fetch latest release notes immediately so the UI has fresh data while showing the update
                _ = Task.Run(() => GetReleaseNotes(forceCheck: true));

                string targetVersion = newVersion.TargetFullRelease.Version.ToString();

                // Notify frontend that update download is starting
                await SendUpdateProgressToFrontend(
                    version: targetVersion,
                    progress: 0,
                    status: "downloading",
                    message: $"Starting download of update to version {targetVersion}...");

                // Download and apply the update with progress reporting
                Log.Information($"Installing update to version {targetVersion}");
                await UpdateManager.DownloadUpdatesAsync(
                    newVersion,
                    progress => SendUpdateProgressToFrontend(targetVersion, progress)
                );

                // Notify frontend that update is ready to install
                await SendUpdateProgressToFrontend(
                    version: targetVersion,
                    progress: 100,
                    status: "ready",
                    message: $"Update to version {targetVersion} is ready to install");

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during update check/installation");
                Core.Models.AppState.Instance.IsCheckingForUpdates = false;
                return false;
            }
            finally
            {
                _updateGate.Release();
            }
        }

        public static void ApplyUpdate()
        {
            Log.Information("Applying update");
            if (UpdateManager == null || LatestUpdateInfo == null)
            {
                Log.Warning("UpdateManager or LatestUpdateInfo is null, cannot apply update");
                return;
            }

            // Stop any active recording first so OBS finalizes cleanly, mirroring Program.cs's shutdown path.
            if (Core.Models.AppState.Instance.Recording != null || Core.Models.AppState.Instance.PreRecording != null)
            {
                Log.Information("Active recording detected while applying update; stopping it first.");
                try
                {
                    Task.Run(() => OBSService.StopRecording()).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error stopping recording before applying update");
                }
            }

            // Shutdown OBS before restarting to unload graphics-hook64.dll from game processes.
            // ApplyUpdatesAndRestart kills the process immediately, bypassing Program.Shutdown().
            OBSService.Shutdown();

            UpdateManager.ApplyUpdatesAndRestart(LatestUpdateInfo);
        }

        private static async Task SendUpdateProgressToFrontend(string version, int progress, string status, string message)
        {
            object updateProgress = SetCurrentUpdateProgress(version, progress, status, message);
            await MessageService.SendFrontendMessage("UpdateProgress", updateProgress);
        }

        // Helper method to send progress updates to the frontend
        public static async void SendUpdateProgressToFrontend(string version, int progress)
        {
            try
            {
                string status = progress < 100 ? "downloading" : "downloaded";
                string message = progress < 100
                    ? $"Downloading update: {progress}% complete"
                    : "Download complete, preparing to install";

                await SendUpdateProgressToFrontend(version, progress, status, message);

                Log.Information($"Update progress: {progress}%");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error sending update progress to frontend");
            }
        }

        public static async Task<bool> ForceReinstallCurrentVersionAsync(CancellationToken ct = default)
        {
            await _updateGate.WaitAsync(ct);
            try
            {
                Core.Models.AppState.Instance.IsCheckingForUpdates = true;

                bool useBetaChannel = Core.Models.Settings.Instance.ReceiveBetaUpdates;
                UpdateManager = new UpdateManager(useBetaChannel ? BetaSource : Source);
                var current = UpdateManager.CurrentVersion;

                if (current == null || UpdateManager == null)
                {
                    Log.Warning("Force reinstall aborted: not an installed build (no CurrentVersion).");
                    return false;
                }

                string? appId = UpdateManager.AppId;
                string channel = VelopackRuntimeInfo.SystemOs.GetOsShortName();

                var updateSource = useBetaChannel ? BetaSource : Source;

#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type
                var feed = await updateSource.GetReleaseFeed(
                    logger: null,
                    appId: appId,
                    channel: channel,
                    stagingId: null);
#pragma warning restore CS8625

                var target = feed.Assets
                    .Where(a => a.PackageId == appId)
                    .Where(a => a.Version == current)
                    .OrderByDescending(a => a.Type)
                    .FirstOrDefault();

                if (target == null)
                {
                    Log.Error($"Force reinstall failed: version {current} not found in feed '{channel}'.");
                    return false;
                }

                string targetVersion = target.Version.ToString();

                var updateInfo = new UpdateInfo(target, isDowngrade: false);

                await UpdateManager.DownloadUpdatesAsync(
                    updateInfo,
                    progress => SendUpdateProgressToFrontend(targetVersion, progress),
                    ct);

                LatestUpdateInfo = updateInfo;

                Log.Information($"Applying force reinstall of {targetVersion}");
                UpdateManager.ApplyUpdatesAndRestart(updateInfo.TargetFullRelease);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Force reinstall failed");
                return false;
            }
            finally
            {
                Core.Models.AppState.Instance.IsCheckingForUpdates = false;
                _updateGate.Release();
            }
        }

        public static async Task GetReleaseNotes(bool forceCheck = false)
        {
            if (!forceCheck && _cachedReleaseNotesList != null && DateTime.UtcNow - _lastReleaseNotesFetchUtc < UpdateCheckCacheTtl)
            {
                Log.Information($"Using cached release notes from {_lastReleaseNotesFetchUtc:O} (TTL {UpdateCheckCacheTtl.TotalHours}h), resending to frontend");
                await MessageService.SendFrontendMessage("ReleaseNotes", new
                {
                    releaseNotesList = _cachedReleaseNotesList
                });
                return;
            }

            try
            {
                Log.Information("Getting release notes from GitHub API");

                NuGet.Versioning.SemanticVersion currentVersion;
                if (UpdateManager.CurrentVersion != null)
                {
                    currentVersion = NuGet.Versioning.SemanticVersion.Parse(UpdateManager.CurrentVersion.ToString());
                }
                else
                {
                    // Fallback for local development builds, which have no installed version.
                    currentVersion = NuGet.Versioning.SemanticVersion.Parse("0.6.6");
                }

                Log.Information($"Current version: {currentVersion}");

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
                httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Segra", currentVersion.ToString()));

                // Fetch releases from GitHub API
                var response = await httpClient.GetAsync($"https://api.github.com/repos/Segergren/Segra/releases");
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (releases == null || !releases.Any())
                {
                    Log.Information("No releases found on GitHub");
                    return;
                }

                var releaseNotesList = new List<object>();
                bool includeBeta = Core.Models.Settings.Instance.ReceiveBetaUpdates;

                foreach (var release in releases)
                {
                    if (!includeBeta && release.Prerelease)
                    {
                        continue;
                    }

                    // Try to parse the tag name as a version
                    string versionString = release.TagName;
                    if (versionString.StartsWith("v") || versionString.StartsWith("V"))
                    {
                        versionString = versionString.Substring(1);
                    }

                    // Skip releases whose tag is not a parseable version. Prerelease tags
                    // (release candidate / beta) are validated on their base version.
                    string versionToValidate = versionString.Contains("-rc.") || versionString.Contains("-beta.")
                        ? versionString.Split('-')[0]
                        : versionString;
                    if (!NuGet.Versioning.SemanticVersion.TryParse(versionToValidate, out _))
                    {
                        Log.Warning($"Could not parse version from tag: {release.TagName}");
                        continue;
                    }

                    string releaseNotes = !string.IsNullOrEmpty(release.Body)
                        ? release.Body
                        : $"No release notes available for version {versionString}";

                    string base64Markdown = Convert.ToBase64String(Encoding.UTF8.GetBytes(releaseNotes));

                    releaseNotesList.Add(new
                    {
                        version = versionString,
                        base64Markdown,
                        releaseDate = release.PublishedAt
                    });

                    // Limit to 20 releases (80 if beta is enabled)
                    if (releaseNotesList.Count >= (includeBeta ? 80 : 20))
                    {
                        break;
                    }
                }

                // Send release notes to frontend
                _ = MessageService.SendFrontendMessage("ReleaseNotes", new
                {
                    releaseNotesList
                });

                _cachedReleaseNotesList = releaseNotesList;
                _lastReleaseNotesFetchUtc = DateTime.UtcNow;
                Log.Information($"Sent {releaseNotesList.Count} release notes to frontend");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting release notes from GitHub API");
            }
        }

        // GitHub releases API response. Only the fields consumed by GetReleaseNotes are modelled;
        // deserialization is case-insensitive and ignores the remaining JSON fields.
        private class GitHubRelease
        {
            [JsonPropertyName("tag_name")]
            public required string TagName { get; set; }

            public bool Prerelease { get; set; }
            public string? Body { get; set; }

            [JsonPropertyName("published_at")]
            public DateTime? PublishedAt { get; set; }
        }
    }
}
