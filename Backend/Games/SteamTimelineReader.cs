using Microsoft.Win32;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Segra.Backend.Games
{
    /// <param name="Time">Time since the start of the timeline.</param>
    /// <param name="Icon">Icon name the game gave the marker, the same in every game language.</param>
    internal record SteamTimelineEvent(TimeSpan Time, string Title, string Description, string Icon, int Priority, TimeSpan Duration);

    /// <param name="Start">Local time the timeline started at.</param>
    internal record SteamTimeline(DateTime Start, IReadOnlyList<SteamTimelineEvent> Events);

    /// <summary>
    /// Reads the markers a game sent to Steam through ISteamTimeline, the ones Steam Game Recording shows on its timeline.
    ///
    /// Steam collects them for every game, also with its own recording turned off, but gives other programs no way to
    /// ask for them. While the game runs, the name of its timeline is in userdata\&lt;user&gt;\gamerecordings\gamerecording.pb.
    /// About two seconds after the game closes, Steam writes the timeline to timelines\&lt;name&gt;.json and, with recording
    /// off, deletes it again some 15 ms later. The file is opened in that window, sharing delete access so it stays
    /// readable after Steam removes it.
    ///
    /// Event times are milliseconds since the start of the timeline and "endtime" is the moment the file was written,
    /// so the file's creation time minus "endtime" is when the timeline started.
    /// </summary>
    internal static class SteamTimelineReader
    {
        /// <summary>
        /// Returns the file Steam will write the running game's timeline to, or null while Steam has no timeline for it.
        /// </summary>
        public static string? FindTimelineFile(int appId)
        {
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam?.GetValue("SteamPath") is not string steamPath)
                return null;

            var userdata = Path.Combine(steamPath, "userdata");
            if (!Directory.Exists(userdata))
                return null;

            // timeline_<app id><yyyyMMdd>_<HHmmss>, the fixed date length keeps app 48 from matching app 480
            var name = new Regex($@"timeline_{appId}\d{{8}}_\d{{6}}(?!\d)");
            string? newest = null;
            var newestWrite = DateTime.MinValue;

            foreach (var user in Directory.EnumerateDirectories(userdata))
            {
                var folder = Path.Combine(user, "gamerecordings");
                var index = Path.Combine(folder, "gamerecording.pb");
                try
                {
                    if (!File.Exists(index))
                        continue;

                    var match = name.Matches(Encoding.Latin1.GetString(ReadShared(index))).LastOrDefault();
                    var write = File.GetLastWriteTimeUtc(index);
                    if (match == null || write <= newestWrite)
                        continue;

                    newest = Path.Combine(folder, "timelines", match.Value + ".json");
                    newestWrite = write;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Steam is rewriting the index, the next look gets it
                }
            }

            return newest;
        }

        /// <summary>
        /// Waits for Steam to write the timeline file and reads it. Call right after the game closed, on its own
        /// thread: it keeps a core busy until the file shows up. Returns null when it never does.
        /// </summary>
        public static SteamTimeline? Catch(string path, TimeSpan timeout)
        {
            // The file is only there for a few milliseconds, the thread must not be kept waiting for a core
            var priority = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            try
            {
                return WaitAndRead(path, timeout);
            }
            finally
            {
                Thread.CurrentThread.Priority = priority;
            }
        }

        private static SteamTimeline? WaitAndRead(string path, TimeSpan timeout)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                if (!File.Exists(path))
                {
                    Thread.SpinWait(200);
                    continue;
                }

                FileStream stream;
                try
                {
                    stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                using (stream)
                {
                    var openedAt = DateTime.Now;
                    var writtenAt = openedAt;
                    try
                    {
                        var created = File.GetCreationTime(stream.SafeFileHandle);
                        if (created <= openedAt && openedAt - created < TimeSpan.FromSeconds(5))
                            writtenAt = created;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }

                    // Steam can still be writing, the open handle keeps the data after the file is deleted
                    for (int attempt = 0; attempt < 40; attempt++)
                    {
                        try
                        {
                            stream.Position = 0;
                            var json = new byte[stream.Length];
                            stream.ReadExactly(json);
                            return Parse(json, writtenAt);
                        }
                        catch (Exception ex) when (ex is JsonException or IOException)
                        {
                            Thread.Sleep(25);
                        }
                    }

                    return null;
                }
            }

            return null;
        }

        /// <param name="writtenAt">Local time Steam wrote the file at.</param>
        public static SteamTimeline Parse(ReadOnlySpan<byte> json, DateTime writtenAt)
        {
            var reader = new Utf8JsonReader(json);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;

            var end = ReadNumber(root, "endtime") ?? throw new JsonException("Timeline has no endtime");
            var start = writtenAt - TimeSpan.FromMilliseconds(end);

            // "daterecorded" is the start in whole seconds, used when the file time is off
            if (ReadNumber(root, "daterecorded") is long recorded)
            {
                var recordedStart = DateTimeOffset.FromUnixTimeSeconds(recorded).LocalDateTime;
                if (start < recordedStart - TimeSpan.FromSeconds(2) || start > recordedStart + TimeSpan.FromSeconds(3))
                    start = recordedStart + TimeSpan.FromMilliseconds(500);
            }

            var events = new List<SteamTimelineEvent>();
            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (ReadString(entry, "type") != "event" || ReadNumber(entry, "time") is not long time)
                        continue;

                    events.Add(new SteamTimelineEvent(
                        TimeSpan.FromMilliseconds(time),
                        ReadString(entry, "title"),
                        ReadString(entry, "description"),
                        ReadString(entry, "icon"),
                        (int)(ReadNumber(entry, "priority") ?? 0),
                        TimeSpan.FromMilliseconds(ReadNumber(entry, "duration") ?? 0)));
                }
            }

            return new SteamTimeline(start, events);
        }

        private static byte[] ReadShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }

        private static string ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

        // Steam writes times as strings and the rest as numbers
        private static long? ReadNumber(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
                return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number;
            return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed) ? parsed : null;
        }
    }
}
