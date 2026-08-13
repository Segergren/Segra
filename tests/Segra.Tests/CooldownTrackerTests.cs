using System;
using System.Collections.Generic;
using Segra.Backend.Core.Models;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

// CooldownTracker used to key its active window on classId alone: the first elimination opened a
// window for the class and every later detection of that class inside the lifetime — wherever it
// was on screen — was folded into it. Two eliminations a second apart produced one bookmark, and
// the class could not tell them apart because it never read DetectionResult's box.
//
// An instance is now class plus position: a detection extends an existing instance only when it
// overlaps that instance's last box, and one that does not opens an instance and a bookmark of its
// own. The instance follows its box across frames, so cooldown stays region-independent — drift is
// one event, not a bookmark per frame.
[Collection(RecordingStateCollection.Name)]
public class CooldownTrackerTests
{
    private static readonly DateTime Origin = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

    private static EventDefinition Trigger(int lifetimeMs = 1200) => new()
    {
        ClassId = 0,
        Name = "elimination",
        Type = EventType.Trigger,
        BookmarkType = BookmarkType.Kill,
        LifetimeMs = lifetimeMs,
    };

    private static DetectionResult Box(float x, float y, float w, float h, int classId = 0) => new()
    {
        ClassId = classId,
        Confidence = 0.9f,
        X = x,
        Y = y,
        Width = w,
        Height = h,
        Timestamp = Origin,
    };

    // Bookmarks land on AppState.Instance.Recording, which is the tracker's only externally visible
    // effect. Whatever was installed is put back so the process-wide state is unchanged.
    private static List<Bookmark> CaptureBookmarks(Action body)
    {
        var previous = AppState.Instance.Recording;
        var recording = new Recording
        {
            Game = "Overwatch",
            FileName = "cooldown-tracker-test.mp4",
            StartTime = Origin,
        };
        AppState.Instance.Recording = recording;

        try
        {
            body();
            return recording.Bookmarks;
        }
        finally
        {
            AppState.Instance.Recording = previous;
        }
    }

    // THE load-bearing case. Nothing runs NMS between the detect head and the tracker, so one
    // elimination icon reaches ProcessDetection as a cluster of near-duplicate boxes in a single
    // frame. Keying on class plus overlap has to absorb the cluster into one instance; if the merge
    // is missing or the cutoff is too strict, this frame alone writes six bookmarks for one kill.
    [Fact]
    public void ProcessDetection_RawDuplicateBoxesForOneObject_CreateOneBookmark()
    {
        var tracker = new CooldownTracker();
        var definition = Trigger();

        // Same icon, as the un-suppressed anchors around it see it: a few thousandths of jitter in
        // both corner and extent.
        var cluster = new[]
        {
            Box(0.400f, 0.300f, 0.120f, 0.060f),
            Box(0.406f, 0.296f, 0.118f, 0.062f),
            Box(0.395f, 0.303f, 0.124f, 0.058f),
            Box(0.404f, 0.305f, 0.116f, 0.061f),
            Box(0.394f, 0.298f, 0.122f, 0.063f),
            Box(0.402f, 0.306f, 0.119f, 0.057f),
        };

        var bookmarks = CaptureBookmarks(() =>
        {
            foreach (var detection in cluster)
                tracker.ProcessDetection(detection, definition, Origin);
        });

        var bookmark = Assert.Single(bookmarks);
        Assert.Equal(BookmarkType.Kill, bookmark.Type);
    }

    // The same cluster re-detected every frame while the icon is on screen: still one event.
    [Fact]
    public void ProcessDetection_DuplicateBoxesAcrossConsecutiveFrames_CreateOneBookmark()
    {
        var tracker = new CooldownTracker();
        var definition = Trigger(lifetimeMs: 300);

        var bookmarks = CaptureBookmarks(() =>
        {
            for (int frame = 0; frame < 8; frame++)
            {
                var now = Origin.AddMilliseconds(frame * 33);
                tracker.ProcessDetection(Box(0.400f, 0.300f, 0.120f, 0.060f), definition, now);
                tracker.ProcessDetection(Box(0.404f, 0.297f, 0.117f, 0.062f), definition, now);
                tracker.ProcessDetection(Box(0.396f, 0.302f, 0.123f, 0.059f), definition, now);
                tracker.Cleanup(now);
            }
        });

        Assert.Single(bookmarks);
    }

    // Two boxes of the same class that overlap are the same object seen twice, whatever the gap
    // between them — this is the old behaviour, and it has to survive the rewrite.
    [Fact]
    public void ProcessDetection_OverlappingDetectionsOfSameClass_CreateOneBookmark()
    {
        var tracker = new CooldownTracker();
        var definition = Trigger();

        var bookmarks = CaptureBookmarks(() =>
        {
            tracker.ProcessDetection(Box(0.100f, 0.100f, 0.100f, 0.050f), definition, Origin);
            tracker.ProcessDetection(Box(0.110f, 0.100f, 0.100f, 0.050f), definition, Origin.AddMilliseconds(400));
        });

        Assert.Single(bookmarks);
    }

    // The bug this class exists to fix: two eliminations inside one lifetime window, on opposite
    // sides of the screen. Class-only keying collapsed them into a single bookmark.
    [Fact]
    public void ProcessDetection_DisjointDetectionsOfSameClass_CreateSeparateBookmarks()
    {
        var tracker = new CooldownTracker();
        var definition = Trigger(lifetimeMs: 5000);

        var bookmarks = CaptureBookmarks(() =>
        {
            tracker.ProcessDetection(Box(0.100f, 0.100f, 0.100f, 0.050f), definition, Origin);
            tracker.ProcessDetection(Box(0.700f, 0.600f, 0.100f, 0.050f), definition, Origin.AddMilliseconds(500));
        });

        Assert.Equal(2, bookmarks.Count);
    }

    // Same frame, boxes on top of each other, different classes: overlap does not merge across
    // classes, so an elimination and a death at the same screen position stay two events.
    [Fact]
    public void ProcessDetection_OverlappingDetectionsOfDifferentClasses_CreateSeparateBookmarks()
    {
        var tracker = new CooldownTracker();
        var kill = Trigger();
        var death = new EventDefinition
        {
            ClassId = 1,
            Name = "death",
            Type = EventType.Trigger,
            BookmarkType = BookmarkType.Death,
        };

        var bookmarks = CaptureBookmarks(() =>
        {
            tracker.ProcessDetection(Box(0.400f, 0.300f, 0.120f, 0.060f), kill, Origin);
            tracker.ProcessDetection(Box(0.402f, 0.301f, 0.119f, 0.061f, classId: 1), death, Origin);
        });

        Assert.Equal(2, bookmarks.Count);
        Assert.Contains(bookmarks, b => b.Type == BookmarkType.Kill);
        Assert.Contains(bookmarks, b => b.Type == BookmarkType.Death);
    }

    // Cooldown is region-independent: an instance is matched against where it was last seen, not
    // where it started. The box below walks far enough that its last position shares no pixels with
    // its first, which would be two events if the instance kept its opening box.
    [Fact]
    public void ProcessDetection_BoxDriftingAcrossFrames_StaysOneInstance()
    {
        var tracker = new CooldownTracker();
        var definition = Trigger(lifetimeMs: 300);

        var first = Box(0.100f, 0.400f, 0.120f, 0.060f);
        var last = Box(0.100f + 9 * 0.020f, 0.400f, 0.120f, 0.060f);
        Assert.True(last.X > first.X + first.Width, "drift must end clear of the opening box");

        var bookmarks = CaptureBookmarks(() =>
        {
            for (int frame = 0; frame < 10; frame++)
            {
                var now = Origin.AddMilliseconds(frame * 100);
                tracker.ProcessDetection(Box(0.100f + frame * 0.020f, 0.400f, 0.120f, 0.060f), definition, now);
                tracker.Cleanup(now);
            }
        });

        Assert.Single(bookmarks);
    }

    // The window still closes on time: the same box, seen again after its lifetime elapsed, is a
    // second elimination in the same spot and gets its own bookmark.
    [Fact]
    public void ProcessDetection_SameBoxAfterLifetimeElapsed_CreatesSecondBookmark()
    {
        var tracker = new CooldownTracker();
        var definition = Trigger(lifetimeMs: 300);

        var bookmarks = CaptureBookmarks(() =>
        {
            tracker.ProcessDetection(Box(0.400f, 0.300f, 0.120f, 0.060f), definition, Origin);
            tracker.ProcessDetection(Box(0.400f, 0.300f, 0.120f, 0.060f), definition, Origin.AddMilliseconds(299));
            Assert.Single(AppState.Instance.Recording!.Bookmarks);

            // The clock runs from the last sighting, not the first: 601ms after the opening
            // detection is only 302ms after the one that extended it, and that is what closes the
            // window.
            tracker.ProcessDetection(Box(0.400f, 0.300f, 0.120f, 0.060f), definition, Origin.AddMilliseconds(601));
        });

        Assert.Equal(2, bookmarks.Count);
    }

    // Instances expire one at a time. Two live at once, only one keeps being seen; Cleanup must
    // retire the stale one and leave the other's window open.
    [Fact]
    public void Cleanup_ExpiresInstancesIndividually()
    {
        var tracker = new CooldownTracker();
        var definition = Trigger(lifetimeMs: 300);

        var left = Box(0.100f, 0.100f, 0.100f, 0.050f);
        var right = Box(0.700f, 0.600f, 0.100f, 0.050f);

        var bookmarks = CaptureBookmarks(() =>
        {
            tracker.ProcessDetection(left, definition, Origin);
            tracker.ProcessDetection(right, definition, Origin);
            Assert.Equal(2, AppState.Instance.Recording!.Bookmarks.Count);

            // Only the left box is still on screen.
            tracker.ProcessDetection(left, definition, Origin.AddMilliseconds(200));
            tracker.Cleanup(Origin.AddMilliseconds(350));

            // The left instance was refreshed at 200ms and is still inside its window.
            tracker.ProcessDetection(left, definition, Origin.AddMilliseconds(400));

            // The right one was not, so this is a new event in that spot.
            tracker.ProcessDetection(right, definition, Origin.AddMilliseconds(400));
        });

        Assert.Equal(3, bookmarks.Count);
    }

    // Expired instances do not accumulate: an object that reappears in the same place every window
    // must leave one instance behind, not one per bookmark it ever wrote.
    [Fact]
    public void Cleanup_DropsExpiredInstancesEvenWhenNothingOverlapsThem()
    {
        var tracker = new CooldownTracker();
        var definition = Trigger(lifetimeMs: 300);

        var bookmarks = CaptureBookmarks(() =>
        {
            for (int round = 0; round < 5; round++)
            {
                var now = Origin.AddMilliseconds(round * 1000);
                tracker.ProcessDetection(Box(0.400f, 0.300f, 0.120f, 0.060f), definition, now);
                tracker.Cleanup(now.AddMilliseconds(500));
            }
        });

        Assert.Equal(5, bookmarks.Count);
    }
}
