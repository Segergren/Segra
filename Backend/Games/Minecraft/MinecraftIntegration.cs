using Segra.Backend.Core.Models;
using Serilog;
using System.Text.RegularExpressions;

namespace Segra.Backend.Games.Minecraft
{
    /// <summary>
    /// Minecraft Java integration via latest.log tailing.
    /// Captures the local player name from "Setting user:" and bookmarks deaths
    /// matched against chat log death verbs.
    /// </summary>
    internal partial class MinecraftIntegration : LogTailIntegration
    {
        protected override string LogPrefix => "MC";
        private string? _localUser;
        private string? _resolvedLogPath;

        // Minecraft death verbs that appear in chat as "<player> <verb> ..."
        // Trailing "whilst/while ..." qualifiers are dropped so matches survive Mojang's
        // whilst→while wording change and cover both base and .player variants.
        private static readonly string[] DeathVerbs =
        {
            "was slain by", "was shot by", "was killed by", "was blown up by",
            "was pricked to death", "drowned", "blew up", "fell out of the world",
            "hit the ground too hard", "fell from a high place", "starved to death",
            "burned to death", "tried to swim in lava", "was struck by lightning",
            "suffocated in a wall", "withered away", "was squashed", "was impaled",
            "froze to death", "was killed while trying to hurt", "experienced kinetic energy",
            "was poked to death by a sweet berry bush", "was pummeled by",
            "was killed by magic",
            "went up in flames", "walked into fire",
            "was burned to a crisp", "discovered the floor was lava",
            "danger zone due to", "was squished too much",
            "walked into a cactus",
            "fell off a ladder", "fell off some vines", "fell off some weeping vines",
            "fell off some twisting vines", "fell off scaffolding", "fell while climbing",
            "fell too far and was finished by", "was doomed to fall",
            "was skewered by a falling stalactite", "went off with a bang",
            "was roasted in dragon's breath", "was stung to death",
            "died from dehydration", "left the confines of this world",
            "was frozen to death by", "was smashed by",
            "was fireballed by", "didn't want to live in the same world as",
            "was obliterated by a sonically-charged shriek", "was speared by",
            "was killed while fighting", "died because of"
        };

        [GeneratedRegex(@"Setting user:\s*(?<user>.+?)\s*$")]
        private static partial Regex SettingUserRegex();

        protected override string? ResolveLogPath()
        {
            if (_resolvedLogPath != null && File.Exists(_resolvedLogPath))
                return _resolvedLogPath;

            // Vanilla .minecraft\logs\latest.log
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var candidates = new List<string>
            {
                Path.Combine(appData, ".minecraft", "logs", "latest.log"),
            };

            // Lunar Client
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(userProfile, ".lunarclient", "logs", "client", "latest.log"));

            Log.Debug($"Minecraft: checking {candidates.Count} candidate log paths");

            string? best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (var c in candidates)
            {
                try
                {
                    if (!File.Exists(c))
                    {
                        Log.Debug($"Minecraft: candidate not found: {c}");
                        continue;
                    }
                    var t = File.GetLastWriteTimeUtc(c);
                    Log.Debug($"Minecraft: candidate found: {c} (last write {t:O})");
                    if (t > bestTime)
                    {
                        bestTime = t;
                        best = c;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"Minecraft: failed to stat {c}: {ex.Message}");
                }
            }

            if (best == null)
            {
                Log.Debug("Minecraft: no log file resolved yet");
            }
            else
            {
                Log.Information($"Minecraft: resolved log path -> {best}");
            }

            _resolvedLogPath = best;
            return _resolvedLogPath;
        }

        protected override void OnLogOpened(string path)
        {
            Log.Information($"Minecraft: opened log {path}, scanning for local user");
            _localUser = null;

            // The tail loop starts at end-of-file, so "Setting user:" (logged early in
            // startup, often before Segra attaches) would otherwise be missed. Scan the
            // existing contents once to recover it.
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var match = SettingUserRegex().Match(line);
                    if (match.Success)
                    {
                        _localUser = match.Groups["user"].Value.Trim();
                        Log.Information($"Minecraft local user captured from existing log: {_localUser}");
                        break;
                    }
                }
                if (_localUser == null)
                {
                    Log.Debug("Minecraft: no 'Setting user:' line found in existing log content");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Minecraft: failed to pre-scan log for user: {ex.Message}");
            }
        }

        protected override void ProcessLine(string line)
        {
            if (_localUser == null)
            {
                var match = SettingUserRegex().Match(line);
                if (match.Success)
                {
                    _localUser = match.Groups["user"].Value.Trim();
                    Log.Information($"Minecraft local user captured: {_localUser}");
                    return;
                }
            }

            if (_localUser == null) return;

            int chatIdx = line.IndexOf("[CHAT]", StringComparison.Ordinal);
            if (chatIdx < 0) return;

            string content = line[(chatIdx + 6)..];

            if (!content.Contains(_localUser, StringComparison.Ordinal)) return;

            Log.Debug($"Minecraft: chat line mentions local user: {content.Trim()}");

            foreach (var verb in DeathVerbs)
            {
                if (content.Contains(verb, StringComparison.OrdinalIgnoreCase))
                {
                    // Only bookmark if user precedes the verb (not killer position after "by")
                    int userIdx = content.IndexOf(_localUser, StringComparison.Ordinal);
                    int verbIdx = content.IndexOf(verb, StringComparison.OrdinalIgnoreCase);
                    if (userIdx >= 0 && userIdx < verbIdx)
                    {
                        Log.Information($"Minecraft death detected (verb '{verb}'): {content.Trim()}");
                        AddBookmark(BookmarkType.Death);
                        return;
                    }
                    else
                    {
                        Log.Debug($"Minecraft: matched verb '{verb}' but user is after it (likely the killer), skipping");
                    }
                }
            }
        }
    }
}
