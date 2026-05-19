using Segra.Backend.Core.Models;
using Serilog;

namespace Segra.Backend.Games
{
    /// <summary>
    /// Base class for game integrations that detect events by tailing a single log file.
    /// Handles file polling, rotation detection, and offset bookkeeping; subclasses only
    /// need to provide the log path and parse individual lines via <see cref="ProcessLine"/>.
    /// </summary>
    internal abstract class LogTailIntegration : Integration, IDisposable
    {
        private CancellationTokenSource? _cts;
        private long _lastLength;
        private string? _currentPath;

        protected abstract string LogPrefix { get; }
        protected virtual int PollIntervalMs => 500;
        protected virtual int FileResolveTimeoutSec => 30;

        /// <summary>
        /// Returns the absolute path to the log file (or null if it cannot be resolved yet).
        /// Called repeatedly until a non-null path is returned or the integration shuts down.
        /// </summary>
        protected abstract string? ResolveLogPath();

        /// <summary>
        /// Called once per new line of log output. May be invoked concurrently across
        /// different files (after rotation), but never concurrently for the same file.
        /// </summary>
        protected abstract void ProcessLine(string line);

        /// <summary>
        /// Called when the integration starts watching a (newly resolved) log file.
        /// Override to reset per-session state.
        /// </summary>
        protected virtual void OnLogOpened(string path) { }

        public override Task Start()
        {
            _cts = new CancellationTokenSource();
            Log.Information($"[{LogPrefix}] Starting log-tail integration");
            _ = Task.Run(() => TailLoop(_cts.Token));
            return Task.CompletedTask;
        }

        public override Task Shutdown()
        {
            Log.Information($"[{LogPrefix}] Shutting down log-tail integration");
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
            }
            catch { }
            _cts = null;
            return Task.CompletedTask;
        }

        public void Dispose() => Shutdown().Wait();

        private async Task TailLoop(CancellationToken token)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(FileResolveTimeoutSec);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    string? path = ResolveLogPath();
                    if (path != null && File.Exists(path))
                    {
                        _currentPath = path;
                        // Start tailing from end of file so we don't replay pre-recording events
                        try
                        {
                            _lastLength = new FileInfo(path).Length;
                        }
                        catch
                        {
                            _lastLength = 0;
                        }
                        OnLogOpened(path);
                        Log.Information($"[{LogPrefix}] Tailing log file: {path}");
                        break;
                    }

                    if (DateTime.UtcNow > deadline)
                    {
                        Log.Warning($"[{LogPrefix}] Log file not yet available, continuing to retry");
                        deadline = DateTime.UtcNow.AddSeconds(FileResolveTimeoutSec);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[{LogPrefix}] Error resolving log path: {ex.Message}");
                }

                try { await Task.Delay(1000, token); } catch { return; }
            }

            while (!token.IsCancellationRequested && _currentPath != null)
            {
                try
                {
                    // Re-resolve path: file may rotate or change between sessions
                    string? resolved = ResolveLogPath();
                    if (resolved != null && !string.Equals(resolved, _currentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Information($"[{LogPrefix}] Log path changed: {_currentPath} -> {resolved}");
                        _currentPath = resolved;
                        _lastLength = 0;
                        OnLogOpened(resolved);
                    }

                    if (!File.Exists(_currentPath))
                    {
                        try { await Task.Delay(PollIntervalMs, token); } catch { return; }
                        continue;
                    }

                    long currentLength;
                    try { currentLength = new FileInfo(_currentPath).Length; }
                    catch { try { await Task.Delay(PollIntervalMs, token); } catch { return; } continue; }

                    if (currentLength < _lastLength)
                    {
                        Log.Information($"[{LogPrefix}] Log truncated, resetting offset");
                        _lastLength = 0;
                        OnLogOpened(_currentPath);
                    }

                    if (currentLength > _lastLength)
                    {
                        try
                        {
                            using var fs = new FileStream(_currentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            fs.Seek(_lastLength, SeekOrigin.Begin);
                            using var reader = new StreamReader(fs);
                            string? line;
                            while ((line = await reader.ReadLineAsync()) != null)
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                try
                                {
                                    ProcessLine(line);
                                }
                                catch (Exception ex)
                                {
                                    Log.Warning($"[{LogPrefix}] Error processing line: {ex.Message}");
                                }
                            }
                            _lastLength = fs.Position;
                        }
                        catch (IOException ex)
                        {
                            Log.Debug($"[{LogPrefix}] Read IO error (will retry): {ex.Message}");
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Warning($"[{LogPrefix}] Tail loop error: {ex.Message}");
                }

                try { await Task.Delay(PollIntervalMs, token); } catch { return; }
            }
        }

        protected void AddBookmark(BookmarkType type)
        {
            if (AppState.Instance.Recording == null)
            {
                Log.Debug($"[{LogPrefix}] No recording active, skipping {type} bookmark");
                return;
            }

            // Compensate for the average polling lag — on a PollIntervalMs cycle the
            // line we just read was on average half an interval old.
            var compensation = TimeSpan.FromMilliseconds(PollIntervalMs / 2.0);

            var bookmark = new Bookmark
            {
                Type = type,
                Time = (DateTime.Now - AppState.Instance.Recording.StartTime) - compensation
            };
            AppState.Instance.Recording.Bookmarks.Add(bookmark);
            Log.Information($"[{LogPrefix}] BOOKMARK ADDED: {type} at {bookmark.Time} (compensated -{compensation.TotalMilliseconds:F0}ms)");
        }
    }
}
