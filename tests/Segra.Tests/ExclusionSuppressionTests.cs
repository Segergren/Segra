using System;
using System.Collections.Generic;
using Segra.Backend.Core.Models;
using Segra.Backend.Detection;
using Segra.Backend.Games;
using Xunit;

namespace Segra.Tests;

// HandleDetections runs one exclusion pass over the whole batch before it processes anything, so a
// single exclusion detection vetoes every trigger the same cycle produced — a kill cam or a
// spectated death on screen means the eliminations detected alongside it are not the player's.
//
// DetectionSessionTests pins that the rule survived the move out of the callback lambda, with one
// trigger and one exclusion. These tests pin the parts of the rule that a plausible rewrite would
// break without failing that one: a per-detection scan that only looked at what it had already
// walked past would let the triggers ahead of the exclusion through, and folding the scan into the
// processing loop would let a suppressed trigger open a cooldown instance anyway.
[Collection(RecordingStateCollection.Name)]
public class ExclusionSuppressionTests
{
    private const int ExclusionClassId = 3;
    private const int UndefinedClassId = 9;

    private static GameIntegrationService.DetectionSession NewSession(
        BookmarkType? exclusionBookmarkType = null) =>
        new(new VisualEventDetector(), new CooldownTracker(),
        [
            Trigger(0, BookmarkType.Kill),
            Trigger(1, BookmarkType.Assist),
            Trigger(2, BookmarkType.Goal),
            new EventDefinition
            {
                ClassId = ExclusionClassId,
                Name = "kill-cam",
                Type = EventType.Exclusion,
                BookmarkType = exclusionBookmarkType,
            },
        ]);

    private static EventDefinition Trigger(int classId, BookmarkType bookmarkType) => new()
    {
        ClassId = classId,
        Name = $"trigger-{classId}",
        Type = EventType.Trigger,
        BookmarkType = bookmarkType,
    };

    // Boxes are spread apart so CooldownTracker treats each one as its own instance: a control run
    // has to be able to produce one bookmark per trigger, otherwise "no bookmarks" would prove
    // nothing about suppression.
    private static DetectionResult Detection(int classId) => new()
    {
        ClassId = classId,
        Confidence = 0.9f,
        X = 0.1f * classId,
        Y = 0.1f * classId,
        Width = 0.05f,
        Height = 0.05f,
        Timestamp = DateTime.Now,
    };

    // Bookmarks land on AppState.Instance.Recording, which is the only externally visible effect a
    // detection cycle has. Whatever was installed is put back so the process-wide state is unchanged.
    private static List<Bookmark> CaptureBookmarks(Action body)
    {
        var previous = AppState.Instance.Recording;
        var recording = new Recording
        {
            Game = "Overwatch",
            FileName = "exclusion-suppression-test.mp4",
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

    private static List<DetectionResult> BatchWithExclusionAt(int index)
    {
        var batch = new List<DetectionResult> { Detection(0), Detection(1), Detection(2) };
        batch.Insert(index, Detection(ExclusionClassId));
        return batch;
    }

    // The batch is not ordered — the region groups are walked in whatever order BuildRegionGroups
    // merged them into, so the exclusion can land anywhere in the list, including after every
    // trigger it is supposed to veto.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ExclusionAnywhereInTheBatch_SuppressesEveryTrigger(int exclusionIndex)
    {
        var session = NewSession();

        var bookmarks = CaptureBookmarks(() =>
            GameIntegrationService.HandleDetections(session, BatchWithExclusionAt(exclusionIndex)));

        Assert.Empty(bookmarks);
    }

    // The control for the theory above: without the exclusion detection these same three triggers
    // each write a bookmark, so "empty" there is suppression rather than the batch never having
    // been able to produce anything.
    [Fact]
    public void WithoutAnExclusionDetection_EveryTriggerInTheBatchBookmarks()
    {
        var session = NewSession();

        var bookmarks = CaptureBookmarks(() =>
            GameIntegrationService.HandleDetections(session,
                new List<DetectionResult> { Detection(0), Detection(1), Detection(2) }));

        Assert.Equal(3, bookmarks.Count);
    }

    // Suppression is keyed on the definition's type, not on "some detection the definitions do not
    // cover". A class the model emits but events.json says nothing about is already ignored on the
    // trigger side; treating it as an exclusion would silently blank out whole cycles.
    [Fact]
    public void DetectionWithNoDefinition_DoesNotSuppressTheBatch()
    {
        var session = NewSession();

        var bookmarks = CaptureBookmarks(() =>
            GameIntegrationService.HandleDetections(session,
                new List<DetectionResult> { Detection(0), Detection(UndefinedClassId) }));

        var bookmark = Assert.Single(bookmarks);
        Assert.Equal(BookmarkType.Kill, bookmark.Type);
    }

    // A suppressed trigger must not reach the cooldown tracker at all. If it did, it would open an
    // instance with no bookmark, and the next cycle's detection of the same object would fold into
    // that instance — so the event would be lost for a whole lifetime window rather than for the one
    // cycle the exclusion was on screen.
    [Fact]
    public void SuppressedTrigger_LeavesNoCooldownInstanceBehind()
    {
        var session = NewSession();

        var bookmarks = CaptureBookmarks(() =>
        {
            GameIntegrationService.HandleDetections(session,
                new List<DetectionResult> { Detection(0), Detection(ExclusionClassId) });

            // Same box, immediately afterwards and well inside the default 1200ms lifetime: this is
            // the cycle after the kill cam cleared.
            GameIntegrationService.HandleDetections(session,
                new List<DetectionResult> { Detection(0) });
        });

        var bookmark = Assert.Single(bookmarks);
        Assert.Equal(BookmarkType.Kill, bookmark.Type);
    }

    // An exclusion is a veto, never an event of its own — even if someone gives its definition a
    // bookmark type, which events.json's schema does not stop them from doing.
    [Fact]
    public void ExclusionDetection_NeverBookmarksItself()
    {
        var session = NewSession(exclusionBookmarkType: BookmarkType.Death);

        var bookmarks = CaptureBookmarks(() =>
            GameIntegrationService.HandleDetections(session,
                new List<DetectionResult> { Detection(ExclusionClassId) }));

        Assert.Empty(bookmarks);
    }
}
