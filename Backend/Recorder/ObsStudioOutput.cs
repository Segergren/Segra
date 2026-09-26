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
                var processes = Process.GetProcessesByName("obs64");
                bool running = processes.Length > 0;
                foreach (var process in processes) process.Dispose();
                if (!running) return false;

                string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "logs");
                if (!Directory.Exists(logDir)) return false;

                // Session logs are named by start time in a fixed format, not the system locale (GenerateTimeDateFilename
                // in obs-studio frontend/OBSApp.cpp), so the ordinal max is the running session's log
                string? log = Directory.EnumerateFiles(logDir, "????-??-?? ??-??-??.txt").Max(StringComparer.Ordinal);
                if (log == null) return false;

                using var reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
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
    }
}
