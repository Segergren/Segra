using System.Collections.Concurrent;
using Segra.Backend.Core;
using Segra.Backend.Core.Models;
using Serilog;

namespace Segra.Backend.Detection;

public class CooldownTracker
{
    private const int DefaultLifetimeMs = 1200;

    // Detections arrive with no NMS, so one object is a cluster of near-identical boxes that all
    // have to fold into one instance. Hence a cutoff below the usual NMS ~0.45, which also absorbs
    // frame-to-frame drift; much lower starts fusing two adjacent kill-feed icons into one event.
    internal const float OverlapIouThreshold = 0.3f;

    // One bucket per class. Never removed: a model has a handful of classes, and a surviving empty
    // bucket stays a stable lock target rather than being racily re-created.
    private readonly ConcurrentDictionary<int, List<ActiveInstance>> _activeInstances = new();

    private sealed class ActiveInstance
    {
        public int LifetimeMs { get; init; }
        public DateTime FirstSeen { get; init; }
        public DateTime LastSeen { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }

    public void ProcessDetection(DetectionResult result, EventDefinition definition, DateTime now)
    {
        var classId = result.ClassId;
        var lifetimeMs = definition.LifetimeMs ?? DefaultLifetimeMs;
        var instances = _activeInstances.GetOrAdd(classId, _ => []);
        bool created;

        lock (instances)
        {
            // Also dropped here, not just in Cleanup: matching an elapsed window would extend an
            // event the timeline considers finished and swallow the new one's bookmark.
            DropExpired(instances, now);

            var match = FindOverlapping(instances, result);
            if (match != null)
            {
                match.LastSeen = now;
                // The instance follows its box, so a target walking across the screen stays one
                // instance; keeping the first box would let drift open a second event.
                match.X = result.X;
                match.Y = result.Y;
                match.Width = result.Width;
                match.Height = result.Height;

                Log.Debug("ProcessDetection: extending instance of class {ClassId} at ({X:F3},{Y:F3}), age {Age:F1}s",
                    classId, result.X, result.Y, (now - match.FirstSeen).TotalSeconds);
                created = false;
            }
            else
            {
                instances.Add(new ActiveInstance
                {
                    LifetimeMs = lifetimeMs,
                    FirstSeen = now,
                    LastSeen = now,
                    X = result.X,
                    Y = result.Y,
                    Width = result.Width,
                    Height = result.Height
                });

                Log.Debug("ProcessDetection: new instance of class {ClassId} at ({X:F3},{Y:F3}), {InstanceCount} active for the class",
                    classId, result.X, result.Y, instances.Count);
                created = true;
            }
        }

        // Outside the lock: bookmarking touches AppState and notifies listeners.
        if (created) CreateBookmark(result, definition, now);
    }

    public void Cleanup(DateTime now)
    {
        foreach (var (_, instances) in _activeInstances)
        {
            lock (instances)
            {
                DropExpired(instances, now);
            }
        }
    }

    // One by one rather than a class at a time: two eliminations overlapping in time run their own
    // windows, and the older one ending must not close the younger one with it.
    private static void DropExpired(List<ActiveInstance> instances, DateTime now)
    {
        for (int i = instances.Count - 1; i >= 0; i--)
        {
            var instance = instances[i];
            if ((now - instance.LastSeen).TotalMilliseconds < instance.LifetimeMs) continue;

            instances.RemoveAt(i);
            Log.Debug("DropExpired: instance expired after {Age:F1}s, last seen {Idle:F1}s ago",
                (instance.LastSeen - instance.FirstSeen).TotalSeconds, (now - instance.LastSeen).TotalSeconds);
        }
    }

    // Best overlap, not first over the line: an un-suppressed box can clear the cutoff against two
    // neighbouring instances, and belongs to the one it covers most.
    private static ActiveInstance? FindOverlapping(List<ActiveInstance> instances, DetectionResult result)
    {
        ActiveInstance? best = null;
        var bestIou = 0f;

        foreach (var instance in instances)
        {
            var iou = IntersectionOverUnion(instance, result);
            if (iou < OverlapIouThreshold || iou <= bestIou) continue;

            best = instance;
            bestIou = iou;
        }

        return best;
    }

    // Boxes are normalized with X/Y at the top-left corner, as ParseYoloOutput emits them.
    private static float IntersectionOverUnion(ActiveInstance instance, DetectionResult result)
    {
        var instanceArea = instance.Width * instance.Height;
        var resultArea = result.Width * result.Height;

        // A zero-area box carries no position, and overlap against it would read 0 forever and
        // spawn a fresh instance every frame. Falls back to matching on class alone.
        if (instanceArea <= 0 || resultArea <= 0) return 1f;

        var left = MathF.Max(instance.X, result.X);
        var top = MathF.Max(instance.Y, result.Y);
        var right = MathF.Min(instance.X + instance.Width, result.X + result.Width);
        var bottom = MathF.Min(instance.Y + instance.Height, result.Y + result.Height);

        var overlapWidth = right - left;
        var overlapHeight = bottom - top;
        if (overlapWidth <= 0 || overlapHeight <= 0) return 0f;

        var intersection = overlapWidth * overlapHeight;
        return intersection / (instanceArea + resultArea - intersection);
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
        Log.Information("CreateBookmark: created {Type} bookmark for '{EventName}' at ({X:F3},{Y:F3})",
            definition.BookmarkType.Value, definition.Name, result.X, result.Y);
    }
}
