namespace Segra.Backend.Shared
{
    // Counts long-running jobs (clips, uploads, imports, highlights) so an automatic update never restarts mid-job.
    public static class BackgroundWork
    {
        private static int _active;

        public static bool IsBusy => Volatile.Read(ref _active) > 0;

        public static IDisposable Begin()
        {
            Interlocked.Increment(ref _active);
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Decrement(ref _active);
            }
        }
    }
}
