using System.Diagnostics;

namespace Segra.Hotkeys;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Anchor CWD to the broker's own dir so a caller's working directory can't pin `current`.
        try
        {
            string brokerDir = Path.GetDirectoryName(Environment.ProcessPath!)!;
            Directory.SetCurrentDirectory(brokerDir);
        }
        catch (Exception ex)
        {
            BrokerLog.Error(ex, "Could not reset the working directory");
        }

        int sessionId = Process.GetCurrentProcess().SessionId;
        string pipeName = HotkeyProtocol.PipeNameForSession(sessionId);

        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], "--pipe", StringComparison.OrdinalIgnoreCase))
                pipeName = args[i + 1];
        }

        // One broker per Windows session. A replacement launched while the previous one is still
        // winding down waits briefly for the mutex instead of giving up.
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, $"SegraHotkeysBroker_{sessionId}");
        }
        catch (Exception ex)
        {
            BrokerLog.Error(ex, "Could not open the single-instance mutex");
            return;
        }

        using (mutex)
        {
            bool owned;
            try
            {
                owned = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            if (!owned)
                return;

            BrokerLog.Info($"Broker starting (session={sessionId}, pipe={pipeName})");
            try
            {
                new BrokerServer(pipeName).Run();
            }
            catch (Exception ex)
            {
                BrokerLog.Error(ex, "Fatal broker error");
            }
            finally
            {
                mutex.ReleaseMutex();
            }
            BrokerLog.Info("Broker exiting");
        }
    }
}
