using NAudio.CoreAudioApi;

namespace Segra.Backend.Windows.Watchers
{
    public class AudioDeviceWatcher : IDisposable
    {
        private MMDeviceEnumerator? _deviceEnumerator;
        private MMDeviceNotificationClient? _notificationClient;

        public event Action? DevicesChanged;

        public AudioDeviceWatcher()
        {
            _deviceEnumerator = new MMDeviceEnumerator();
            _notificationClient = _deviceEnumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notificationClient.DeviceStateChanged += (_, _) => DevicesChanged?.Invoke();
            _notificationClient.DeviceAdded += (_, _) => DevicesChanged?.Invoke();
            _notificationClient.DeviceRemoved += (_, _) => DevicesChanged?.Invoke();
            _notificationClient.DefaultDeviceChanged += (_, _) => DevicesChanged?.Invoke();
        }

        public void Dispose()
        {
            _notificationClient?.Dispose();
            _notificationClient = null;
            _deviceEnumerator?.Dispose();
            _deviceEnumerator = null;
        }
    }
}
