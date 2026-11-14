using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Segra.Backend.Games
{
    /// <param name="Time">Time since the start of the timeline.</param>
    /// <param name="Icon">Icon name the game gave the marker, the same in every game language.</param>
    internal record SteamTimelineEvent(TimeSpan Time, string Title, string Description, string Icon, int Priority, TimeSpan Duration);

    /// <param name="Start">Local time the timeline started at.</param>
    internal record SteamTimeline(DateTime Start, IReadOnlyList<SteamTimelineEvent> Events);

    /// <summary>A timeline Steam is collecting markers for.</summary>
    /// <param name="Started">Local time the timeline started at, in whole seconds.</param>
    /// <param name="File">The file Steam writes the timeline to when it ends.</param>
    internal record SteamTimelineFile(string Id, DateTime Started, string File);

    /// <summary>
    /// Reads the markers a game sent to Steam through ISteamTimeline, the ones Steam Game Recording shows on its timeline.
    ///
    /// Steam collects them for every game, also with its own recording turned off, but gives other programs no way to
    /// ask for them. While the game runs, its timeline is listed in userdata\&lt;user&gt;\gamerecordings\gamerecording.pb.
    /// A few seconds after the game closes, Steam writes the timeline to timelines\&lt;id&gt;.json and, with recording off,
    /// deletes it again within milliseconds.
    ///
    /// To keep the data, Steam's file is made a hard link to a file of ours before Steam writes it. Steam creates the
    /// file with CREATE_ALWAYS, which overwrites the linked file in place, and its delete only removes its own name.
    ///
    /// Event times are milliseconds since the start of the timeline and "endtime" is the moment the file was written,
    /// so the file's write time minus "endtime" is when the timeline started.
    /// </summary>
    internal static class SteamTimelineReader
    {
        private const string KeptSuffix = ".segra";

        // Steam's files that are linked to one of ours right now
        private static readonly HashSet<string> _kept = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the timelines Steam is collecting for the game, oldest first.
        /// </summary>
        public static List<SteamTimelineFile> FindTimelines(int appId) =>
            [.. ReadIndexes().Where(t => t.GameId == (ulong)appId).Select(t => t.Timeline).OrderBy(t => t.Started)];

        /// <summary>
        /// Makes Steam write the timeline into a file that stays after Steam deletes its own. Call while the game runs,
        /// false when the file could not be linked.
        /// </summary>
        public static bool Keep(SteamTimelineFile timeline)
        {
            try
            {
                lock (_kept)
                    _kept.Add(timeline.File);

                Directory.CreateDirectory(Path.GetDirectoryName(timeline.File)!);

                var kept = timeline.File + KeptSuffix;
                if (!File.Exists(kept))
                    File.Create(kept).Dispose();

                // Already there when Segra was restarted during the game, or when Steam keeps the file for its own recording
                return File.Exists(timeline.File) || CreateHardLinkW(timeline.File, kept, IntPtr.Zero);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Reads a kept timeline, null while Steam has not written it.
        /// </summary>
        public static SteamTimeline? Read(SteamTimelineFile timeline)
        {
            foreach (var path in new[] { timeline.File + KeptSuffix, timeline.File })
            {
                try
                {
                    // Steam writes without sharing, so the file only opens once it is complete
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length == 0)
                        continue;

                    var json = new byte[stream.Length];
                    stream.ReadExactly(json);
                    return Parse(json, File.GetLastWriteTime(stream.SafeFileHandle));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                }
            }

            return null;
        }

        /// <summary>
        /// Removes the file made by <see cref="Keep"/>.
        /// </summary>
        public static void Release(SteamTimelineFile timeline)
        {
            Release(timeline.File);
            lock (_kept)
                _kept.Remove(timeline.File);
        }

        /// <summary>
        /// Removes the files of timelines that ended while Segra was not running.
        /// </summary>
        public static void RemoveLeftovers()
        {
            try
            {
                var running = ReadIndexes().Select(t => t.Timeline.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in RecordingFolders())
                {
                    var timelines = Path.Combine(folder, "timelines");
                    if (!Directory.Exists(timelines))
                        continue;

                    foreach (var kept in Directory.GetFiles(timelines, "*" + KeptSuffix))
                    {
                        var file = kept[..^KeptSuffix.Length];
                        bool inUse;
                        lock (_kept)
                            inUse = _kept.Contains(file);
                        if (!inUse && !running.Contains(file))
                            Release(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
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

        private static void Release(string file)
        {
            try
            {
                var kept = file + KeptSuffix;

                // Steam never wrote the timeline, its file is still the empty link
                if (IsEmpty(kept) && IsEmpty(file))
                    File.Delete(file);
                File.Delete(kept);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static bool IsEmpty(string path)
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == 0;
        }

        private static IEnumerable<string> RecordingFolders()
        {
            using var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam?.GetValue("SteamPath") is not string steamPath)
                return [];

            var userdata = Path.Combine(Path.GetFullPath(steamPath), "userdata");
            if (!Directory.Exists(userdata))
                return [];

            return Directory.GetDirectories(userdata).Select(user => Path.Combine(user, "gamerecordings"));
        }

        /// <summary>
        /// Reads the timelines out of every user's gamerecording.pb, a CGameRecordingFile message. Only its field 1
        /// holds them, the tags in field 4 also name timelines that ended long ago.
        /// </summary>
        private static List<(ulong GameId, SteamTimelineFile Timeline)> ReadIndexes()
        {
            var timelines = new List<(ulong, SteamTimelineFile)>();
            foreach (var folder in RecordingFolders())
            {
                try
                {
                    var index = Path.Combine(folder, "gamerecording.pb");
                    if (!File.Exists(index))
                        continue;

                    foreach (var (number, _, metadata) in ReadFields(ReadShared(index)))
                    {
                        if (number != 1)
                            continue;

                        // CGameRecordingTimelineMetadata: 1 = timeline_id, 2 = game_id, 3 = date_recorded
                        var fields = ReadFields(metadata);
                        var id = Encoding.UTF8.GetString(fields.FirstOrDefault(f => f.Number == 1).Bytes.Span);
                        if (id.Length == 0 || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                            continue;

                        var started = DateTimeOffset.FromUnixTimeSeconds((long)fields.FirstOrDefault(f => f.Number == 3).Value).LocalDateTime;
                        var file = Path.Combine(folder, "timelines", id + ".json");
                        timelines.Add((fields.FirstOrDefault(f => f.Number == 2).Value, new SteamTimelineFile(id, started, file)));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentOutOfRangeException)
                {
                    // Steam is rewriting the index, the next look gets it
                }
            }

            return timelines;
        }

        /// <summary>
        /// Splits a protobuf message into its varint and length-delimited fields.
        /// </summary>
        private static List<(int Number, ulong Value, ReadOnlyMemory<byte> Bytes)> ReadFields(ReadOnlyMemory<byte> message)
        {
            var fields = new List<(int, ulong, ReadOnlyMemory<byte>)>();
            var data = message.Span;
            int position = 0;
            while (position < data.Length)
            {
                ulong key = ReadVarint(data, ref position);
                int number = (int)(key >> 3);
                switch (key & 7)
                {
                    case 0:
                        fields.Add((number, ReadVarint(data, ref position), default));
                        break;
                    case 1:
                        position += 8;
                        break;
                    case 2:
                        ulong length = ReadVarint(data, ref position);
                        if (length > (ulong)(data.Length - position))
                            throw new InvalidDataException("Field runs past the end of the message");
                        fields.Add((number, 0, message.Slice(position, (int)length)));
                        position += (int)length;
                        break;
                    case 5:
                        position += 4;
                        break;
                    default:
                        throw new InvalidDataException("Unknown wire type");
                }
            }

            return fields;
        }

        private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int position)
        {
            ulong value = 0;
            for (int shift = 0; shift < 64 && position < data.Length; shift += 7)
            {
                byte part = data[position++];
                value |= (ulong)(part & 0x7F) << shift;
                if ((part & 0x80) == 0)
                    return value;
            }

            throw new InvalidDataException("Unfinished varint");
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
    }
}
