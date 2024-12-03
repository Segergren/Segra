﻿using ReCaps.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReCaps.Backend.Utils
{
    internal static class SettingsUtils
    {
        private static readonly string SettingsFilePath =
           Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReCaps", "settings.json");

        public static void SaveSettings()
        {
            try
            {
                // Ensure the directory exists
                var directory = Path.GetDirectoryName(SettingsFilePath);
                if (!Directory.Exists(directory))
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

        public static void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                {
                    Log.Information($"Settings file not found at {SettingsFilePath}. Using default settings.");
                    return;
                }

                // Read and deserialize settings
                var json = File.ReadAllText(SettingsFilePath);
                var loadedSettings = JsonSerializer.Deserialize<Settings>(json);

                if (loadedSettings != null)
                {
                    // Begin bulk update to suppress unnecessary updates
                    Settings.Instance.BeginBulkUpdate();

                    // Update properties individually
                    Settings.Instance.Theme = loadedSettings.Theme;
                    Settings.Instance.ContentFolder = loadedSettings.ContentFolder;
                    Settings.Instance.Resolution = loadedSettings.Resolution;
                    Settings.Instance.FrameRate = loadedSettings.FrameRate;
                    Settings.Instance.Bitrate = loadedSettings.Bitrate;
                    Settings.Instance.Encoder = loadedSettings.Encoder;
                    Settings.Instance.Codec = loadedSettings.Codec;
                    Settings.Instance.StorageLimit = loadedSettings.StorageLimit;
                    Settings.Instance.InputDevice = loadedSettings.InputDevice;
                    Settings.Instance.OutputDevice = loadedSettings.OutputDevice;
                    Settings.Instance.KeyframeInterval = loadedSettings.KeyframeInterval;
                    Settings.Instance.Preset = loadedSettings.Preset;
                    Settings.Instance.Profile = loadedSettings.Profile;

                    Settings.Instance.RateControl = loadedSettings.RateControl;
                    Settings.Instance.CrfValue = loadedSettings.CrfValue;
                    Settings.Instance.CqLevel = loadedSettings.CqLevel;

                    // End bulk update
                    Settings.Instance.EndBulkUpdateAndSaveSettings();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load settings: {ex.Message}");
            }
        }

        public static void HandleUpdateSettings(JsonElement settingsElement)
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
                    UpdateSettingsInstance(updatedSettings);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to update settings: {ex.Message}");
            }
        }

        private static void UpdateSettingsInstance(Settings updatedSettings)
        {
            var settings = Settings.Instance;

            settings.BeginBulkUpdate();

            // Update Theme
            if (settings.Theme != updatedSettings.Theme)
            {
                Log.Information($"Theme changed from '{settings.Theme}' to '{updatedSettings.Theme}'");
                settings.Theme = updatedSettings.Theme;
            }

            // Update ContentFolder
            if (settings.ContentFolder != updatedSettings.ContentFolder)
            {
                Log.Information($"ContentFolder changed from '{settings.ContentFolder}' to '{updatedSettings.ContentFolder}'");
                settings.ContentFolder = updatedSettings.ContentFolder;
            }

            // Update Resolution
            if (settings.Resolution != updatedSettings.Resolution)
            {
                Log.Information($"Resolution changed from '{settings.Resolution}' to '{updatedSettings.Resolution}'");
                settings.Resolution = updatedSettings.Resolution;
            }

            // Update FrameRate
            if (settings.FrameRate != updatedSettings.FrameRate)
            {
                Log.Information($"FrameRate changed from '{settings.FrameRate}' to '{updatedSettings.FrameRate}'");
                settings.FrameRate = updatedSettings.FrameRate;
            }

            // Update Bitrate
            if (settings.Bitrate != updatedSettings.Bitrate)
            {
                Log.Information($"Bitrate changed from '{settings.Bitrate} Mbps' to '{updatedSettings.Bitrate} Mbps'");
                settings.Bitrate = updatedSettings.Bitrate;
            }

            // Update Encoder
            if (settings.Encoder != updatedSettings.Encoder)
            {
                Log.Information($"Encoder changed from '{settings.Encoder}' to '{updatedSettings.Encoder}'");
                settings.Encoder = updatedSettings.Encoder;
            }

            // Update Codec
            if (settings.Codec != updatedSettings.Codec)
            {
                Log.Information($"Codec changed from '{settings.Codec}' to '{updatedSettings.Codec}'");
                settings.Codec = updatedSettings.Codec;
            }

            // Update StorageLimit
            if (settings.StorageLimit != updatedSettings.StorageLimit)
            {
                Log.Information($"StorageLimit changed from '{settings.StorageLimit} GB' to '{updatedSettings.StorageLimit} GB'");
                settings.StorageLimit = updatedSettings.StorageLimit;
            }

            // Update InputDevice
            if (settings.InputDevice != updatedSettings.InputDevice)
            {
                Log.Information($"InputDevice changed from '{settings.InputDevice}' to '{updatedSettings.InputDevice}'");
                settings.InputDevice = updatedSettings.InputDevice;
            }

            // Update OutputDevice
            if (settings.OutputDevice != updatedSettings.OutputDevice)
            {
                Log.Information($"OutputDevice changed from '{settings.OutputDevice}' to '{updatedSettings.OutputDevice}'");
                settings.OutputDevice = updatedSettings.OutputDevice;
            }

            // Update KeyframeInterval
            if (settings.KeyframeInterval != updatedSettings.KeyframeInterval)
            {
                Log.Information($"KeyframeInterval changed from '{settings.KeyframeInterval} seconds' to '{updatedSettings.KeyframeInterval} seconds'");
                settings.KeyframeInterval = updatedSettings.KeyframeInterval;
            }

            // Update Preset
            if (settings.Preset != updatedSettings.Preset)
            {
                Log.Information($"Preset changed from '{settings.Preset}' to '{updatedSettings.Preset}'");
                settings.Preset = updatedSettings.Preset;
            }

            // Update Profile
            if (settings.Profile != updatedSettings.Profile)
            {
                Log.Information($"Profile changed from '{settings.Profile}' to '{updatedSettings.Profile}'");
                settings.Profile = updatedSettings.Profile;
            }

            // Update RateControl
            if (settings.RateControl != updatedSettings.RateControl)
            {
                Log.Information($"RateControl changed from '{settings.RateControl}' to '{updatedSettings.RateControl}'");
                settings.RateControl = updatedSettings.RateControl;
            }

            // Update CrfValue
            if (settings.CrfValue != updatedSettings.CrfValue)
            {
                Log.Information($"CrfValue changed from '{settings.CrfValue}' to '{updatedSettings.CrfValue}'");
                settings.CrfValue = updatedSettings.CrfValue;
            }

            // Update CqLevel
            if (settings.CqLevel != updatedSettings.CqLevel)
            {
                Log.Information($"CqLevel changed from '{settings.CqLevel}' to '{updatedSettings.CqLevel}'");
                settings.CqLevel = updatedSettings.CqLevel;
            }

            settings.EndBulkUpdateAndSaveSettings();
        }



        public static void LoadContentFromFolderIntoState(bool sendToFrontend = true)
        {
            string baseMetadataPath = Settings.Instance.ContentFolder + "/.metadata";
            string[] subfolders = { "videos", "clips" };
            var content = new List<Content>();

            try
            {
                foreach (var subfolder in subfolders)
                {
                    string metadataPath = Path.Combine(baseMetadataPath, subfolder).Replace("\\", "/");

                    if (!Directory.Exists(metadataPath))
                    {
                        Log.Information($"Directory does not exist: {metadataPath}");
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
                                Log.Information($"Invalid or missing metadata for file: {serializedMetadataFilePath}");
                                continue;
                            }

                            content.Add(new Content
                            {
                                Type = metadata.Type,
                                Title = metadata.Title,
                                Game = metadata.Game,
                                FileName = metadata.FileName,
                                FilePath = metadata.FilePath,
                                FileSize = metadata.FileSize,
                                Duration = metadata.Duration,
                                CreatedAt = metadata.CreatedAt
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
        }

        public static void GetPrimaryMonitorResolution(out uint width, out uint height)
        {
            width = (uint)Screen.PrimaryScreen.Bounds.Width;
            height = (uint)Screen.PrimaryScreen.Bounds.Height;
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
            return Path.GetExtension(filePath).Equals(".json", StringComparison.OrdinalIgnoreCase);
        }

        private static TimeSpan GetVideoDuration(string filePath)
        {
            // Placeholder for video duration retrieval
            // Use a library like MediaToolkit or FFmpeg to fetch actual duration
            return TimeSpan.Zero;
        }
    }
}
