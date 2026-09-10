using Serilog;
using Segra.Backend.App;
using Segra.Backend.Shared;
using Segra.Backend.Core.Models;

namespace Segra.Backend.Recorder
{
    // libobs cannot recover a lost graphics device in-process, so the only remedy is restarting Segra.
    // Also catches a recording start that hangs inside libobs without ever reporting an error.
    internal static class RecorderHealthService
    {
        private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StuckStartLimit = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan BusyWaitLimit = TimeSpan.FromMinutes(2);

        private static System.Threading.Timer? _watchdog;
        private static PreRecording? _watchedPreRecording;
        private static DateTime _watchedSinceUtc;
        private static int _restartScheduled;

        public static bool IsRecorderLost => Volatile.Read(ref _restartScheduled) == 1;

        public static void StartWatchdog()
        {
            _watchdog ??= new System.Threading.Timer(CheckStuckStart, null, WatchdogInterval, WatchdogInterval);
        }

        private static void CheckStuckStart(object? state)
        {
            PreRecording? pending = AppState.Instance.PreRecording;
            if (pending == null || AppState.Instance.Recording != null)
            {
                _watchedPreRecording = null;
                return;
            }

            if (!ReferenceEquals(pending, _watchedPreRecording))
            {
                _watchedPreRecording = pending;
                _watchedSinceUtc = DateTime.UtcNow;
                return;
            }

            if (DateTime.UtcNow - _watchedSinceUtc >= StuckStartLimit)
                MarkRecorderLost($"starting the recording for {pending.Game} has been stuck for {StuckStartLimit.TotalMinutes:F0} minutes");
        }

        // libobs calls this on the crashing thread for any fatal error, such as a failed device rebuild. It must not return.
        public static void OnLibobsCrash(string message)
        {
            Program.ExitAfterCrash($"libobs: {message}", minimized: Program.Window == null);
        }

        public static void MarkRecorderLost(string reason)
        {
            // A recorder that never came up is reported by the init path; restarting would only loop.
            if (!OBSService.IsInitialized)
            {
                Log.Warning($"Recorder problem before initialization finished ({reason}); not restarting");
                return;
            }

            if (Interlocked.Exchange(ref _restartScheduled, 1) == 1)
                return;

            Log.Error($"Recorder is unusable ({reason}); restarting Segra to recover");
            _watchdog?.Dispose();
            _watchdog = null;

            _ = Task.Run(async () =>
            {
                try
                {
                    await MessageService.ShowModal(
                        "Recorder needs a restart",
                        "The recorder stopped working, usually because the graphics driver was reset. Segra will restart now to recover.",
                        "error");
                    OBSService.PlaySound("error");

                    DateTime deadline = DateTime.UtcNow + BusyWaitLimit;
                    while (BackgroundWork.IsBusy && DateTime.UtcNow < deadline)
                        await Task.Delay(1000);

                    await Task.Delay(5000);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error preparing the recorder restart");
                }

                Program.Restart(reason, minimized: Program.Window == null);
            });
        }
    }
}
