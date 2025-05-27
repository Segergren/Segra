using Segra.Backend.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Segra.Backend.Utils
{
    internal static class SettingsUtils
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
                
                Settings.Instance.RunOnStartup = StartupUtils.GetStartupStatus();
                
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
            bool hasChanges = false;

            // Begin bulk update to suppress multiple state updates
            settings.BeginBulkUpdate();

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
                settings.ContentFolder = updatedSettings.ContentFolder;
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

            // Update Bitrate
            if (settings.Bitrate != updatedSettings.Bitrate)
            {
                Log.Information($"Bitrate changed from '{settings.Bitrate} Mbps' to '{updatedSettings.Bitrate} Mbps'");
                settings.Bitrate = updatedSettings.Bitrate;
                hasChanges = true;
            }

            // Update Encoder
            if (settings.Encoder != updatedSettings.Encoder)
            {
                Log.Information($"Encoder changed from '{settings.Encoder}' to '{updatedSettings.Encoder}'");
                settings.Encoder = updatedSettings.Encoder;
                hasChanges = true;
            }

            // Update Codec
            if (settings.Codec != updatedSettings.Codec)
            {
                Log.Information($"Codec changed from '{settings.Codec}' to '{updatedSettings.Codec}'");
                settings.Codec = updatedSettings.Codec;
                hasChanges = true;
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

            // Update RateControl
            if (settings.RateControl != updatedSettings.RateControl)
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

            // Update EnableDisplayRecording
            if (settings.EnableDisplayRecording != updatedSettings.EnableDisplayRecording)
            {
                Log.Information($"EnableDisplayRecording changed from '{settings.EnableDisplayRecording}' to '{updatedSettings.EnableDisplayRecording}'");
                settings.EnableDisplayRecording = updatedSettings.EnableDisplayRecording;
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
                _ = Task.Run(UpdateUtils.UpdateAppIfNecessary);
                _ = Task.Run(UpdateUtils.GetReleaseNotes);
            }

            // Update EnableRunOnStartup
            if (settings.RunOnStartup != updatedSettings.RunOnStartup)
            {
                Log.Information($"RunOnStartup changed from '{settings.RunOnStartup}' to '{updatedSettings.RunOnStartup}'");
                settings.RunOnStartup = updatedSettings.RunOnStartup;
                hasChanges = true;
            }

            // Update Keybindings
            if (updatedSettings.Keybindings != null && !settings.Keybindings.SequenceEqual(updatedSettings.Keybindings))
            {
                Log.Information("Keybindings updated");
                settings.Keybindings = updatedSettings.Keybindings;
                hasChanges = true;
            }

            // Update OnlyRecordGameAndDiscord
            if (settings.OnlyRecordGameAndDiscord != updatedSettings.OnlyRecordGameAndDiscord)
            {
                Log.Information($"OnlyRecordGameAndDiscord changed from '{settings.OnlyRecordGameAndDiscord}' to '{updatedSettings.OnlyRecordGameAndDiscord}'");
                settings.OnlyRecordGameAndDiscord = updatedSettings.OnlyRecordGameAndDiscord;
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

        public static void LoadContentFromFolderIntoState(bool sendToFrontend = true)
        {
            string baseMetadataPath = Settings.Instance.ContentFolder + "/.metadata";
            string[] subfolders = Enum.GetValues(typeof(Content.ContentType))
                                    .Cast<Content.ContentType>()
                                    .Select(ct => ct.ToString().ToLower() + "s")
                                    .ToArray();
            var content = new List<Content>();

            try
            {
                foreach (var subfolder in subfolders)
                {
                    string metadataPath = Path.Combine(baseMetadataPath, subfolder).Replace("\\", "/");

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
                                Log.Information($"Invalid or missing metadata for file: {serializedMetadataFilePath}");
                                continue;
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
        private static void CopyProperties(object source, object destination, HashSet<string>? excludedProperties = null)
        {
            if (source == null || destination == null)
                return;

            var properties = destination.GetType().GetProperties(
                System.Reflection.BindingFlags.Public | 
                System.Reflection.BindingFlags.Instance);

            foreach (var property in properties)
            {
                if (excludedProperties != null && excludedProperties.Contains(property.Name))
                    continue;

                if (!property.CanRead || !property.CanWrite)
                    continue;

                try
                {
                    var sourceProperty = source.GetType().GetProperty(property.Name);
                    if (sourceProperty == null)
                        continue;

                    var value = sourceProperty.GetValue(source);
                    
                    if (value != null)
                    {
                        property.SetValue(destination, value);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to copy property {property.Name}: {ex.Message}");
                }
            }
        }
    }
}
