using Serilog;
using Segra.Backend.Recorder;
using System.Runtime.InteropServices;

namespace Segra.Backend.Windows.Power
{
    internal static partial class PowerModeMonitor
    {
        private const uint DEVICE_NOTIFY_CALLBACK = 2;
        private const uint PBT_APMSUSPEND = 0x4;
        private const uint PBT_APMRESUMEAUTOMATIC = 0x12;

        private delegate uint DeviceNotifyCallbackRoutine(nint context, uint type, nint setting);

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceNotifySubscribeParameters
        {
            public nint Callback;
            public nint Context;
        }

        [LibraryImport("powrprof.dll")]
        private static partial uint PowerRegisterSuspendResumeNotification(uint flags, ref DeviceNotifySubscribeParameters recipient, out nint registrationHandle);

        // Referenced for the life of the process so the native callback stays valid
        private static readonly DeviceNotifyCallbackRoutine PowerEventCallback = OnPowerEvent;
        private static nint _registration;

        public static void StartMonitoring()
        {
            if (_registration != 0)
            {
                Log.Warning("PowerModeMonitor is already running");
                return;
            }

            var parameters = new DeviceNotifySubscribeParameters { Callback = Marshal.GetFunctionPointerForDelegate(PowerEventCallback) };
            uint error = PowerRegisterSuspendResumeNotification(DEVICE_NOTIFY_CALLBACK, ref parameters, out _registration);
            if (error != 0)
                Log.Error($"Failed to register for sleep and wake notifications (error {error})");
        }

        // Unlike SystemEvents.PowerModeChanged, this also reports wakes without user input (PBT_APMRESUMEAUTOMATIC)
        private static uint OnPowerEvent(nint context, uint type, nint setting)
        {
            try
            {
                switch (type)
                {
                    case PBT_APMSUSPEND:
                        Log.Information("Power Mode: SUSPEND - System is entering sleep/hibernation");
                        OBSService.OnSystemSuspend();
                        break;
                    case PBT_APMRESUMEAUTOMATIC:
                        Log.Information("Power Mode: RESUME - System woke up from sleep/hibernation");
                        OBSService.OnSystemResume();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to handle a power event");
            }

            return 0;
        }
    }
}
