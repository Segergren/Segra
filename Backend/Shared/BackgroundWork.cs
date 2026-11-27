using Segra.Backend.Core.Models;

namespace Segra.Backend.Shared
{
    // Counts long-running jobs (clips, uploads, imports, highlights) so an automatic update never restarts mid-job.
    public static class BackgroundWork
    {
        private static readonly TimeSpan MemoryReleaseDelay = TimeSpan.FromSeconds(5);
        private static readonly System.Threading.Timer _memoryReleaseTimer = new(_ => ReleaseMemory(), null, Timeout.Infinite, Timeout.Infinite);
        private static int _active;

        public static bool IsBusy => Volatile.Read(ref _active) > 0;

        public static IDisposable Begin()
        {
            Interlocked.Increment(ref _active);
            return new Scope();
        }

        // An idle app rarely reaches a collection on its own, so the memory of a finished job would stay committed
        private static void ReleaseMemory()
        {
            if (IsBusy || AppState.Instance.Recording != null || AppState.Instance.PreRecording != null)
                return;

            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }

        private sealed class Scope : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0 && Interlocked.Decrement(ref _active) == 0)
                    _memoryReleaseTimer.Change(MemoryReleaseDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }
}
