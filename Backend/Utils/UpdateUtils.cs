using Serilog;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Versioning;
using Velopack;
using Velopack.Sources;

namespace Segra.Backend.Utils
{
    public static class UpdateUtils
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
                Models.Settings.Instance.State.IsCheckingForUpdates = true;

                bool useBetaChannel = Models.Settings.Instance.ReceiveBetaUpdates;
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
                UpdateInfo newVersion = await UpdateManager.CheckForUpdatesAsync();

                if (newVersion == null)
                {
                    Log.Information("No update available");
                    return false;
                }

                // Store the update info for later use
                LatestUpdateInfo = newVersion;

                // Get target version string
                string targetVersion = newVersion.TargetFullRelease.Version.ToString();

                // Notify frontend that update download is starting
                await MessageUtils.SendFrontendMessage("UpdateProgress", new
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
                await MessageUtils.SendFrontendMessage("UpdateProgress", new
                {
                    version = targetVersion,
                    progress = 100,
                    status = "ready",
                    message = $"Update to version {targetVersion} is ready to install"
                });

                Models.Settings.Instance.State.IsCheckingForUpdates = false;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during update check/installation");
                Models.Settings.Instance.State.IsCheckingForUpdates = false;
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

                await MessageUtils.SendFrontendMessage("UpdateProgress", new
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

        public static async Task GetReleaseNotes()
        {
            try
            {
                Log.Information("Getting release notes from GitHub API");

                // Get current version
                SemanticVersion currentVersion;
                if (UpdateManager.CurrentVersion != null)
                {
                    currentVersion = UpdateManager.CurrentVersion;
                }
                else
                {
                    // Running in local development, uncomment the line bellow and comment out the return to test
                    currentVersion = SemanticVersion.Parse("0.6.6");
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
                SemanticVersion? targetVersion = null;

                // Check if beta releases should be included
                bool includeBeta = Models.Settings.Instance.ReceiveBetaUpdates;

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

                    if (!SemanticVersion.TryParse(versionString, out var releaseVersion))
                    {
                        Log.Warning($"Could not parse version from tag: {release.TagName}");
                        continue;
                    }

                    // Skip if this version is not what we're looking for based on includeOnlyRecentUpdate
                    if (targetVersion != null)
                    {
                        if (releaseVersion.CompareTo(currentVersion) <= 0)
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

                    Log.Information($"Added release notes for version {versionString}");

                    // Limit to 20 releases if we're not filtering by version
                    if (targetVersion == null && releaseNotesList.Count >= 20)
                    {
                        break;
                    }
                }

                // Send release notes to frontend
                _ = MessageUtils.SendFrontendMessage("ReleaseNotes", new
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
            public string Url { get; set; }

            [JsonPropertyName("html_url")]
            public string HtmlUrl { get; set; }

            [JsonPropertyName("tag_name")]
            public string TagName { get; set; }

            public string Name { get; set; }
            public bool Draft { get; set; }
            public bool Prerelease { get; set; }
            public string Body { get; set; }

            [JsonPropertyName("created_at")]
            public DateTime CreatedAt { get; set; }

            [JsonPropertyName("published_at")]
            public DateTime PublishedAt { get; set; }

            [JsonPropertyName("target_commitish")]
            public string TargetCommitish { get; set; }

            [JsonPropertyName("assets_url")]
            public string AssetsUrl { get; set; }

            [JsonPropertyName("upload_url")]
            public string UploadUrl { get; set; }

            public long Id { get; set; }

            [JsonPropertyName("tarball_url")]
            public string TarballUrl { get; set; }

            [JsonPropertyName("zipball_url")]
            public string ZipballUrl { get; set; }

            public List<GitHubAsset> Assets { get; set; }

            public GitHubUser Author { get; set; }

            [JsonPropertyName("node_id")]
            public string NodeId { get; set; }
        }

        // GitHub asset model
        private class GitHubAsset
        {
            public string Url { get; set; }
            public long Id { get; set; }

            [JsonPropertyName("node_id")]
            public string NodeId { get; set; }

            public string Name { get; set; }
            public string Label { get; set; }

            [JsonPropertyName("content_type")]
            public string ContentType { get; set; }

            public string State { get; set; }
            public long Size { get; set; }

            [JsonPropertyName("download_count")]
            public int DownloadCount { get; set; }

            [JsonPropertyName("created_at")]
            public DateTime CreatedAt { get; set; }

            [JsonPropertyName("updated_at")]
            public DateTime UpdatedAt { get; set; }

            [JsonPropertyName("browser_download_url")]
            public string BrowserDownloadUrl { get; set; }

            public GitHubUser Uploader { get; set; }
        }

        // GitHub user model
        private class GitHubUser
        {
            public string Login { get; set; }
            public long Id { get; set; }

            [JsonPropertyName("node_id")]
            public string NodeId { get; set; }

            [JsonPropertyName("avatar_url")]
            public string AvatarUrl { get; set; }

            [JsonPropertyName("html_url")]
            public string HtmlUrl { get; set; }

            public string Type { get; set; }

            [JsonPropertyName("site_admin")]
            public bool SiteAdmin { get; set; }
        }
    }
}