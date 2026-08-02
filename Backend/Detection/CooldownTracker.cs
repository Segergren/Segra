using System.Collections.Concurrent;
using Segra.Backend.Core;
using Segra.Backend.Core.Models;
using Serilog;

namespace Segra.Backend.Detection;

public class CooldownTracker
{
    private readonly ConcurrentDictionary<int, DateTime> _lastDetectionTime = new();

    public bool CanDetect(int classId, int cooldownMs, DateTime now)
    {
        if (_lastDetectionTime.TryGetValue(classId, out var lastTime))
        {
            return (now - lastTime).TotalMilliseconds >= cooldownMs;
        }
        return true;
    }

    public void Record(int classId, DateTime now)
    {
        _lastDetectionTime[classId] = now;
    }

    public void CreateBookmark(DetectionResult result, EventDefinition definition, DateTime now)
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


