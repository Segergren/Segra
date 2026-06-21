using System.Linq;
using Segra.Backend.Core;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Games
{
    // The recording settings that actually apply to a given recording, after overlaying any
    // per-game overrides on top of the global settings. Resolved once at record start.
    public class EffectiveRecordingSettings
    {
        public string Resolution { get; set; } = "1080p";
        public int FrameRate { get; set; } = 60;
        public string RateControl { get; set; } = "VBR";
        public int CrfValue { get; set; } = 23;
        public int CqLevel { get; set; } = 20;
        public int Bitrate { get; set; } = 50;
        public int MinBitrate { get; set; } = 40;
        public int MaxBitrate { get; set; } = 70;
        public string Encoder { get; set; } = "gpu";
        public Codec? Codec { get; set; }
        public RecordingMode RecordingMode { get; set; } = RecordingMode.Hybrid;
        public int ReplayBufferDuration { get; set; } = 30;
        public int ReplayBufferMaxSize { get; set; } = 1000;
        public bool DiscardSessionsWithoutBookmarks { get; set; }
    }

    public static class GameSettingsService
    {
        /// <summary>
        /// Finds the per-game settings entry whose executable patterns match the given path, or null.
        /// </summary>
        public static GameSetting? FindForExePath(string? exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return null;

            foreach (var game in Settings.Instance.Games)
            {
                if (game.Paths.Any(path => GameUtils.MatchesExePattern(exePath, path)))
                    return game;
            }

            return null;
        }

        /// <summary>
        /// Resolves the effective recording settings for the game at the given exe path by overlaying
        /// any per-game overrides (quality / recording mode / discard) on top of the global settings.
        /// </summary>
        public static EffectiveRecordingSettings Resolve(string? exePath)
        {
            var s = Settings.Instance;
            var eff = new EffectiveRecordingSettings
            {
                Resolution = s.Resolution,
                FrameRate = s.FrameRate,
                RateControl = s.RateControl,
                CrfValue = s.CrfValue,
                CqLevel = s.CqLevel,
                Bitrate = s.Bitrate,
                MinBitrate = s.MinBitrate,
                MaxBitrate = s.MaxBitrate,
                Encoder = s.Encoder,
                Codec = s.Codec,
                RecordingMode = s.RecordingMode,
                ReplayBufferDuration = s.ReplayBufferDuration,
                ReplayBufferMaxSize = s.ReplayBufferMaxSize,
                DiscardSessionsWithoutBookmarks = s.DiscardSessionsWithoutBookmarks
            };

            var match = FindForExePath(exePath);
            if (match == null) return eff;

            ApplyQualityOverride(eff, match.QualityOverride);

            if (match.RecordingModeOverride != null)
            {
                eff.RecordingMode = match.RecordingModeOverride.RecordingMode;
                eff.ReplayBufferDuration = match.RecordingModeOverride.ReplayBufferDuration;
                eff.ReplayBufferMaxSize = match.RecordingModeOverride.ReplayBufferMaxSize;
            }

            if (match.DiscardSessionsWithoutBookmarksOverride.HasValue)
            {
                eff.DiscardSessionsWithoutBookmarks = match.DiscardSessionsWithoutBookmarksOverride.Value;
            }

            return eff;
        }

        private static void ApplyQualityOverride(EffectiveRecordingSettings eff, GameQualityOverride? quality)
        {
            if (quality == null) return;

            if (string.Equals(quality.Preset, "custom", StringComparison.OrdinalIgnoreCase))
            {
                eff.Resolution = quality.Resolution;
                eff.FrameRate = quality.FrameRate;
                eff.RateControl = quality.RateControl;
                eff.CrfValue = quality.CrfValue;
                eff.CqLevel = quality.CqLevel;
                eff.Bitrate = quality.Bitrate;
                eff.MinBitrate = quality.MinBitrate;
                eff.MaxBitrate = quality.MaxBitrate;
                eff.Encoder = quality.Encoder;
                if (quality.Codec != null) eff.Codec = quality.Codec;
                return;
            }

            // Named preset: resolve concrete values from the same source as the global preset.
            // The codec is not part of a preset, so it stays the global codec (already set on eff).
            var values = PresetsService.GetVideoPresetValues(quality.Preset, PresetsService.IsAmdEncoder(eff.Codec));
            if (values == null) return;

            eff.Resolution = values.Resolution;
            eff.FrameRate = values.FrameRate;
            eff.RateControl = values.RateControl;
            eff.CqLevel = values.CqLevel;
            eff.Bitrate = values.Bitrate;
            eff.MinBitrate = values.MinBitrate;
            eff.MaxBitrate = values.MaxBitrate;
            eff.Encoder = values.Encoder;
        }
    }
}
