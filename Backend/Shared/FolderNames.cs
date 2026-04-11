using Segra.Backend.Core.Models;

namespace Segra.Backend.Shared
{
    /// <summary>
    /// Centralized folder name constants and path helpers.
    /// This is the single source of truth for all folder names used in the application.
    /// </summary>
    public static class FolderNames
    {
        // Video content folder names
        public const string Sessions = "Full Sessions";
        public const string Buffers = "Replay Buffers";
        public const string Clips = "Clips";
        public const string Highlights = "Highlights";

        // Legacy folder names (for migration purposes)
        public const string LegacySessions = "sessions";
        public const string LegacyBuffers = "buffers";
        public const string LegacyClips = "clips";
        public const string LegacyHighlights = "highlights";

        // Metadata folder names (stored in AppData)
        public const string Metadata = "metadata";
        public const string Thumbnails = "thumbnails";
        public const string Waveforms = "waveforms";

        // Legacy metadata folder names (with dot prefix, for migration)
        public const string LegacyMetadata = ".metadata";
        public const string LegacyThumbnails = ".thumbnails";
        public const string LegacyWaveforms = ".waveforms";

        /// <summary>
        /// Gets the cache folder path for Segra (metadata, thumbnails, waveforms).
        /// This is configurable via Settings, with default at C:\Users\{user}\AppData\Roaming\Segra
        /// </summary>
        public static string CacheFolder => Settings.Instance.CacheFolder.Replace("/", "\\");

        /// <summary>
        /// Gets the video folder name for a content type.
        /// </summary>
        public static string GetVideoFolderName(Content.ContentType type)
        {
            return type switch
            {
                Content.ContentType.Session => Sessions,
                Content.ContentType.Buffer => Buffers,
                Content.ContentType.Clip => Clips,
                Content.ContentType.Highlight => Highlights,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown content type")
            };
        }

        /// <summary>
        /// Gets the legacy video folder name for a content type (for migration purposes).
        /// </summary>
        public static string GetLegacyVideoFolderName(Content.ContentType type)
        {
            return type switch
            {
                Content.ContentType.Session => LegacySessions,
                Content.ContentType.Buffer => LegacyBuffers,
                Content.ContentType.Clip => LegacyClips,
                Content.ContentType.Highlight => LegacyHighlights,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown content type")
            };
        }

        /// <summary>
        /// Gets the metadata subfolder name for a content type (e.g., "Full Sessions", "Clips").
        /// Uses the same names as video folders for consistency.
        /// </summary>
        public static string GetMetadataSubfolderName(Content.ContentType type)
        {
            return GetVideoFolderName(type);
        }

        /// <summary>
        /// Gets the legacy metadata subfolder name for a content type (e.g., "sessions", "clips").
        /// </summary>
        public static string GetLegacyMetadataSubfolderName(Content.ContentType type)
        {
            return GetLegacyVideoFolderName(type);
        }

        /// <summary>
        /// Gets the full path to a video folder for a content type.
        /// </summary>
        public static string GetVideoFolderPath(string contentFolder, Content.ContentType type)
        {
            return Path.Combine(contentFolder, GetVideoFolderName(type));
        }

        /// <summary>
        /// Gets the full path to the metadata folder for a content type.
        /// Metadata is stored in AppData/Roaming/Segra/metadata/{ContentType}
        /// </summary>
        public static string GetMetadataFolderPath(Content.ContentType type)
        {
            return Path.Combine(CacheFolder, Metadata, GetMetadataSubfolderName(type));
        }

        /// <summary>
        /// Gets the full path to the thumbnails folder for a content type.
        /// Thumbnails are stored in AppData/Roaming/Segra/thumbnails/{ContentType}
        /// </summary>
        public static string GetThumbnailsFolderPath(Content.ContentType type)
        {
            return Path.Combine(CacheFolder, Thumbnails, GetMetadataSubfolderName(type));
        }

        /// <summary>
        /// Gets the full path to the waveforms folder for a content type.
        /// Waveforms are stored in AppData/Roaming/Segra/waveforms/{ContentType}
        /// </summary>
        public static string GetWaveformsFolderPath(Content.ContentType type)
        {
            return Path.Combine(CacheFolder, Waveforms, GetMetadataSubfolderName(type));
        }

        /// <summary>
        /// Checks if a path contains a specific content type folder.
        /// Useful for determining content type from a file path.
        /// </summary>
        public static bool PathContainsContentType(string path, Content.ContentType type)
        {
            string normalizedPath = path.Replace("\\", "/");
            string folderName = GetVideoFolderName(type);

            // Check for both forward slash variants to handle different path formats
            return normalizedPath.Contains($"/{folderName}/", StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.Contains($"/{folderName}", StringComparison.OrdinalIgnoreCase) &&
                   normalizedPath.EndsWith($"/{folderName}", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Tries to determine the content type from a file path.
        /// </summary>
        public static Content.ContentType? GetContentTypeFromPath(string path)
        {
            string normalizedPath = path.Replace("\\", "/").ToLower();

            // Check new folder names first
            if (normalizedPath.Contains($"/{Sessions.ToLower()}/"))
                return Content.ContentType.Session;
            if (normalizedPath.Contains($"/{Buffers.ToLower()}/"))
                return Content.ContentType.Buffer;
            if (normalizedPath.Contains($"/{Clips.ToLower()}/"))
                return Content.ContentType.Clip;
            if (normalizedPath.Contains($"/{Highlights.ToLower()}/"))
                return Content.ContentType.Highlight;

            // Check legacy folder names for backwards compatibility
            if (normalizedPath.Contains($"/{LegacySessions}/"))
                return Content.ContentType.Session;
            if (normalizedPath.Contains($"/{LegacyBuffers}/"))
                return Content.ContentType.Buffer;
            if (normalizedPath.Contains($"/{LegacyClips}/"))
                return Content.ContentType.Clip;
            if (normalizedPath.Contains($"/{LegacyHighlights}/"))
                return Content.ContentType.Highlight;

            return null;
        }
    }
}
