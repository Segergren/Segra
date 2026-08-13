using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Segra.Backend.Core.Models;
using Segra.Backend.Detection;
using Segra.Backend.Games;
using Xunit;

namespace Segra.Tests;

// GameIntegrationService used to hold the detector, its cooldown tracker and its event definitions
// in three separate mutable statics. The DetectionsAvailable callback snapshotted _eventDefinitions
// but read _cooldownTracker straight off the static on every detection — from the detector's own
// inference thread. Start() and Shutdown() null those fields from the caller's thread, so a teardown
// landing inside a cycle already in flight threw a NullReferenceException that the detection loop's
// catch-all logged as a Log.Warning and swallowed: bookmarks quietly stopped being created.
//
// The three are now one immutable DetectionSession, captured by the callback and swapped as a unit.
// These tests pin the property that makes the class of bug unwritable: a cycle in flight keeps
// working against the session it was wired to, whatever teardown does to the statics.
[Collection(RecordingStateCollection.Name)]
public class DetectionSessionTests
{
    private static GameIntegrationService.DetectionSession NewSession(params EventDefinition[] definitions)
        => new(new VisualEventDetector(), new CooldownTracker(), definitions);

    private static EventDefinition Trigger(int classId, BookmarkType bookmarkType) => new()
    {
        ClassId = classId,
        Name = $"trigger-{classId}",
        Type = EventType.Trigger,
        BookmarkType = bookmarkType,
    };

    private static EventDefinition Exclusion(int classId) => new()
    {
        ClassId = classId,
        Name = $"exclusion-{classId}",
        Type = EventType.Exclusion,
    };

    private static DetectionResult Detection(int classId) => new()
    {
        ClassId = classId,
        Confidence = 0.9f,
        Timestamp = DateTime.Now,
    };

    // CooldownTracker writes bookmarks onto AppState.Instance.Recording, which is the only
    // externally visible effect a detection cycle has. Whatever was there is put back so the
    // process-wide state is unchanged.
    private static List<Bookmark> CaptureBookmarks(Action body)
    {
        var previous = AppState.Instance.Recording;
        var recording = new Recording
        {
            Game = "Overwatch",
            FileName = "detection-session-test.mp4",
            StartTime = DateTime.Now,
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

    // The exact ordering the old code lost: teardown clears the service's state, and only then does
    // a detection cycle that was already running deliver its results.
    [Fact]
    public async Task HandleDetections_AfterShutdownClearedTheStatics_StillCreatesBookmarks()
    {
        var session = NewSession(Trigger(0, BookmarkType.Kill));

        await GameIntegrationService.Shutdown();

        var bookmarks = CaptureBookmarks(() =>
            GameIntegrationService.HandleDetections(session, new List<DetectionResult> { Detection(0) }));

        var bookmark = Assert.Single(bookmarks);
        Assert.Equal(BookmarkType.Kill, bookmark.Type);
    }

    // The suppression rule the callback carried before the refactor, pinned so moving it out of the
    // lambda did not change what reaches the timeline.
    [Fact]
    public void HandleDetections_SuppressesTriggersWhileAnExclusionIsPresent()
    {
        var session = NewSession(Trigger(0, BookmarkType.Kill), Exclusion(1));

        var bookmarks = CaptureBookmarks(() =>
            GameIntegrationService.HandleDetections(session,
                new List<DetectionResult> { Detection(0), Detection(1) }));

        Assert.Empty(bookmarks);
    }

    // A detection the definitions say nothing about must not reach the tracker at all — the old
    // lambda's null-definition guard, kept.
    [Fact]
    public void HandleDetections_IgnoresClassIdsWithNoDefinition()
    {
        var session = NewSession(Trigger(0, BookmarkType.Kill));

        var bookmarks = CaptureBookmarks(() =>
            GameIntegrationService.HandleDetections(session, new List<DetectionResult> { Detection(7) }));

        Assert.Empty(bookmarks);
    }

    // The race itself, run for real: one thread delivering detections against its own session while
    // another tears the service down over and over. Against the old three-static design this is the
    // shape that produced the swallowed NullReferenceException.
    [Fact]
    public async Task HandleDetections_RunningConcurrentlyWithShutdown_NeverFaults()
    {
        const int shutdowns = 50;
        const int maxCycles = 100_000;

        var session = NewSession(Trigger(0, BookmarkType.Kill), Exclusion(1));
        var detections = new List<DetectionResult> { Detection(0) };

        Exception? failure = null;
        var stop = new ManualResetEventSlim();

        var worker = new Thread(() =>
        {
            try
            {
                for (int i = 0; i < maxCycles && !stop.IsSet; i++)
                    GameIntegrationService.HandleDetections(session, detections);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        { IsBackground = true, Name = "DetectionSessionTests.worker" };

        worker.Start();
        for (int i = 0; i < shutdowns; i++)
            await GameIntegrationService.Shutdown();
        stop.Set();

        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "detection worker did not stop");
        Assert.Null(failure);
    }
}
