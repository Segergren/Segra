using Microsoft.Win32;
using Serilog;
using System.Text.Json;
using System.Text.Json.Nodes;
using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Platform;

namespace Segra.Backend.Media
{
    /// <summary>
    /// Exports a video and its bookmarks as an OpenTimelineIO timeline that DaVinci Resolve
    /// can import via File > Import > Timeline. Markers are attached to the clip.
    /// </summary>
    internal static class DaVinciExportService
    {
        private static readonly Lazy<bool> _davinciInstalled = new(DetectDaVinci);
        public static bool IsDaVinciInstalled => _davinciInstalled.Value;

        private static bool DetectDaVinci()
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    return File.Exists("/opt/resolve/bin/resolve");
                }

                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (File.Exists(Path.Combine(programFiles, "Blackmagic Design", "DaVinci Resolve", "Resolve.exe")))
                {
                    return true;
                }

                // Custom install locations still register an uninstall entry
                using var uninstall = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (string name in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var key = uninstall!.OpenSubKey(name);
                    // Exact names, since the installer also adds e.g. "DaVinci Resolve Control Panels" that outlive an uninstall
                    if (key?.GetValue("DisplayName") is "DaVinci Resolve" or "DaVinci Resolve Studio")
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not detect DaVinci Resolve: {ex.Message}");
            }
            return false;
        }

        // The export points at the video's path, so it goes stale when the video is renamed, moved or deleted
        public static void DeleteDaVinciExport(string videoPath)
        {
            try
            {
                string otioPath = Path.ChangeExtension(videoPath, ".otio");
                if (File.Exists(otioPath))
                {
                    File.Delete(otioPath);
                    Log.Information($"Deleted Resolve export {otioPath}");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not delete Resolve export for {videoPath}: {ex.Message}");
            }
        }

        public static async Task HandleExportMarkers(JsonElement message)
        {
            try
            {
                if (!message.TryGetProperty("Id", out JsonElement idElement))
                {
                    Log.Error("Id parameter not found in ExportMarkers message");
                    return;
                }

                string? contentId = idElement.GetString();
                var content = AppState.Instance.Content.FirstOrDefault(c => c.Id == contentId);
                if (content == null)
                {
                    Log.Error($"Content item not found for {contentId}");
                    return;
                }

                var bookmarks = content.Bookmarks?.OrderBy(b => b.Time).ToList();
                if (bookmarks == null || bookmarks.Count == 0)
                {
                    await MessageService.ShowModal("No Markers", "This video has no bookmarks to export.", "info");
                    return;
                }

                if (!File.Exists(content.FilePath))
                {
                    await MessageService.ShowModal("Export Failed", "The video file could not be found.", "error");
                    return;
                }

                var (fps, duration) = await ProbeVideo(content);
                string title = !string.IsNullOrWhiteSpace(content.Title) ? content.Title : Path.GetFileNameWithoutExtension(content.FileName);
                string otio = GenerateOtio(title, Path.GetFullPath(content.FilePath), bookmarks, fps, duration);

                string otioPath = Path.ChangeExtension(content.FilePath, ".otio");
                await File.WriteAllTextAsync(otioPath, otio);
                Log.Information($"Exported {bookmarks.Count} markers at {fps} fps to {otioPath}");

                PlatformServices.Dialogs.OpenFileLocation(otioPath);
                await MessageService.ShowModal(
                    "Exported to Resolve",
                    $"In Resolve, go to File > Import > Timeline and open\n`{Path.GetFullPath(otioPath)}`");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error handling ExportMarkers");
                await MessageService.ShowModal("Export Failed", $"Could not export markers: {ex.Message}", "error");
            }
        }

        internal static string GenerateOtio(string title, string filePath, List<Bookmark> bookmarks, double fps, TimeSpan duration)
        {
            long durationFrames = Math.Max(1, (long)Math.Round(duration.TotalSeconds * fps));
            var markers = new JsonArray();
            foreach (var bookmark in bookmarks)
            {
                long frame = Math.Clamp((long)Math.Round(bookmark.Time.TotalSeconds * fps), 0, durationFrames - 1);
                markers.Add(new JsonObject
                {
                    ["OTIO_SCHEMA"] = "Marker.2",
                    ["name"] = GetMarkerName(bookmark),
                    ["color"] = GetMarkerColor(bookmark.Type),
                    ["marked_range"] = TimeRange(frame, 1, fps),
                    ["comment"] = "",
                    ["metadata"] = new JsonObject(),
                });
            }

            var timeline = new JsonObject
            {
                ["OTIO_SCHEMA"] = "Timeline.1",
                ["name"] = title,
                ["metadata"] = new JsonObject(),
                // Resolve's default timeline start, 01:00:00:00
                ["global_start_time"] = RationalTime(3600 * Math.Round(fps), fps),
                ["tracks"] = new JsonObject
                {
                    ["OTIO_SCHEMA"] = "Stack.1",
                    ["name"] = "tracks",
                    ["metadata"] = new JsonObject(),
                    ["source_range"] = null,
                    ["effects"] = new JsonArray(),
                    ["markers"] = new JsonArray(),
                    ["children"] = new JsonArray
                    {
                        Track("Video 1", "Video", Clip(title, filePath, fps, durationFrames, markers)),
                        Track("Audio 1", "Audio", Clip(title, filePath, fps, durationFrames, new JsonArray())),
                    },
                },
            };

            return timeline.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        private static JsonObject Track(string name, string kind, JsonObject clip) => new()
        {
            ["OTIO_SCHEMA"] = "Track.1",
            ["name"] = name,
            ["kind"] = kind,
            ["metadata"] = new JsonObject(),
            ["source_range"] = null,
            ["effects"] = new JsonArray(),
            ["markers"] = new JsonArray(),
            ["children"] = new JsonArray { clip },
        };

        private static JsonObject Clip(string name, string filePath, double fps, long durationFrames, JsonArray markers) => new()
        {
            ["OTIO_SCHEMA"] = "Clip.1",
            ["name"] = name,
            ["metadata"] = new JsonObject(),
            ["source_range"] = TimeRange(0, durationFrames, fps),
            ["effects"] = new JsonArray(),
            ["markers"] = markers,
            ["media_reference"] = new JsonObject
            {
                ["OTIO_SCHEMA"] = "ExternalReference.1",
                ["name"] = Path.GetFileName(filePath),
                ["target_url"] = filePath,
                ["available_range"] = TimeRange(0, durationFrames, fps),
                ["metadata"] = new JsonObject(),
            },
        };

        private static JsonObject TimeRange(double startFrame, double durationFrames, double fps) => new()
        {
            ["OTIO_SCHEMA"] = "TimeRange.1",
            ["start_time"] = RationalTime(startFrame, fps),
            ["duration"] = RationalTime(durationFrames, fps),
        };

        private static JsonObject RationalTime(double value, double fps) => new()
        {
            ["OTIO_SCHEMA"] = "RationalTime.1",
            ["rate"] = fps,
            ["value"] = value,
        };

        private static async Task<(double Fps, TimeSpan Duration)> ProbeVideo(Content content)
        {
            double? fps = null;
            TimeSpan duration = content.Duration;
            try
            {
                string metadata = await FFmpegService.GetMetadata(content.FilePath);
                fps = FFmpegService.ExtractFps(metadata);
                TimeSpan probed = FFmpegService.ExtractDuration(metadata);
                if (probed > TimeSpan.Zero)
                {
                    duration = probed;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not probe {content.FilePath}: {ex.Message}");
            }

            if (fps is not > 0)
            {
                int fallback = Settings.Instance.FrameRate;
                Log.Warning($"Falling back to {fallback} fps for marker export of {content.FilePath}");
                fps = fallback > 0 ? fallback : 60;
            }

            // ffmpeg prints NTSC rates rounded (59.94), Resolve needs the exact 60000/1001
            double ntsc = Math.Round(fps.Value) * 1000 / 1001;
            return (Math.Abs(fps.Value - ntsc) < 0.01 ? ntsc : fps.Value, duration);
        }

        private static string GetMarkerName(Bookmark bookmark)
        {
            string name = bookmark.Type == BookmarkType.Manual ? "Bookmark" : bookmark.Type.ToString();
            return bookmark.Subtype != null ? $"{name} ({bookmark.Subtype})" : name;
        }

        private static string GetMarkerColor(BookmarkType type)
        {
            var colors = Settings.Instance.DaVinciMarkerColors;
            return type switch
            {
                BookmarkType.Kill => colors.Kill,
                BookmarkType.Death => colors.Death,
                BookmarkType.Assist => colors.Assist,
                BookmarkType.Goal => colors.Goal,
                _ => colors.Manual,
            };
        }
    }
}
