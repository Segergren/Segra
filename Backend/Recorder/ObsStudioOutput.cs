using System.Diagnostics;
using Serilog;

namespace Segra.Backend.Recorder
{
    /// <summary>
    /// Detects an active OBS Studio stream or recording from its session log, which marks every start and stop.
    /// </summary>
    internal static class ObsStudioOutput
    {
        private const string StreamStartMarker = "==== Streaming Start ====";
        private const string StreamStopMarker = "==== Streaming Stop ====";
        private const string RecordStartMarker = "==== Recording Start ====";
        private const string RecordStopMarker = "==== Recording Stop ====";

        public static bool IsStreamingOrRecording()
        {
            try
            {
                string? log = GetRunningSessionLog();
                if (log == null) return false;

                using var reader = OpenLog(log);
                bool streaming = false;
                bool recording = false;
                while (reader.ReadLine() is { } line)
                {
                    if (line.Contains(StreamStartMarker)) streaming = true;
                    else if (line.Contains(StreamStopMarker)) streaming = false;
                    else if (line.Contains(RecordStartMarker)) recording = true;
                    else if (line.Contains(RecordStopMarker)) recording = false;
                }
                return streaming || recording;
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to check OBS Studio output state: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// True when a Game Capture in OBS Studio has tried to hook this game since the given time.
        /// </summary>
        public static bool IsHookingGame(string exeFileName, DateTime since)
        {
            try
            {
                string? log = GetRunningSessionLog();
                if (log == null) return false;

                string marker = $"attempting to hook process: {exeFileName}";
                using var reader = OpenLog(log);
                while (reader.ReadLine() is { } line)
                {
                    // Lines look like "14:01:28.500: [game-capture: 'Game Capture'] attempting to hook process: cs2.exe"
                    if (line.Length > 12 && line.Contains(marker, StringComparison.OrdinalIgnoreCase)
                        && TimeSpan.TryParse(line.AsSpan(0, 12), out var time))
                    {
                        var at = DateTime.Today + time;
                        if (at > DateTime.Now.AddMinutes(1)) at = at.AddDays(-1);
                        if (at >= since) return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to check OBS Studio game hook attempts: {ex.Message}");
                return false;
            }
        }

        private static string? GetRunningSessionLog()
        {
            var processes = Process.GetProcessesByName("obs64");
            bool running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            if (!running) return null;

            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "logs");
            if (!Directory.Exists(logDir)) return null;

            // Session logs are named by start time in a fixed format, not the system locale (GenerateTimeDateFilename
            // in obs-studio frontend/OBSApp.cpp), so the ordinal max is the running session's log
            return Directory.EnumerateFiles(logDir, "????-??-?? ??-??-??.txt").Max(StringComparer.Ordinal);
        }

        private static StreamReader OpenLog(string path) =>
            new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
    }
}
