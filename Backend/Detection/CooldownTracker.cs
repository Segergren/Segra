using System.Collections.Concurrent;
using Segra.Backend.Core;
using Segra.Backend.Core.Models;
using Serilog;

namespace Segra.Backend.Detection;

public class CooldownTracker
{
    private const int DefaultLifetimeMs = 1200;
    private readonly ConcurrentDictionary<int, ActiveEvent> _activeEvents = new();

    private sealed class ActiveEvent
    {
        public int LifetimeMs { get; set; }
        public DateTime FirstSeen { get; set; }
        public DateTime LastSeen { get; set; }
    }

    public void ProcessDetection(DetectionResult result, EventDefinition definition, DateTime now)
    {
        var classId = result.ClassId;
        var lifetimeMs = definition.LifetimeMs ?? DefaultLifetimeMs;

        if (_activeEvents.TryGetValue(classId, out var active))
        {
            lock (active)
            {
                if ((now - active.LastSeen).TotalMilliseconds < active.LifetimeMs)
                {
                    active.LastSeen = now;
                    Log.Debug("ProcessDetection: extending active window for class {ClassId}, age {Age:F1}s",
                        classId, (now - active.FirstSeen).TotalSeconds);
                    return;
                }
            }
        }

        var created = new ActiveEvent { LifetimeMs = lifetimeMs, FirstSeen = now, LastSeen = now };
        _activeEvents[classId] = created;
        CreateBookmark(result, definition, now);
    }

    public void Cleanup(DateTime now)
    {
        foreach (var classId in _activeEvents.Keys.ToList())
        {
            if (_activeEvents.TryGetValue(classId, out var active)
                && (now - active.LastSeen).TotalMilliseconds > active.LifetimeMs)
            {
                _activeEvents.TryRemove(classId, out _);
                Log.Debug("Cleanup: expired active window for class {ClassId}", classId);
            }
        }
    }

    private void CreateBookmark(DetectionResult result, EventDefinition definition, DateTime now)
    {
        if (definition.BookmarkType == null)
        {
            Log.Debug("CreateBookmark: BookmarkType is null for {EventName}", definition.Name);
            return;
        }

        var recording = AppState.Instance.Recording;
        if (recording == null)
        {
            Log.Debug("CreateBookmark: no active recording");
            return;
        }

        var bookmark = new Bookmark
        {
            Type = definition.BookmarkType.Value,
            Time = now - recording.StartTime
        };
        recording.AddBookmark(bookmark);
        Log.Information("CreateBookmark: created {Type} bookmark for '{EventName}'", definition.BookmarkType.Value, definition.Name);
    }
}
