using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Media;
using Segra.Backend.Recorder;
using Segra.Backend.Shared;
using Segra.Backend.Utils;
using Segra.Backend.Windows.Display;
using Segra.Backend.Windows.Input;
using Serilog;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Segra.Backend.Services
{
    internal static class SettingsService
    {
        public static readonly string SettingsFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra", "settings.json");

        public static void SaveSettings()
        {
            if (Program.hasLoadedInitialSettings == false)
            {
                Log.Error("Program has not loaded initial settings. Can't save!");
                return;
            }

            try
            {
                // Ensure the directory exists
                var directory = Path.GetDirectoryName(SettingsFilePath);
                if (directory != null && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Serialize Settings
                var json = JsonSerializer.Serialize(Settings.Instance, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(SettingsFilePath, json);
                Log.Information($"Settings saved to {SettingsFilePath}");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to save settings: {ex.Message}");
            }
        }

        public static bool LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    Log.Information($"Settings file not found at {SettingsFilePath}. Using default settings.");
                    return false;
                }

                var json = File.ReadAllText(SettingsFilePath);

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                Settings.Instance.BeginBulkUpdate();

                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;

                    foreach (JsonProperty property in root.EnumerateObject())
                    {
                        try
                        {
                            if (property.Value.ValueKind == JsonValueKind.Array)
                            {
                                var propertyName = char.ToUpper(property.Name[0]) + property.Name.Substring(1);
                                var targetProperty = typeof(Settings).GetProperty(
                                    propertyName,
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                                if (targetProperty != null && targetProperty.CanWrite)
                                {
                                    try
                                    {
                                        Type collectionType = targetProperty.PropertyType;

                                        Type elementType = collectionType.IsGenericType ?
                                            collectionType.GetGenericArguments()[0] : typeof(object);

                                        var listType = typeof(List<>).MakeGenericType(elementType);
                                        var validItems = Activator.CreateInstance(listType);

                                        var addMethod = listType.GetMethod("Add");

                                        foreach (JsonElement itemElement in property.Value.EnumerateArray())
                                        {
                                            try
                                            {
                                                var item = JsonSerializer.Deserialize(itemElement.GetRawText(), elementType, options);
                                                if (item != null)
                                                {
                                                    addMethod?.Invoke(validItems, new[] { item });
                                                }
                                            }
                                            catch (Exception itemEx)
                                            {
                                                Log.Warning($"Failed to deserialize an item in {property.Name}: {itemEx.Message}");
                                            }
                                        }

                                        targetProperty.SetValue(Settings.Instance, validItems);
                                    }
                                    catch (Exception collEx)
                                    {
                                        Log.Warning($"Failed to process collection property {property.Name}: {collEx.Message}");
                                    }
                                }
                            }
                            else if (property.Value.ValueKind == JsonValueKind.Object)
                            {
                                var propertyName = char.ToUpper(property.Name[0]) + property.Name.Substring(1);
                                var targetProperty = typeof(Settings).GetProperty(
                                    propertyName,
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                                if (targetProperty != null && targetProperty.CanWrite)
                                {
                                    try
                                    {
                                        var value = JsonSerializer.Deserialize(property.Value.GetRawText(), targetProperty.PropertyType, options);
                                        if (value != null)
                                        {
                                            targetProperty.SetValue(Settings.Instance, value);
                                        }
                                    }
                                    catch (Exception objEx)
                                    {
                                        Log.Warning($"Failed to deserialize object property {property.Name}: {objEx.Message}");
                                    }
                                }
                            }
                            else
                            {
                                var propertyName = char.ToUpper(property.Name[0]) + property.Name.Substring(1);
                                var targetProperty = typeof(Settings).GetProperty(
                                    propertyName,
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                                if (targetProperty != null && targetProperty.CanWrite)
                                {
                                    try
                                    {
                                        var value = JsonSerializer.Deserialize(property.Value.GetRawText(), targetProperty.PropertyType, options);
                                        if (value != null)
                                        {
                                            targetProperty.SetValue(Settings.Instance, value);
                                        }
                                    }
                                    catch (Exception primEx)
                                    {
                                        Log.Warning($"Failed to deserialize primitive property {property.Name}: {primEx.Message}");
                                    }
                                }
                            }
                        }
                        catch (Exception propEx)
                        {
                            Log.Warning($"Error processing property {property.Name}: {propEx.Message}");
                        }
                    }
                }

                Settings.Instance.RunOnStartup = StartupService.GetStartupStatus();
                Settings.Instance.State.GpuVendor = GeneralUtils.DetectGpuVendor();

                Log.Information("Settings loaded from {0}", SettingsFilePath);

                Settings.Instance.EndBulkUpdateAndSaveSettings();
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load settings: {ex.Message}");
                return false;
            }
        }

        public static async Task HandleUpdateSettings(JsonElement settingsElement)
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
                };

                // Deserialize the settings from the parameters
                var updatedSettings = JsonSerializer.Deserialize<Settings>(settingsElement.GetRawText(), options);

                if (updatedSettings != null)
                {
                    await UpdateSettingsInstance(updatedSettings);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to update settings: {ex.Message}");
            }
        }

        private static async Task UpdateSettingsInstance(Settings updatedSettings)
        {
            var settings = Settings.Instance;
            bool hasChanges = false;

            // Begin bulk update to suppress multiple state updates
            settings.BeginBulkUpdate();

            // Update ClipClearSelectionsAfterCreatingClip
            if (settings.ClipClearSelectionsAfterCreatingClip != updatedSettings.ClipClearSelectionsAfterCreatingClip)
            {
                Log.Information($"ClipClearSelectionsAfterCreatingClip changed from '{settings.ClipClearSelectionsAfterCreatingClip}' to '{updatedSettings.ClipClearSelectionsAfterCreatingClip}'");
                settings.ClipClearSelectionsAfterCreatingClip = updatedSettings.ClipClearSelectionsAfterCreatingClip;
                hasChanges = true;
            }

            // Update ClipEncoder
            bool hasAutoSelectedClipCodec = false;
            if (settings.ClipEncoder != updatedSettings.ClipEncoder)
            {
                Log.Information($"ClipEncoder changed from '{settings.ClipEncoder}' to '{updatedSettings.ClipEncoder}'");
                settings.ClipEncoder = updatedSettings.ClipEncoder;

                Log.Information($"Automatically changing ClipCodec to 'h264' due to ClipEncoder change");
                settings.ClipCodec = "h264";
                hasAutoSelectedClipCodec = true;

                hasChanges = true;
            }

            // Update ClipShowInBrowserAfterUpload
            if (settings.ClipShowInBrowserAfterUpload != updatedSettings.ClipShowInBrowserAfterUpload)
            {
                Log.Information($"ClipShowInBrowserAfterUpload changed from '{settings.ClipShowInBrowserAfterUpload}' to '{updatedSettings.ClipShowInBrowserAfterUpload}'");
                settings.ClipShowInBrowserAfterUpload = updatedSettings.ClipShowInBrowserAfterUpload;
                hasChanges = true;
            }

            // Update ClipQualityCpu
            if (settings.ClipQualityCpu != updatedSettings.ClipQualityCpu)
            {
                Log.Information($"ClipQualityCpu changed from '{settings.ClipQualityCpu}' to '{updatedSettings.ClipQualityCpu}'");
                settings.ClipQualityCpu = updatedSettings.ClipQualityCpu;
                hasChanges = true;
            }

            // Update ClipQualityGpu
            if (settings.ClipQualityGpu != updatedSettings.ClipQualityGpu)
            {
                Log.Information($"ClipQualityGpu changed from '{settings.ClipQualityGpu}' to '{updatedSettings.ClipQualityGpu}'");
                settings.ClipQualityGpu = updatedSettings.ClipQualityGpu;
                hasChanges = true;
            }

            // Update ClipCodec
            if (settings.ClipCodec != updatedSettings.ClipCodec && !hasAutoSelectedClipCodec)
            {
                Log.Information($"ClipCodec changed from '{settings.ClipCodec}' to '{updatedSettings.ClipCodec}'");
                settings.ClipCodec = updatedSettings.ClipCodec;
                hasChanges = true;
            }

            // Update ClipFps
            if (settings.ClipFps != updatedSettings.ClipFps)
            {
                Log.Information($"ClipFps changed from '{settings.ClipFps}' to '{updatedSettings.ClipFps}'");
                settings.ClipFps = updatedSettings.ClipFps;
                hasChanges = true;
            }

            // Update ClipAudioQuality
            if (settings.ClipAudioQuality != updatedSettings.ClipAudioQuality)
            {
                Log.Information($"ClipAudioQuality changed from '{settings.ClipAudioQuality}' to '{updatedSettings.ClipAudioQuality}'");
                settings.ClipAudioQuality = updatedSettings.ClipAudioQuality;
                hasChanges = true;
            }

            // Update ClipPreset
            if (settings.ClipPreset != updatedSettings.ClipPreset)
            {
                Log.Information($"ClipPreset changed from '{settings.ClipPreset}' to '{updatedSettings.ClipPreset}'");
                settings.ClipPreset = updatedSettings.ClipPreset;
                hasChanges = true;
            }

            // Update SoundEffectsVolume
            if (settings.SoundEffectsVolume != updatedSettings.SoundEffectsVolume)
            {
                Log.Information($"SoundEffectsVolume changed from '{settings.SoundEffectsVolume}' to '{updatedSettings.SoundEffectsVolume}'");
                settings.SoundEffectsVolume = updatedSettings.SoundEffectsVolume;
                // Play the sound with the new volume to provide immediate feedback
                _ = Task.Run(() => OBSService.PlaySound("start"));
                hasChanges = true;
            }

            // Update ShowNewBadgeOnVideos
            if (settings.ShowNewBadgeOnVideos != updatedSettings.ShowNewBadgeOnVideos)
            {
                Log.Information($"ShowNewBadgeOnVideos changed from '{settings.ShowNewBadgeOnVideos}' to '{updatedSettings.ShowNewBadgeOnVideos}'");
                settings.ShowNewBadgeOnVideos = updatedSettings.ShowNewBadgeOnVideos;
                hasChanges = true;
            }

            // Update ShowGameBackground
            if (settings.ShowGameBackground != updatedSettings.ShowGameBackground)
            {
                Log.Information($"ShowGameBackground changed from '{settings.ShowGameBackground}' to '{updatedSettings.ShowGameBackground}'");
                settings.ShowGameBackground = updatedSettings.ShowGameBackground;
                hasChanges = true;
            }

            // Update ShowAudioWaveformInTimeline
            if (settings.ShowAudioWaveformInTimeline != updatedSettings.ShowAudioWaveformInTimeline)
            {
                Log.Information($"ShowAudioWaveformInTimeline changed from '{settings.ShowAudioWaveformInTimeline}' to '{updatedSettings.ShowAudioWaveformInTimeline}'");
                settings.ShowAudioWaveformInTimeline = updatedSettings.ShowAudioWaveformInTimeline;
                hasChanges = true;
            }

            // Update EnableSeparateAudioTracks
            if (settings.EnableSeparateAudioTracks != updatedSettings.EnableSeparateAudioTracks)
            {
                Log.Information($"EnableSeparateAudioTracks changed from '{settings.EnableSeparateAudioTracks}' to '{updatedSettings.EnableSeparateAudioTracks}'");
                settings.EnableSeparateAudioTracks = updatedSettings.EnableSeparateAudioTracks;
                hasChanges = true;
            }

            // Update VideoQualityPreset
            if (settings.VideoQualityPreset != updatedSettings.VideoQualityPreset)
            {
                Log.Information($"VideoQualityPreset changed from '{settings.VideoQualityPreset}' to '{updatedSettings.VideoQualityPreset}'");
                settings.VideoQualityPreset = updatedSettings.VideoQualityPreset;
                hasChanges = true;
            }

            // Update ClipQualityPreset
            if (settings.ClipQualityPreset != updatedSettings.ClipQualityPreset)
            {
                Log.Information($"ClipQualityPreset changed from '{settings.ClipQualityPreset}' to '{updatedSettings.ClipQualityPreset}'");
                settings.ClipQualityPreset = updatedSettings.ClipQualityPreset;
                hasChanges = true;
            }

            if (settings.RemoveOriginalAfterCompression != updatedSettings.RemoveOriginalAfterCompression)
            {
                Log.Information($"RemoveOriginalAfterCompression changed from '{settings.RemoveOriginalAfterCompression}' to '{updatedSettings.RemoveOriginalAfterCompression}'");
                settings.RemoveOriginalAfterCompression = updatedSettings.RemoveOriginalAfterCompression;
                hasChanges = true;
            }

            // Update DiscardSessionsWithoutBookmarks
            if (settings.DiscardSessionsWithoutBookmarks != updatedSettings.DiscardSessionsWithoutBookmarks)
            {
                Log.Information($"DiscardSessionsWithoutBookmarks changed from '{settings.DiscardSessionsWithoutBookmarks}' to '{updatedSettings.DiscardSessionsWithoutBookmarks}'");
                settings.DiscardSessionsWithoutBookmarks = updatedSettings.DiscardSessionsWithoutBookmarks;
                hasChanges = true;
            }

            // Update GameIntegrations
            if (updatedSettings.GameIntegrations != null)
            {
                var current = settings.GameIntegrations;
                var updated = updatedSettings.GameIntegrations;

                if (current.CounterStrike2.Enabled != updated.CounterStrike2.Enabled)
                {
                    Log.Information($"GameIntegrations.CounterStrike2.Enabled changed from '{current.CounterStrike2.Enabled}' to '{updated.CounterStrike2.Enabled}'");
                    current.CounterStrike2.Enabled = updated.CounterStrike2.Enabled;
                    hasChanges = true;
                }
                if (current.LeagueOfLegends.Enabled != updated.LeagueOfLegends.Enabled)
                {
                    Log.Information($"GameIntegrations.LeagueOfLegends.Enabled changed from '{current.LeagueOfLegends.Enabled}' to '{updated.LeagueOfLegends.Enabled}'");
                    current.LeagueOfLegends.Enabled = updated.LeagueOfLegends.Enabled;
                    hasChanges = true;
                }
                if (current.Pubg.Enabled != updated.Pubg.Enabled)
                {
                    Log.Information($"GameIntegrations.Pubg.Enabled changed from '{current.Pubg.Enabled}' to '{updated.Pubg.Enabled}'");
                    current.Pubg.Enabled = updated.Pubg.Enabled;
                    hasChanges = true;
                }
                if (current.RocketLeague.Enabled != updated.RocketLeague.Enabled)
                {
                    Log.Information($"GameIntegrations.RocketLeague.Enabled changed from '{current.RocketLeague.Enabled}' to '{updated.RocketLeague.Enabled}'");
                    current.RocketLeague.Enabled = updated.RocketLeague.Enabled;
                    hasChanges = true;
                }
            }

            // Update Theme
            if (settings.Theme != updatedSettings.Theme)
            {
                Log.Information($"Theme changed from '{settings.Theme}' to '{updatedSettings.Theme}'");
                settings.Theme = updatedSettings.Theme;
                hasChanges = true;
            }

            // Update ContentFolder
            if (settings.ContentFolder != updatedSettings.ContentFolder)
            {
                Log.Information($"ContentFolder changed from '{settings.ContentFolder}' to '{updatedSettings.ContentFolder}'");

                // Check if the new folder would exceed storage limit
                bool shouldProceed = await StorageWarningService.CheckContentFolderChange(updatedSettings.ContentFolder);
                if (shouldProceed)
                {
                    settings.ContentFolder = updatedSettings.ContentFolder;
                    hasChanges = true;
                }
                // If not proceeding, a warning modal was sent to the frontend
            }

            // Update CacheFolder
            string? oldCacheFolder = null;
            if (settings.CacheFolder != updatedSettings.CacheFolder)
            {
                Log.Information($"CacheFolder changed from '{settings.CacheFolder}' to '{updatedSettings.CacheFolder}'");
                oldCacheFolder = settings.CacheFolder;
                settings.CacheFolder = updatedSettings.CacheFolder;
                hasChanges = true;
            }

            // Update RecordingMode
            if (settings.RecordingMode != updatedSettings.RecordingMode)
            {
                Log.Information($"RecordingMode changed from '{settings.RecordingMode}' to '{updatedSettings.RecordingMode}'");
                settings.RecordingMode = updatedSettings.RecordingMode;
                hasChanges = true;
            }

            // Update ReplayBufferDuration
            if (settings.ReplayBufferDuration != updatedSettings.ReplayBufferDuration)
            {
                Log.Information($"ReplayBufferDuration changed from '{settings.ReplayBufferDuration}' to '{updatedSettings.ReplayBufferDuration}'");
                settings.ReplayBufferDuration = updatedSettings.ReplayBufferDuration;
                hasChanges = true;
            }

            // Update ReplayBufferMaxSize
            if (settings.ReplayBufferMaxSize != updatedSettings.ReplayBufferMaxSize)
            {
                Log.Information($"ReplayBufferMaxSize changed from '{settings.ReplayBufferMaxSize}' to '{updatedSettings.ReplayBufferMaxSize}'");
                settings.ReplayBufferMaxSize = updatedSettings.ReplayBufferMaxSize;
                hasChanges = true;
            }

            // Update Resolution
            if (settings.Resolution != updatedSettings.Resolution)
            {
                Log.Information($"Resolution changed from '{settings.Resolution}' to '{updatedSettings.Resolution}'");
                settings.Resolution = updatedSettings.Resolution;
                hasChanges = true;
            }

            // Update FrameRate
            if (settings.FrameRate != updatedSettings.FrameRate)
            {
                Log.Information($"FrameRate changed from '{settings.FrameRate}' to '{updatedSettings.FrameRate}'");
                settings.FrameRate = updatedSettings.FrameRate;
                hasChanges = true;
            }

            // Update Stretch4By3
            if (settings.Stretch4By3 != updatedSettings.Stretch4By3)
            {
                Log.Information($"Stretch4By3 changed from '{settings.Stretch4By3}' to '{updatedSettings.Stretch4By3}'");
                settings.Stretch4By3 = updatedSettings.Stretch4By3;
                hasChanges = true;
            }

            // Update Bitrate
            if (settings.Bitrate != updatedSettings.Bitrate)
            {
                Log.Information($"Bitrate changed from '{settings.Bitrate} Mbps' to '{updatedSettings.Bitrate} Mbps'");
                settings.Bitrate = updatedSettings.Bitrate;
                hasChanges = true;
            }

            // Update MinBitrate (VBR only)
            if (settings.MinBitrate != updatedSettings.MinBitrate)
            {
                Log.Information($"MinBitrate changed from '{settings.MinBitrate} Mbps' to '{updatedSettings.MinBitrate} Mbps'");
                settings.MinBitrate = updatedSettings.MinBitrate;
                hasChanges = true;
            }

            // Update MaxBitrate (VBR only)
            if (settings.MaxBitrate != updatedSettings.MaxBitrate)
            {
                Log.Information($"MaxBitrate changed from '{settings.MaxBitrate} Mbps' to '{updatedSettings.MaxBitrate} Mbps'");
                settings.MaxBitrate = updatedSettings.MaxBitrate;
                hasChanges = true;
            }

            // Update Encoder
            bool hasAutoSelectedCodec = false;
            bool hasAutoSelectedRateControl = false;
            if (settings.Encoder != updatedSettings.Encoder)
            {
                Log.Information($"Encoder changed from '{settings.Encoder}' to '{updatedSettings.Encoder}'");
                settings.Encoder = updatedSettings.Encoder;

                // When encoder changes, automatically select an appropriate codec
                var newCodec = OBSService.SelectDefaultCodec(settings.Encoder, settings.State.Codecs);
                if (newCodec != null && (settings.Codec == null || !settings.Codec.Equals(newCodec)))
                {
                    Log.Information($"Automatically changing codec to '{newCodec.FriendlyName}' based on encoder change");
                    settings.Codec = newCodec;
                    hasAutoSelectedCodec = true;
                }

                // Ensure CRF is only used with CPU encoder; if user switches to GPU, switch to CQP
                if (settings.Encoder == "gpu" && settings.RateControl == "CRF")
                {
                    Log.Information($"Automatically changing RateControl from 'CRF' to 'CQP' because encoder is GPU");
                    settings.RateControl = "CQP";
                    hasAutoSelectedRateControl = true;
                }
                else if (settings.Encoder == "cpu" && settings.RateControl == "CQP")
                {
                    Log.Information($"Automatically changing RateControl from 'CQP' to 'CRF' because encoder is CPU");
                    settings.RateControl = "CRF";
                    hasAutoSelectedRateControl = true;
                }

                hasChanges = true;
            }

            // Update Codec
            if (settings.Codec != null && updatedSettings.Codec != null && !settings.Codec.Equals(updatedSettings.Codec) && !hasAutoSelectedCodec)
            {
                if (!OBSService.IsInitialized)
                {
                    Log.Warning($"Codec change before OBS initialization, skipping");
                }
                else
                {
                    Log.Information($"Codec changed from '{settings.Codec.FriendlyName}' to '{updatedSettings.Codec.FriendlyName}'");
                    settings.Codec = updatedSettings.Codec;
                    hasChanges = true;
                }
            }

            // Update StorageLimit
            if (settings.StorageLimit != updatedSettings.StorageLimit)
            {
                Log.Information($"StorageLimit changed from '{settings.StorageLimit} GB' to '{updatedSettings.StorageLimit} GB'");
                settings.StorageLimit = updatedSettings.StorageLimit;
                hasChanges = true;
            }

            // Update InputDevice
            if (!settings.InputDevices.SequenceEqual(updatedSettings.InputDevices, new DeviceSettingEqualityComparer()))
            {
                Log.Information($"InputDevice changed from '[{string.Join(", ", settings.InputDevices.Select(d => $"{d.Name}"))}]' to '[{string.Join(", ", updatedSettings.InputDevices.Select(d => $"{d.Name}"))}]'");
                settings.InputDevices = updatedSettings.InputDevices;
                hasChanges = true;
            }

            // Update OutputDevice
            if (!settings.OutputDevices.SequenceEqual(updatedSettings.OutputDevices, new DeviceSettingEqualityComparer()))
            {
                Log.Information($"OutputDevice changed from '[{string.Join(", ", settings.OutputDevices.Select(d => $"{d.Name}"))}]' to '[{string.Join(", ", updatedSettings.OutputDevices.Select(d => $"{d.Name}"))}]'");
                settings.OutputDevices = updatedSettings.OutputDevices;
                hasChanges = true;
            }

            // Update ForceMonoInputSources
            if (settings.ForceMonoInputSources != updatedSettings.ForceMonoInputSources)
            {
                Log.Information($"ForceMonoInputSources changed from '{settings.ForceMonoInputSources}' to '{updatedSettings.ForceMonoInputSources}'");
                settings.ForceMonoInputSources = updatedSettings.ForceMonoInputSources;
                hasChanges = true;
            }

            // Update RateControl
            if (settings.RateControl != updatedSettings.RateControl && !hasAutoSelectedRateControl)
            {
                Log.Information($"RateControl changed from '{settings.RateControl}' to '{updatedSettings.RateControl}'");
                settings.RateControl = updatedSettings.RateControl;
                hasChanges = true;
            }

            // Update CrfValue
            if (settings.CrfValue != updatedSettings.CrfValue)
            {
                Log.Information($"CrfValue changed from '{settings.CrfValue}' to '{updatedSettings.CrfValue}'");
                settings.CrfValue = updatedSettings.CrfValue;
                hasChanges = true;
            }

            // Update CqLevel
            if (settings.CqLevel != updatedSettings.CqLevel)
            {
                Log.Information($"CqLevel changed from '{settings.CqLevel}' to '{updatedSettings.CqLevel}'");
                settings.CqLevel = updatedSettings.CqLevel;
                hasChanges = true;
            }

            // Update SelectedDisplay
            if ((settings.SelectedDisplay == null && updatedSettings.SelectedDisplay != null) ||
                (settings.SelectedDisplay != null && updatedSettings.SelectedDisplay == null) ||
                (settings.SelectedDisplay != null && updatedSettings.SelectedDisplay != null && !settings.SelectedDisplay.Equals(updatedSettings.SelectedDisplay)))
            {
                Log.Information($"SelectedDisplay changed from '{settings.SelectedDisplay}' to '{updatedSettings.SelectedDisplay}'");
                settings.SelectedDisplay = updatedSettings.SelectedDisplay;

                // Update display source if we have a recording and it is not using game hook
                if (Settings.Instance.State.Recording != null && !Settings.Instance.State.Recording.IsUsingGameHook)
                {
                    OBSService.DisposeDisplaySource();
                    OBSService.AddMonitorCapture();
                }
                hasChanges = true;
            }

            // Update DisplayCaptureMethod
            if (settings.DisplayCaptureMethod != updatedSettings.DisplayCaptureMethod)
            {
                Log.Information($"DisplayCaptureMethod changed from '{settings.DisplayCaptureMethod}' to '{updatedSettings.DisplayCaptureMethod}'");
                settings.DisplayCaptureMethod = updatedSettings.DisplayCaptureMethod;
                hasChanges = true;
            }

            // Update EnableAi
            if (settings.EnableAi != updatedSettings.EnableAi)
            {
                Log.Information($"EnableAi changed from '{settings.EnableAi}' to '{updatedSettings.EnableAi}'");
                settings.EnableAi = updatedSettings.EnableAi;
                hasChanges = true;
            }

            // Update AutoGenerateHighlights
            if (settings.AutoGenerateHighlights != updatedSettings.AutoGenerateHighlights)
            {
                Log.Information($"AutoGenerateHighlights changed from '{settings.AutoGenerateHighlights}' to '{updatedSettings.AutoGenerateHighlights}'");
                settings.AutoGenerateHighlights = updatedSettings.AutoGenerateHighlights;
                hasChanges = true;
            }

            // Update ReceiveBetaUpdates
            if (settings.ReceiveBetaUpdates != updatedSettings.ReceiveBetaUpdates)
            {
                Log.Information($"ReceiveBetaUpdates changed from '{settings.ReceiveBetaUpdates}' to '{updatedSettings.ReceiveBetaUpdates}'");
                settings.ReceiveBetaUpdates = updatedSettings.ReceiveBetaUpdates;
                hasChanges = true;
                _ = Task.Run(UpdateService.UpdateAppIfNecessary);
                _ = Task.Run(UpdateService.GetReleaseNotes);
            }

            // Update RunOnStartup
            if (settings.RunOnStartup != updatedSettings.RunOnStartup)
            {
                Log.Information($"RunOnStartup changed from '{settings.RunOnStartup}' to '{updatedSettings.RunOnStartup}'");
                settings.RunOnStartup = updatedSettings.RunOnStartup;
                hasChanges = true;
            }

            // Update SelectedOBSVersion
            if (settings.SelectedOBSVersion != updatedSettings.SelectedOBSVersion)
            {
                Log.Information($"SelectedOBSVersion changed from '{settings.SelectedOBSVersion ?? "Automatic"}' to '{updatedSettings.SelectedOBSVersion ?? "Automatic"}'");
                settings.SelectedOBSVersion = updatedSettings.SelectedOBSVersion;
                hasChanges = true;

                // If we're changing OBS version, check if we need to download it
                if (OBSService.IsInitialized)
                {
                    _ = Task.Run(() => OBSService.CheckIfExistsOrDownloadAsync(true));
                }
            }

            // Update Keybindings
            if (updatedSettings.Keybindings != null)
            {
                settings.Keybindings = updatedSettings.Keybindings;
                KeybindCaptureService.RefreshKeybindingsCache();
                hasChanges = true;
            }

            // Only save settings and send to frontend if changes were actually made
            if (hasChanges)
            {
                Log.Information("Settings updated, saving changes");
                settings.EndBulkUpdateAndSaveSettings();
            }
            else
            {
                // End bulk update without saving if no changes were made
                settings._isBulkUpdating = false;
                Log.Information("No settings changes detected");
            }
        }

        public static async Task LoadContentFromFolderIntoState(bool sendToFrontend = true)
        {
            var contentTypes = Enum.GetValues(typeof(Content.ContentType)).Cast<Content.ContentType>().ToArray();
            var content = new List<Content>();

            try
            {
                foreach (var contentType in contentTypes)
                {
                    string metadataPath = FolderNames.GetMetadataFolderPath(contentType);

                    if (!Directory.Exists(metadataPath))
                    {
                        continue;
                    }

                    // Get metadata files in the current folder
                    var metadataFiles = Directory.EnumerateFiles(metadataPath, "*.json", SearchOption.TopDirectoryOnly)
                                                 .Where(file => IsMetadataFile(file));

                    foreach (var metadataFilePath in metadataFiles)
                    {
                        var serializedMetadataFilePath = metadataFilePath.Replace("\\", "/");
                        try
                        {
                            // Read and parse metadata
                            var metadataContent = File.ReadAllText(serializedMetadataFilePath);
                            var metadata = JsonSerializer.Deserialize<Content>(metadataContent);

                            if (metadata == null || !File.Exists(metadata.FilePath))
                            {
                                Log.Warning($"Invalid or missing metadata for file: {serializedMetadataFilePath}");
                                continue;
                            }

                            // Update FileSizeKb if it is 0 (migration, remove this in the future)
                            if (metadata.FileSizeKb == 0)
                            {
                                Log.Information($"[MIGRATION] Adding FileSizeKb to {metadata.FilePath}");
                                var updatedMetadata = await ContentService.UpdateMetadataFile(metadataFilePath, c =>
                                {
                                    c.FileSizeKb = ContentService.GetFileSize(c.FilePath).sizeKb;
                                });

                                if (updatedMetadata != null)
                                {
                                    metadata = updatedMetadata;
                                }
                            }

                            content.Add(new Content
                            {
                                Type = metadata.Type,
                                Title = metadata.Title,
                                Game = metadata.Game,
                                Bookmarks = metadata.Bookmarks,
                                FileName = metadata.FileName,
                                FilePath = metadata.FilePath,
                                FileSize = metadata.FileSize,
                                FileSizeKb = metadata.FileSizeKb,
                                Duration = metadata.Duration,
                                CreatedAt = metadata.CreatedAt,
                                UploadId = metadata.UploadId,
                                IgdbId = metadata.IgdbId,
                                AudioTrackNames = metadata.AudioTrackNames,
                                IsImported = metadata.IsImported
                            });
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Error processing metadata file '{serializedMetadataFilePath}': {ex.Message}");
                        }
                    }
                }

                // Sort videos by creation date descending
                content = content.OrderByDescending(v => v.CreatedAt).ToList();
            }
            catch (Exception ex)
            {
                Log.Error($"Error reading videos: {ex.Message}");
            }

            Settings.Instance.State.SetContent(content, sendToFrontend);

            // Update folder size in state
            Windows.Storage.StorageService.UpdateFolderSizeInState();
        }

        public static void GetPrimaryMonitorResolution(out uint boundsWidth, out uint boundsHeight)
        {
            // Try to get physical resolution (DPI-aware)
            if (DisplayService.GetPrimaryMonitorPhysicalResolution(out boundsWidth, out boundsHeight))
            {
                if (Screen.PrimaryScreen != null)
                {
                    Log.Information($"Physical resolution: {boundsWidth}x{boundsHeight} (logical: {Screen.PrimaryScreen.Bounds.Width}x{Screen.PrimaryScreen.Bounds.Height})");
                }
                return;
            }

            // Fallback to logical resolution
            if (Screen.PrimaryScreen != null)
            {
                boundsWidth = (uint)Screen.PrimaryScreen.Bounds.Width;
                boundsHeight = (uint)Screen.PrimaryScreen.Bounds.Height;
                Log.Warning("Using logical resolution as fallback");
            }
            else
            {
                boundsWidth = 1920;
                boundsHeight = 1080;
                Log.Warning("Primary screen not found, defaulting to 1920x1080");
            }
        }

        public static void GetResolution(string resolution, out uint width, out uint height)
        {
            switch (resolution)
            {
                case "720p":
                    width = 1280;
                    height = 720;
                    break;
                case "1080p":
                    width = 1920;
                    height = 1080;
                    break;
                case "1440p":
                    width = 2560;
                    height = 1440;
                    break;
                case "4K":
                    width = 3840;
                    height = 2160;
                    break;
                default:
                    // Default to 1080p if unknown
                    width = 1920;
                    height = 1080;
                    break;
            }
        }

        private static bool IsMetadataFile(string filePath)
        {
            // Check if the file is a metadata file
            return Path.GetExtension(filePath).Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                   !Path.GetFileName(filePath).StartsWith(".");
        }

        public static void SetAvailableOBSVersions(List<Core.Models.OBSVersion> versions)
        {
            if (versions == null || versions.Count == 0)
            {
                Log.Warning("Received empty OBS versions list");
                return;
            }

            Log.Information($"Setting {versions.Count} available OBS versions");
            Settings.Instance.State.AvailableOBSVersions = versions;

            // If the selected version is not in the list anymore, reset it to null (automatic)
            if (!string.IsNullOrEmpty(Settings.Instance.SelectedOBSVersion) &&
                !versions.Any(v => v.Version == Settings.Instance.SelectedOBSVersion))
            {
                Log.Warning($"Selected OBS version {Settings.Instance.SelectedOBSVersion} is no longer available, resetting to automatic");
                Settings.Instance.SelectedOBSVersion = null;
            }
        }

        /// <summary>
        /// Reconciles selected device settings with currently available devices.
        /// If a selected device's ID no longer exists but a device with a matching name is found,
        /// the selected device's ID is updated to the new ID. This handles cases where Windows
        /// assigns new IDs to devices after updates or hardware changes.
        /// </summary>
        public static void ReconcileDeviceSettings(List<DeviceSetting> selectedDevices, List<AudioDevice> availableDevices, string deviceType)
        {
            bool hasChanges = false;

            foreach (DeviceSetting selectedDevice in selectedDevices)
            {
                bool idExists = availableDevices.Any(d => d.Id == selectedDevice.Id);
                if (idExists)
                {
                    continue;
                }

                string savedNameNormalized = NormalizeDeviceName(selectedDevice.Name);

                // Prevent matching the same device multiple times if they have the same name
                HashSet<string> alreadyUsedIds = selectedDevices.Select(d => d.Id).ToHashSet();

                AudioDevice? matchingDevice = availableDevices
                    .Where(d => !alreadyUsedIds.Contains(d.Id) &&
                        NormalizeDeviceName(d.Name).Equals(savedNameNormalized, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.IsDefault)
                    .FirstOrDefault();

                if (matchingDevice != null)
                {
                    Log.Information($"Reconciling {deviceType} device: '{selectedDevice.Name}' ID changed from '{selectedDevice.Id}' to '{matchingDevice.Id}'");
                    selectedDevice.Id = matchingDevice.Id;
                    selectedDevice.Name = matchingDevice.Name;
                    hasChanges = true;
                }
                else
                {
                    Log.Warning($"Saved {deviceType} device '{selectedDevice.Name}' (ID: {selectedDevice.Id}) not found in available devices");
                }
            }

            if (hasChanges)
            {
                SaveSettings();
            }
        }

        private static string NormalizeDeviceName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            const string defaultSuffix = " (Default)";
            if (name.EndsWith(defaultSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^defaultSuffix.Length];
            }

            return name;
        }

        public static void SelectDefaultDevices()
        {
            List<AudioDevice> inputDevices = Settings.Instance.State.InputDevices;
            List<AudioDevice> outputDevices = Settings.Instance.State.OutputDevices;

            AudioDevice? defaultInputDevice = inputDevices.FirstOrDefault(d => d.IsDefault);
            if (defaultInputDevice != null)
            {
                Settings.Instance.BeginBulkUpdate();
                Settings.Instance.InputDevices.Add(new DeviceSetting
                {
                    Id = defaultInputDevice.Id,
                    Name = defaultInputDevice.Name,
                    Volume = 1.0f
                });
                Settings.Instance.EndBulkUpdateAndSaveSettings();
                Log.Information($"Auto-selected default input device: {defaultInputDevice.Name}");
            }

            AudioDevice? defaultOutputDevice = outputDevices.FirstOrDefault(d => d.IsDefault);
            if (defaultOutputDevice != null)
            {
                Settings.Instance.BeginBulkUpdate();
                Settings.Instance.OutputDevices.Add(new DeviceSetting
                {
                    Id = defaultOutputDevice.Id,
                    Name = defaultOutputDevice.Name,
                    Volume = 1.0f
                });
                Settings.Instance.EndBulkUpdateAndSaveSettings();
                Log.Information($"Auto-selected default output device: {defaultOutputDevice.Name}");
            }
        }

        /// <summary>
        /// Migrates cache contents (metadata, thumbnails, waveforms) from the old folder to the new folder.
        /// </summary>
        public static async Task MigrateCacheFolder(string oldCacheFolder, string newCacheFolder)
        {
            if (string.IsNullOrEmpty(oldCacheFolder) || string.IsNullOrEmpty(newCacheFolder))
            {
                Log.Warning("Cannot migrate cache: old or new folder path is empty");
                return;
            }

            if (string.Equals(oldCacheFolder.Replace("/", "\\"), newCacheFolder.Replace("/", "\\"), StringComparison.OrdinalIgnoreCase))
            {
                Log.Information("Cache folder unchanged, no migration needed");
                return;
            }

            var foldersToMigrate = new[] { FolderNames.Metadata, FolderNames.Thumbnails, FolderNames.Waveforms };

            foreach (var folderName in foldersToMigrate)
            {
                string sourcePath = Path.Combine(oldCacheFolder.Replace("/", "\\"), folderName);
                string destPath = Path.Combine(newCacheFolder.Replace("/", "\\"), folderName);

                if (!Directory.Exists(sourcePath))
                {
                    Log.Information($"Source folder does not exist, skipping: {sourcePath}");
                    continue;
                }

                try
                {
                    // Create destination directory if it doesn't exist
                    Directory.CreateDirectory(destPath);

                    // Move all files and subdirectories
                    foreach (var dir in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
                    {
                        string targetDir = dir.Replace(sourcePath, destPath);
                        Directory.CreateDirectory(targetDir);
                    }

                    foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
                    {
                        string targetFile = file.Replace(sourcePath, destPath);
                        if (File.Exists(targetFile))
                        {
                            File.Delete(targetFile);
                        }
                        File.Move(file, targetFile);
                    }

                    // Remove old directory after successful migration
                    Directory.Delete(sourcePath, true);
                    Log.Information($"Successfully migrated {folderName} from {sourcePath} to {destPath}");
                }
                catch (Exception ex)
                {
                    Log.Error($"Error migrating {folderName}: {ex.Message}");
                }
            }

            // Reload content to update paths
            await LoadContentFromFolderIntoState();
            Log.Information("Cache migration completed");
        }
    }
}
