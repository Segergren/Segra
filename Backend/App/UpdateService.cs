using Serilog;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Velopack;
using Velopack.Sources;

namespace Segra.Backend.App
{
    public static class UpdateService
    {
        // Store the update information
        public static UpdateInfo? LatestUpdateInfo { get; private set; } = null;
        public static GithubSource Source = new GithubSource("https://github.com/Segergren/Segra", null, false);
        public static GithubSource BetaSource = new GithubSource("https://github.com/Segergren/Segra", null, true);
        public static UpdateManager UpdateManager { get; private set; } = new UpdateManager(Source);

        public static async Task<bool> UpdateAppIfNecessary()
        {
            try
            {
                Core.Models.Settings.Instance.State.IsCheckingForUpdates = true;

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

                Log.Information("Checking if update is necessary");
                UpdateInfo? newVersion = await UpdateManager.CheckForUpdatesAsync();

                if (newVersion == null)
                {
                    Log.Information("No update available");
                    Core.Models.Settings.Instance.State.IsCheckingForUpdates = false;
                    return false;
                }

                // Store the update info for later use
                LatestUpdateInfo = newVersion;

                // Fetch latest release notes immediately so the UI has fresh data while showing the update
                _ = Task.Run(GetReleaseNotes);

                // Get target version string
                string targetVersion = newVersion.TargetFullRelease.Version.ToString();

                // Notify frontend that update download is starting
                await MessageService.SendFrontendMessage("UpdateProgress", new
                {
                    version = targetVersion,
                    progress = 0,
                    status = "downloading",
                    message = $"Starting download of update to version {targetVersion}..."
                });

                // Download and apply the update with progress reporting
                Log.Information($"Installing update to version {targetVersion}");
                await UpdateManager.DownloadUpdatesAsync(
                    newVersion,
                    progress => SendUpdateProgressToFrontend(targetVersion, progress)
                );

                // Notify frontend that update is ready to install
                await MessageService.SendFrontendMessage("UpdateProgress", new
                {
                    version = targetVersion,
                    progress = 100,
                    status = "ready",
                    message = $"Update to version {targetVersion} is ready to install"
                });

                Core.Models.Settings.Instance.State.IsCheckingForUpdates = false;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during update check/installation");
                Core.Models.Settings.Instance.State.IsCheckingForUpdates = false;
                return false;
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

            UpdateManager.ApplyUpdatesAndRestart(LatestUpdateInfo);
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

                await MessageService.SendFrontendMessage("UpdateProgress", new
                {
                    version,
                    progress,
                    status,
                    message
                });

                Log.Information($"Update progress: {progress}%");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error sending update progress to frontend");
            }
        }

        public static async Task<bool> ForceReinstallCurrentVersionAsync(CancellationToken ct = default)
        {
            try
            {
                Core.Models.Settings.Instance.State.IsCheckingForUpdates = true;

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
                Core.Models.Settings.Instance.State.IsCheckingForUpdates = false;
            }
        }

        public static async Task GetReleaseNotes()
        {
            try
            {
                Log.Information("Getting release notes from GitHub API");

                // Get current version
                NuGet.Versioning.SemanticVersion currentVersion;
                if (UpdateManager.CurrentVersion != null)
                {
                    currentVersion = NuGet.Versioning.SemanticVersion.Parse(UpdateManager.CurrentVersion.ToString());
                }
                else
                {
                    // Running in local development, uncomment the line bellow and comment out the return to test
                    currentVersion = NuGet.Versioning.SemanticVersion.Parse("0.6.6");
                    //Log.Error("Could not get current version");
                    //return; 
                }

                Log.Information($"Current version: {currentVersion}");

                // Create HttpClient for GitHub API
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

                // Filter and process releases
                var releaseNotesList = new List<object>();
                NuGet.Versioning.SemanticVersion? targetVersion = null;

                // Check if beta releases should be included
                bool includeBeta = Core.Models.Settings.Instance.ReceiveBetaUpdates;

                // Process releases
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

                    // Handle release candidate versions
                    string displayVersion = versionString;
                    NuGet.Versioning.SemanticVersion? releaseVersion = null;

                    if (versionString.Contains("-rc."))
                    {
                        // Extract base version without RC suffix for comparison
                        string baseVersionStr = versionString.Split('-')[0];
                        if (!NuGet.Versioning.SemanticVersion.TryParse(baseVersionStr, out releaseVersion))
                        {
                            Log.Warning($"Could not parse version from tag: {release.TagName}");
                            continue;
                        }
                    }
                    else if (!NuGet.Versioning.SemanticVersion.TryParse(versionString, out releaseVersion))
                    {
                        Log.Warning($"Could not parse version from tag: {release.TagName}");
                        continue;
                    }

                    // Skip if this version is not what we're looking for based on includeOnlyRecentUpdate
                    if (targetVersion != null)
                    {
                        if (releaseVersion <= currentVersion)
                        {
                            continue;
                        }
                    }

                    // Include release notes
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
                    if (targetVersion == null && releaseNotesList.Count >= (includeBeta ? 80 : 20))
                    {
                        break;
                    }
                }

                // Send release notes to frontend
                _ = MessageService.SendFrontendMessage("ReleaseNotes", new
                {
                    releaseNotesList
                });

                Log.Information($"Sent {releaseNotesList.Count} release notes to frontend");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting release notes from GitHub API");
            }
        }

        // GitHub API response model - complete model with JsonPropertyName attributes
        private class GitHubRelease
        {
            public string? Url { get; set; }

            [JsonPropertyName("html_url")]
            public string? HtmlUrl { get; set; }

            [JsonPropertyName("tag_name")]
            public required string TagName { get; set; }

            public string? Name { get; set; }
            public bool Draft { get; set; }
            public bool Prerelease { get; set; }
            public string? Body { get; set; }

            [JsonPropertyName("created_at")]
            public DateTime? CreatedAt { get; set; }

            [JsonPropertyName("published_at")]
            public DateTime? PublishedAt { get; set; }

            [JsonPropertyName("target_commitish")]
            public string? TargetCommitish { get; set; }

            [JsonPropertyName("assets_url")]
            public string? AssetsUrl { get; set; }

            [JsonPropertyName("upload_url")]
            public string? UploadUrl { get; set; }

            public long Id { get; set; }

            [JsonPropertyName("tarball_url")]
            public string? TarballUrl { get; set; }

            [JsonPropertyName("zipball_url")]
            public string? ZipballUrl { get; set; }

            public List<GitHubAsset>? Assets { get; set; }

            public GitHubUser? Author { get; set; }

            [JsonPropertyName("node_id")]
            public string? NodeId { get; set; }
        }

        // GitHub asset model
        private class GitHubAsset
        {
            public string? Url { get; set; }
            public long Id { get; set; }

            [JsonPropertyName("node_id")]
            public string? NodeId { get; set; }

            public string? Name { get; set; }
            public string? Label { get; set; }

            [JsonPropertyName("content_type")]
            public string? ContentType { get; set; }

            public string? State { get; set; }
            public long Size { get; set; }

            [JsonPropertyName("download_count")]
            public int DownloadCount { get; set; }

            [JsonPropertyName("created_at")]
            public DateTime CreatedAt { get; set; }

            [JsonPropertyName("updated_at")]
            public DateTime UpdatedAt { get; set; }

            [JsonPropertyName("browser_download_url")]
            public string? BrowserDownloadUrl { get; set; }

            public GitHubUser? Uploader { get; set; }
        }

        // GitHub user model
        private class GitHubUser
        {
            public string? Login { get; set; }
            public long Id { get; set; }

            [JsonPropertyName("node_id")]
            public string? NodeId { get; set; }

            [JsonPropertyName("avatar_url")]
            public string? AvatarUrl { get; set; }

            [JsonPropertyName("html_url")]
            public string? HtmlUrl { get; set; }

            public string? Type { get; set; }

            [JsonPropertyName("site_admin")]
            public bool SiteAdmin { get; set; }
        }
    }
}
