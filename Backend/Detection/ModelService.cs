using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Serilog;

namespace Segra.Backend.Detection;

public static class ModelService
{
    public static readonly string BasePath = Path.Combine(AppContext.BaseDirectory, "data", "training");

    // An InferenceSession is ~10 MB of native memory shared by every detector on the same game, so
    // it is refcounted rather than owned by whoever asked last. Lazy gives exactly one construction
    // (GetOrAdd's factory could race and drop one undisposed), the count exactly one disposal.
    private sealed class ModelHandle
    {
        public required Lazy<InferenceSession> Session { get; init; }
        public int RefCount { get; set; }
    }

    private static readonly Dictionary<string, ModelHandle> _models = new();
    private static readonly object _modelsLock = new();
    private static readonly ConcurrentDictionary<string, List<EventDefinition>> _definitions = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static List<EventDefinition> LoadEventDefinitions(string gameId)
    {
        return _definitions.GetOrAdd(gameId, id =>
        {
            var path = Path.Combine(GetGamePath(id), "events.json");

            if (!File.Exists(path))
            {
                Log.Information("No events.json found for game {GameId}", id);
                return new List<EventDefinition>();
            }

            var json = File.ReadAllText(path);
            var definitions = JsonSerializer.Deserialize<List<EventDefinition>>(json, _jsonOptions) ?? new();
            Log.Information("Loaded {Count} event definitions for game {GameId}", definitions.Count, id);
            return definitions;
        });
    }

    public static void SaveEventDefinitions(string gameId, List<EventDefinition> definitions)
    {
        var path = Path.Combine(GetGamePath(gameId), "events.json");
        var json = JsonSerializer.Serialize(definitions, _jsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        _definitions[gameId] = definitions;
        Log.Information("Saved {Count} event definitions for game {GameId}", definitions.Count, gameId);
    }

    public static bool HasModelForGame(string gameId)
    {
        var gamePath = FindGameDirectory(gameId);
        if (gamePath == null)
        {
            // Debug, not Warning: most games legitimately have no model. Listing what is on disk is
            // what turns a casing mismatch from a silent no-op into something a log can explain.
            Log.Debug("No model directory matching {GameId} under {BasePath}; available directories: {AvailableGameIds}",
                gameId, BasePath, GetAvailableGameIds());
            return false;
        }

        var modelPath = Path.Combine(gamePath, "model.onnx");
        if (!File.Exists(modelPath))
        {
            Log.Debug("Model directory {GamePath} has no model.onnx", gamePath);
            return false;
        }

        return true;
    }

    // Takes a reference on the game's session. Every successful call must be paired with exactly
    // one UnloadModel; the session stays alive until the last of those calls.
    public static InferenceSession LoadModel(string gameId)
    {
        ModelHandle handle;
        int refCount;

        lock (_modelsLock)
        {
            if (!_models.TryGetValue(gameId, out var existing))
            {
                existing = new ModelHandle
                {
                    Session = new Lazy<InferenceSession>(() => CreateSession(gameId),
                        LazyThreadSafetyMode.ExecutionAndPublication),
                };
                _models[gameId] = existing;
            }

            handle = existing;
            refCount = ++handle.RefCount;
        }

        try
        {
            // Outside the lock: construction reads a 10 MB file and runs ORT's graph optimizer.
            var session = handle.Session.Value;
            Log.Debug("ONNX model for game {GameId} now has {RefCount} user(s)", gameId, refCount);
            return session;
        }
        catch
        {
            // Lazy caches failures forever, so the poisoned entry goes with the reference: a
            // half-written model should be retried next recording, not replayed for the process.
            lock (_modelsLock)
            {
                if (--handle.RefCount <= 0
                    && _models.TryGetValue(gameId, out var current) && ReferenceEquals(current, handle))
                {
                    _models.Remove(gameId);
                }
            }
            throw;
        }
    }

    // Releases one reference taken by LoadModel. Stopping one detector must not free native memory
    // another detector on the same game is still running inference against.
    public static void UnloadModel(string gameId)
    {
        InferenceSession? released = null;

        lock (_modelsLock)
        {
            if (!_models.TryGetValue(gameId, out var handle))
            {
                // Unbalanced release (or a detector that never got a session). Definitions are
                // still dropped so a re-read picks up an edited events.json.
                _definitions.TryRemove(gameId, out _);
                Log.Debug("No loaded ONNX model to unload for game {GameId}", gameId);
                return;
            }

            if (--handle.RefCount > 0)
            {
                Log.Debug("ONNX model for game {GameId} still has {RefCount} user(s), keeping it loaded",
                    gameId, handle.RefCount);
                return;
            }

            _models.Remove(gameId);
            _definitions.TryRemove(gameId, out _);

            // Nothing to dispose when the only user never got past a failed construction.
            if (handle.Session.IsValueCreated)
                released = handle.Session.Value;
        }

        // Outside the lock: native teardown must not block another game's load.
        released?.Dispose();
        Log.Information("Unloaded ONNX model for game {GameId}", gameId);
    }

    // Test seam. The reference count is the whole point of the cache and is not observable from
    // the InferenceSession callers get back.
    internal static int GetSessionRefCount(string gameId)
    {
        lock (_modelsLock)
            return _models.TryGetValue(gameId, out var handle) ? handle.RefCount : 0;
    }

    private static InferenceSession CreateSession(string gameId)
    {
        var modelPath = GetModelPath(gameId);

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model not found for game {gameId}", modelPath);

        // ORT defaults to one intra-op thread per physical core and spins them after every
        // Run. Disabling spin and capping threads keeps idle CPU near zero.
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = 2,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        options.AppendExecutionProvider_CPU();
        var session = new InferenceSession(modelPath, options);

        Log.Information("Loaded ONNX model for game {GameId}", gameId);
        return session;
    }

    // Directories ship with the game's own casing ("Overwatch") but the id reaching us can be
    // spelled any way. Windows papers over the mismatch; ext4 and the Flatpak runtime do not, and a
    // File.Exists miss would silently no-op the whole ML feature.
    private static string? FindGameDirectory(string gameId)
    {
        // Guard rather than let EnumerateDirectories throw: a trimmed or misbuilt package has no
        // data/training at all, and the only production caller runs inside GameIntegrationService's
        // lock on every recording start, where an exception would break recording entirely.
        if (!Directory.Exists(BasePath))
            return null;

        foreach (var directory in Directory.EnumerateDirectories(BasePath))
        {
            if (Path.GetFileName(directory).Equals(gameId, StringComparison.OrdinalIgnoreCase))
                return directory;
        }

        return null;
    }

    private static string[] GetAvailableGameIds()
    {
        if (!Directory.Exists(BasePath))
            return Array.Empty<string>();

        return Directory.EnumerateDirectories(BasePath).Select(Path.GetFileName).OfType<string>().ToArray();
    }

    public static string GetGamePath(string gameId)
    {
        // Falls back to the literal id so SaveEventDefinitions can still create a directory for a
        // game that has none yet; only lookups of existing directories need the on-disk casing.
        return FindGameDirectory(gameId) ?? Path.Combine(BasePath, gameId);
    }

    public static string GetModelPath(string gameId)
    {
        return Path.Combine(GetGamePath(gameId), "model.onnx");
    }
}


