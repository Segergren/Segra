using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Serilog;
using Segra.Hotkeys;

namespace Segra.Backend.Windows.Input.HotkeyBroker
{
    /// <summary>
    /// Asks the elevated hotkey broker to exit over its pipe; nothing unprivileged can kill it.
    /// Older brokers ignore the request and fall back to their idle timeout.
    /// </summary>
    internal static class HotkeyBrokerShutdown
    {
        /// <summary>Waits for the broker to exit only when <paramref name="exitTimeout"/> is given.</summary>
        public static void RequestShutdown(TimeSpan? exitTimeout = null)
        {
            if (!HotkeyBrokerPaths.IsInstalled)
                return;

            if (!IsBrokerRunning())
                return;

            // Retry briefly while the old session tears down, but stop once a send goes through so we
            // don't re-connect and reset an older broker's idle timer.
            var sendDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < sendDeadline)
            {
                if (TrySendShutdown())
                    break;
                Thread.Sleep(200);
            }

            if (exitTimeout is not TimeSpan timeout)
                return;

            if (WaitForExit(timeout))
                Log.Information("Hotkey broker stopped for update");
            else
                Log.Warning("Hotkey broker did not exit within {Seconds:F0}s; the update may be blocked", timeout.TotalSeconds);
        }

        private static bool TrySendShutdown()
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", HotkeyBrokerPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(500);

                var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
                var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);

                // Protocol handshake: the broker writes its version first.
                int version = reader.ReadByte();
                if (version != HotkeyProtocol.Version)
                    return true;

                HotkeyProtocol.WriteMessage(writer, BrokerMessage.Shutdown);
                return true;
            }
            catch (Exception ex)
            {
                // No broker waiting; WaitForExit still lets an older broker time out on its own.
                Log.Debug(ex, "Could not signal the hotkey broker to shut down");
                return false;
            }
        }

        private static bool WaitForExit(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                if (!IsBrokerRunning())
                    return true;

                if (DateTime.UtcNow >= deadline)
                    return false;

                Thread.Sleep(100);
            }
        }

        private static bool IsBrokerRunning()
        {
            int sessionId = Process.GetCurrentProcess().SessionId;
            string processName = Path.GetFileNameWithoutExtension(HotkeyProtocol.BrokerExeName);

            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (process.SessionId == sessionId)
                        return true;
                }
                catch
                {
                    // Access denied: assume still alive so we don't race the directory swap.
                    return true;
                }
                finally
                {
                    process.Dispose();
                }
            }

            return false;
        }
    }
}
