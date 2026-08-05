#if ENABLE_TRAINING

using Segra.Backend.Core;
using Segra.Backend.Core.Models;
using Serilog;

namespace Segra.Backend.Training;

internal class ActiveEvent
{
    public Segra.Backend.Detection.EventDefinition Definition { get; init; } = null!;
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public bool BookmarkCreated { get; set; }
}

public class TrainingEventLifecycle
{
    private readonly Dictionary<int, ActiveEvent> _activeTriggers = new();
    private readonly Dictionary<int, Segra.Backend.Detection.EventDefinition> _eventMap;
    private readonly HashSet<int> _exclusionEventIds;
    private DateTime? _exclusionActiveSince;
    private int? _exclusionEventId;

    public TrainingEventLifecycle(List<Segra.Backend.Detection.EventDefinition> definitions)
    {
        var sorted = definitions.OrderBy(d => d.Id).ToList();
        _eventMap = sorted
            .Select((d, i) => (d, i))
            .ToDictionary(x => x.i, x => x.d);
        _exclusionEventIds = sorted
            .Select((d, i) => (d, i))
            .Where(x => x.d.Type == Segra.Backend.Detection.EventType.Exclusion)
            .Select(x => x.i)
            .ToHashSet();
    }

    public void ProcessDetections(List<Segra.Backend.Detection.DetectionResult> detections, DateTime now)
    {
        var detectedClassIds = detections.Select(d => d.ClassId).ToHashSet();
        var detectedExclusions = detectedClassIds.Intersect(_exclusionEventIds).ToHashSet();

        // Update exclusion state
        if (detectedExclusions.Count > 0)
        {
            if (_exclusionActiveSince == null)
            {
                _exclusionActiveSince = now;
                _exclusionEventId = detectedExclusions.First();
                Log.Debug("TrainingEventLifecycle: exclusion started (class {ClassId})", _exclusionEventId);
            }
        }
        else
        {
            if (_exclusionActiveSince != null)
            {
                Log.Debug("TrainingEventLifecycle: exclusion ended (class {ClassId})", _exclusionEventId);
            }
            _exclusionActiveSince = null;
            _exclusionEventId = null;
        }

        bool exclusionActive = _exclusionActiveSince != null;

        // Process trigger detections
        var detectedTriggerIds = detectedClassIds
            .Where(id => !_exclusionEventIds.Contains(id))
            .ToHashSet();

        foreach (var classId in detectedTriggerIds)
        {
            if (exclusionActive)
            {
                Log.Debug("TrainingEventLifecycle: suppressing class {ClassId} (exclusion active)", classId);
                continue;
            }

            if (!_eventMap.TryGetValue(classId, out var def))
                continue;

            if (_activeTriggers.TryGetValue(classId, out var active))
            {
                // Extend existing event
                active.LastSeen = now;
            }
            else
            {
                // New event — create bookmark immediately (like game integrations do)
                _activeTriggers[classId] = new ActiveEvent
                {
                    Definition = def,
                    FirstSeen = now,
                    LastSeen = now
                };
                Log.Debug("TrainingEventLifecycle: new trigger started (class {ClassId})", classId);
                var newEvent = _activeTriggers[classId];
                CreateBookmark(newEvent);
                newEvent.BookmarkCreated = true;
            }
        }

        // Finalize events that are no longer detected
        var endedClasses = _activeTriggers.Keys
            .Where(id => !detectedTriggerIds.Contains(id))
            .ToList();

        foreach (var classId in endedClasses)
        {
            var evt = _activeTriggers[classId];
            _activeTriggers.Remove(classId);
            if (!evt.BookmarkCreated)
                CreateBookmark(evt);
        }
    }

    private static void CreateBookmark(ActiveEvent evt)
    {
        if (evt.Definition.BookmarkType == null) return;

        var recording = AppState.Instance.Recording;
        if (recording == null) return;

        var bookmarkTime = evt.FirstSeen - recording.StartTime;
        var bookmark = new Bookmark
        {
            Type = evt.Definition.BookmarkType.Value,
            Time = bookmarkTime
        };

        recording.AddBookmark(bookmark);
        Log.Information("TrainingEventLifecycle: bookmark created for '{EventName}' at {Time}",
            evt.Definition.Name, bookmarkTime);
    }

    public void Reset()
    {
        _activeTriggers.Clear();
        _exclusionActiveSince = null;
        _exclusionEventId = null;
    }
}
#endif
