using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Segra.Backend.Games
{
    public static class SteamUtils
    {
        public record SteamAppInfo(int? AppId, string Name);

        // Successes only — misses can be transient (Steam rewrites manifests at launch)
        private static readonly ConcurrentDictionary<string, SteamAppInfo> _appInfoCache = new(StringComparer.OrdinalIgnoreCase);

        public static SteamAppInfo? GetAppInfoFromExePath(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;
            if (_appInfoCache.TryGetValue(exePath, out var cached)) return cached;

            var info = ResolveAppInfo(exePath);
            if (info != null) _appInfoCache.TryAdd(exePath, info);
            return info;
        }

        private static SteamAppInfo? ResolveAppInfo(string exePath)
        {
            try
            {
                string normalized = exePath.Replace("\\", "/");
                var splitAroundCommon = Regex.Split(normalized, "/steamapps/common/", RegexOptions.IgnoreCase);
                if (splitAroundCommon.Length < 2) return null;

                string folder = splitAroundCommon[1].Split('/')[0];
                string prefix = splitAroundCommon[0].TrimEnd('/', '\\');
                if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(folder)) return null;

                string steamAppsDir = prefix + "/steamapps";
                if (!Directory.Exists(steamAppsDir)) return null;

                foreach (string acfFile in Directory.GetFiles(steamAppsDir, "*.acf"))
                {
                    string contents;
                    try { contents = File.ReadAllText(acfFile); }
                    catch { continue; }

                    if (!ExtractAcfField(contents, "installdir").Equals(folder, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string name = ExtractAcfField(contents, "name");
                    int? appId = int.TryParse(ExtractAcfField(contents, "appid"), out int parsed) ? parsed : null;
                    if (appId == null && string.IsNullOrEmpty(name)) return null;
                    return new SteamAppInfo(appId, name);
                }
                return null;
            }
            catch { return null; }
        }

        private static string ExtractAcfField(string acfContent, string key)
        {
            if (string.IsNullOrEmpty(acfContent) || string.IsNullOrEmpty(key)) return string.Empty;
            string pattern = $"\"{key}\"\\s+\"([^\"]+)\"";
            var match = Regex.Match(acfContent, pattern, RegexOptions.IgnoreCase);
            return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : string.Empty;
        }
    }
}
