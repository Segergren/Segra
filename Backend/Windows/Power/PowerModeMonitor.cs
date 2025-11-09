using Microsoft.Win32;
using Serilog;

namespace Segra.Backend.Windows.Power
{
    internal static class PowerModeMonitor
    {
        private static bool _isMonitoring = false;

        public static void StartMonitoring()
        {
            if (_isMonitoring)
            {
                Log.Warning("PowerModeMonitor is already running");
                return;
            }

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _isMonitoring = true;
        }

        private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            switch (e.Mode)
            {
                case PowerModes.Resume:
                    Log.Information("Power Mode: RESUME - System woke up from sleep/hibernation");
                    break;
                case PowerModes.Suspend:
                    Log.Information("Power Mode: SUSPEND - System is entering sleep/hibernation");
                    break;
                default:
                    break;
            }
        }
    }
}
