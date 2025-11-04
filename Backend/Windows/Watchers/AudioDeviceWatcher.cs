using NAudio.CoreAudioApi.Interfaces;
using NAudio.CoreAudioApi;

namespace Segra.Backend.Windows.Watchers
{
    public class AudioDeviceWatcher : IMMNotificationClient, IDisposable
    {
        private MMDeviceEnumerator? _deviceEnumerator;

        public event Action? DevicesChanged;

        public AudioDeviceWatcher()
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            _deviceEnumerator.RegisterEndpointNotificationCallback(this);
        }

        // IMMNotificationClient implementation
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            DevicesChanged?.Invoke();
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
            DevicesChanged?.Invoke();
        }

        public void OnDeviceRemoved(string deviceId)
        {
            DevicesChanged?.Invoke();
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            DevicesChanged?.Invoke();
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Not needed for this purpose
        }

        // IDisposable implementation
        public void Dispose()
        {
            if (_deviceEnumerator != null)
            {
                _deviceEnumerator.UnregisterEndpointNotificationCallback(this);
                _deviceEnumerator.Dispose();
                _deviceEnumerator = null;
            }
        }
    }
}
