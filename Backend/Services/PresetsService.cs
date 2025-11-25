using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Windows.Display;
using Serilog;

namespace Segra.Backend.Services
{
    public static class PresetsService
    {
        /// <summary>
        /// Applies a video quality preset to the settings
        /// </summary>
        public static async Task ApplyVideoPreset(string presetName)
        {
            var settings = Settings.Instance;
            settings.BeginBulkUpdate();

            try
            {
                switch (presetName.ToLower())
                {
                    case "low":
                        settings.VideoQualityPreset = "low";
                        settings.Resolution = "720p";
                        settings.FrameRate = 30;
                        settings.RateControl = "CBR";
                        settings.Bitrate = 10;
                        settings.Encoder = "gpu";
                        break;

                    case "standard":
                        settings.VideoQualityPreset = "standard";
                        settings.Resolution = "1080p";
                        settings.FrameRate = 60;
                        settings.RateControl = "VBR";
                        settings.Bitrate = 40;
                        settings.MinBitrate = 40;
                        settings.MaxBitrate = 60;
                        settings.Encoder = "gpu";
                        break;

                    case "high":
                        settings.VideoQualityPreset = "high";
                        settings.Resolution = DisplayService.HasDisplayWithMinHeight(1440) ? "1440p" : "1080p";
                        settings.FrameRate = 60;
                        settings.RateControl = "VBR";
                        settings.Bitrate = 70;
                        settings.MinBitrate = 70;
                        settings.MaxBitrate = 100;
                        settings.Encoder = "gpu";
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

                settings.EndBulkUpdateAndSaveSettings();
                await MessageService.SendSettingsToFrontend("Video preset applied");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to apply video preset");
                settings.EndBulkUpdateAndSaveSettings();
            }
        }

        /// <summary>
        /// Applies a clip quality preset to the settings
        /// </summary>
        public static async Task ApplyClipPreset(string presetName)
        {
            var settings = Settings.Instance;
            settings.BeginBulkUpdate();

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

                settings.EndBulkUpdateAndSaveSettings();
                await MessageService.SendSettingsToFrontend("Clip preset applied");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to apply clip preset");
                settings.EndBulkUpdateAndSaveSettings();
            }
        }
    }
}
