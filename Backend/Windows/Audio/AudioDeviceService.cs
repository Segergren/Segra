using NAudio.CoreAudioApi;
using Segra.Backend.Core.Models;
using System.Text.RegularExpressions;

namespace Segra.Backend.Windows.Audio
{
    internal class AudioDeviceService
    {
        private static string GetCleanDeviceName(string friendlyName)
        {

            // If it's Voicemeeter, Elgato or GoXLR, return the original name
            if (friendlyName.Contains("Voicemeeter") || friendlyName.Contains("Elgato") || friendlyName.Contains("GoXLR"))
            {
                return friendlyName;
            }
            else if (friendlyName.Contains("SteelSeries Sonar"))
            {
                int index = friendlyName.IndexOf("(");
                if (index > 0)
                {
                    return friendlyName.Substring(0, index).Trim();
                }
            }

            // Looks for patterns like "Microphone (2- Shure MV7)" or "Speakers (Sound BlasterX AE-5 Plus)" or "Stereo Mix (Realtek(R) Audio)"
            // Extract the main part of the device name, handling cases with nested parentheses
            var mainPattern = @"^([^(]+)\((.+)\)$";
            var match = Regex.Match(friendlyName, mainPattern);

            if (match.Success && match.Groups.Count > 2)
            {
                // Group 2 contains everything inside the main parentheses
                var deviceName = match.Groups[2].Value.Trim();
                return deviceName;
            }

            // Fallback to original name if pattern doesn't match
            return friendlyName;
        }

        public static List<AudioDevice> GetInputDevices()
        {
            var devices = new List<AudioDevice>();
            var enumerator = new MMDeviceEnumerator();
            var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            try
            {
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                if (defaultDevice != null)
                {
                    // Add default device first with (Default)
                    var defaultDeviceName = GetCleanDeviceName(defaultDevice.FriendlyName);
                    devices.Add(new AudioDevice
                    {
                        Id = defaultDevice.ID,
                        Name = defaultDeviceName + " (Default)",
                        IsDefault = true
                    });
                }
            }
            catch
            {
                // No default device available
            }

            if (collection != null)
            {
                foreach (var device in collection)
                {
                    if (device == null) continue;

                    // Skip if this device is already added as the default
                    if (devices.Any(d => d.Id == device.ID)) continue;

                    try
                    {
                        var cleanName = GetCleanDeviceName(device.FriendlyName);
                        devices.Add(new AudioDevice { Id = device.ID, Name = cleanName, IsDefault = false });
                    }
                    catch
                    {
                        // Device name is invalid
                    }
                }
            }

            // Sort devices by name (keeping the default at the top if it exists)
            if (devices.Count > 0)
            {
                var defaultDev = devices.FirstOrDefault(d => d.IsDefault);
                var devicesToSort = defaultDev != null ? devices.Where(d => !d.IsDefault).ToList() : devices;
                var sortedDevices = devicesToSort.OrderBy(d => d.Name).ToList();

                if (defaultDev != null)
                {
                    sortedDevices.Insert(0, defaultDev);
                    return sortedDevices;
                }

                return sortedDevices;
            }

            return devices;
        }

        public static List<AudioDevice> GetOutputDevices()
        {
            var devices = new List<AudioDevice>();
            var enumerator = new MMDeviceEnumerator();
            var collection = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            try
            {
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (defaultDevice != null)
                {
                    // Add default device first with (Default)
                    var defaultDeviceName = GetCleanDeviceName(defaultDevice.FriendlyName);
                    devices.Add(new AudioDevice
                    {
                        Id = defaultDevice.ID,
                        Name = defaultDeviceName + " (Default)",
                        IsDefault = true
                    });
                }
            }
            catch
            {
                // No default device available
            }

            if (collection != null)
            {
                foreach (var device in collection)
                {
                    if (device == null) continue;

                    // Skip if this device is already added as the default
                    if (devices.Any(d => d.Id == device.ID)) continue;

                    try
                    {
                        var cleanName = GetCleanDeviceName(device.FriendlyName);
                        devices.Add(new AudioDevice { Id = device.ID, Name = cleanName, IsDefault = false });
                    }
                    catch
                    {
                        // Device name is invalid
                    }
                }
            }

            // Sort devices by name (keeping the default at the top if it exists)
            if (devices.Count > 0)
            {
                var defaultDev = devices.FirstOrDefault(d => d.IsDefault);
                var devicesToSort = defaultDev != null ? devices.Where(d => !d.IsDefault).ToList() : devices;
                var sortedDevices = devicesToSort.OrderBy(d => d.Name).ToList();

                if (defaultDev != null)
                {
                    sortedDevices.Insert(0, defaultDev);
                    return sortedDevices;
                }

                return sortedDevices;
            }

            return devices;
        }
    }
}
