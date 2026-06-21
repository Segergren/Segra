using Serilog;
using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Windows.Display;

namespace Segra.Backend.Core
{
    // Concrete video quality values produced by a named preset (low/standard/high).
    public class VideoPresetValues
    {
        public required string Resolution { get; init; }
        public required int FrameRate { get; init; }
        public required string RateControl { get; init; }
        public required int CqLevel { get; init; }
        public required int Bitrate { get; init; }
        public required int MinBitrate { get; init; }
        public required int MaxBitrate { get; init; }
        public required string Encoder { get; init; }
    }

    public static class PresetsService
    {
        /// <summary>
        /// Resolves the concrete video quality values for a named preset.
        /// Returns null for "custom" or unknown presets (callers should use explicit values).
        /// This is the single source of truth shared by the global preset and per-game quality overrides.
        /// </summary>
        public static VideoPresetValues? GetVideoPresetValues(string presetName, bool isAmd)
        {
            switch (presetName.ToLower())
            {
                case "low":
                    return new VideoPresetValues
                    {
                        Resolution = "720p",
                        FrameRate = 30,
                        RateControl = "VBR",
                        CqLevel = isAmd ? 22 : 24,
                        Bitrate = isAmd ? 20 : 15,
                        MinBitrate = 10,
                        MaxBitrate = isAmd ? 20 : 15,
                        Encoder = "gpu"
                    };

                case "standard":
                    return new VideoPresetValues
                    {
                        Resolution = "1080p",
                        FrameRate = 60,
                        RateControl = "VBR",
                        CqLevel = isAmd ? 20 : 22,
                        Bitrate = isAmd ? 40 : 30,
                        MinBitrate = isAmd ? 25 : 20,
                        MaxBitrate = isAmd ? 50 : 40,
                        Encoder = "gpu"
                    };

                case "high":
                    return new VideoPresetValues
                    {
                        Resolution = DisplayService.HasDisplayWithMinHeight(1440) ? "1440p" : "1080p",
                        FrameRate = 60,
                        RateControl = "VBR",
                        CqLevel = isAmd ? 18 : 20,
                        Bitrate = isAmd ? 60 : 50,
                        MinBitrate = isAmd ? 45 : 40,
                        MaxBitrate = isAmd ? 90 : 70,
                        Encoder = "gpu"
                    };

                default:
                    return null;
            }
        }

        /// <summary>
        /// Returns true if the global codec is an AMD (AMF) encoder.
        /// </summary>
        public static bool IsAmdEncoder(Codec? codec)
        {
            return codec != null && codec.InternalEncoderId.Contains("amf", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Applies a video quality preset to the settings
        /// </summary>
        public static async Task ApplyVideoPreset(string presetName)
        {
            var settings = Settings.Instance;
            settings.BeginBulkUpdate();
            bool isAmd = IsAmdEncoder();
            bool applied = false;

            try
            {
                switch (presetName.ToLower())
                {
                    case "low":
                    case "standard":
                    case "high":
                        var values = GetVideoPresetValues(presetName, isAmd)!;
                        settings.VideoQualityPreset = presetName.ToLower();
                        settings.Resolution = values.Resolution;
                        settings.FrameRate = values.FrameRate;
                        settings.RateControl = values.RateControl;
                        settings.CqLevel = values.CqLevel;
                        settings.Bitrate = values.Bitrate;
                        settings.MinBitrate = values.MinBitrate;
                        settings.MaxBitrate = values.MaxBitrate;
                        settings.Encoder = values.Encoder;
                        break;

                    case "custom":
                        settings.VideoQualityPreset = "custom";
                        break;

                    default:
                        Log.Warning($"Unknown video preset: {presetName}");
                        return;
                }

                Log.Information("Applied video preset '{Preset}': {Resolution}, {FrameRate}fps, {RateControl}, {Encoder}",
                    settings.VideoQualityPreset, settings.Resolution, settings.FrameRate, settings.RateControl, settings.Encoder);
                applied = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to apply video preset");
            }
            finally
            {
                settings.EndBulkUpdateAndSaveSettings();
            }

            if (applied)
            {
                await MessageService.SendSettingsToFrontend("Video preset applied");
            }
        }

        /// <summary>
        /// Applies a clip quality preset to the settings
        /// </summary>
        public static async Task ApplyClipPreset(string presetName)
        {
            var settings = Settings.Instance;
            settings.BeginBulkUpdate();
            bool applied = false;

            try
            {
                switch (presetName.ToLower())
                {
                    case "low":
                        settings.ClipQualityPreset = "low";
                        settings.ClipEncoder = "cpu";
                        settings.ClipQualityCpu = 28;
                        settings.ClipCodec = "h264";
                        settings.ClipFps = 30;
                        settings.ClipAudioQuality = "96k";
                        settings.ClipPreset = "ultrafast";
                        break;

                    case "standard":
                        settings.ClipQualityPreset = "standard";
                        settings.ClipEncoder = "cpu";
                        settings.ClipQualityCpu = 23;
                        settings.ClipCodec = "h264";
                        settings.ClipFps = 60;
                        settings.ClipAudioQuality = "128k";
                        settings.ClipPreset = "veryfast";
                        break;

                    case "high":
                        settings.ClipQualityPreset = "high";
                        settings.ClipEncoder = "cpu";
                        settings.ClipQualityCpu = 20;
                        settings.ClipCodec = "h264";
                        settings.ClipFps = 60;
                        settings.ClipAudioQuality = "192k";
                        settings.ClipPreset = "medium";
                        break;

                    case "custom":
                        settings.ClipQualityPreset = "custom";
                        break;

                    default:
                        Log.Warning($"Unknown clip preset: {presetName}");
                        return;
                }

                Log.Information("Applied clip preset '{Preset}': {Encoder}, CRF {Quality}, {Codec}, {Fps}fps, {Audio} audio, {EncoderPreset}",
                    settings.ClipQualityPreset, settings.ClipEncoder, settings.ClipQualityCpu, settings.ClipCodec, settings.ClipFps, settings.ClipAudioQuality, settings.ClipPreset);
                applied = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to apply clip preset");
            }
            finally
            {
                settings.EndBulkUpdateAndSaveSettings();
            }

            if (applied)
            {
                await MessageService.SendSettingsToFrontend("Clip preset applied");
            }
        }

        private static bool IsAmdEncoder()
        {
            return IsAmdEncoder(Settings.Instance.Codec);
        }
    }
}
