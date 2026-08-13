using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Segra.Backend.Detection;
using Segra.Backend.Games;
using Xunit;

namespace Segra.Tests;

// Start() used to stop the running detector only inside the branch that successfully starts a new
// one. Every path that declines — no game name, no model on disk, integration toggled off — left
// the previous game's detector subscribed to OBS raw video, running its inference thread, and
// appending bookmarks to AppState.Instance.Recording, which by then belonged to a different game.
//
// The teardown now sits above all of those branches. That placement is the whole fix, and nothing
// else in the suite exercises it: the other lifecycle tests only cover Stop()/Dispose() on a
// detector that was never started, and DetectionSessionTests calls Shutdown() as a precondition
// without asserting anything about the previous detector. These tests pin the decline path.
[Collection(RecordingStateCollection.Name)]
public class DetectorTeardownOnGameSwitchTests
{
    private static readonly FieldInfo SessionField =
        typeof(GameIntegrationService).GetField("_detectionSession", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("GameIntegrationService._detectionSession not found");

    // Seeding the token source is what makes "was it stopped?" observable: Stop() cancels it and
    // never disposes it, and a detector that was never started has no thread or subscription to
    // watch instead.
    private static readonly FieldInfo CtsField =
        typeof(VisualEventDetector).GetField("_cts", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("VisualEventDetector._cts not found");

    private static (GameIntegrationService.DetectionSession Session, CancellationTokenSource Cts) SeedRunningSession()
    {
        var detector = new VisualEventDetector();
        var cts = new CancellationTokenSource();
        CtsField.SetValue(detector, cts);

        var session = new GameIntegrationService.DetectionSession(
            detector, new CooldownTracker(), Array.Empty<EventDefinition>());
        SessionField.SetValue(null, session);

        return (session, cts);
    }

    // igdbId and gameName both null: no integration in the if-chain matches, and ResolveModelId
    // returns null so the ML branch declines before it looks for a model. This is precisely the
    // path that used to skip the teardown.
    [Fact]
    public async Task Start_WhenItDeclinesToStartDetection_StopsThePreviousDetector()
    {
        var (_, cts) = SeedRunningSession();

        try
        {
            await GameIntegrationService.Start(null, null);

            Assert.Null(SessionField.GetValue(null));
            Assert.True(cts.IsCancellationRequested,
                "the previous game's detector was left running when Start() declined to begin ML detection");
        }
        finally
        {
            SessionField.SetValue(null, null);
            cts.Dispose();
        }
    }

    // The same guarantee for a recognised game that simply has no model shipped for it: the
    // HasModelForGame miss must not be a path that skips teardown either.
    [Fact]
    public async Task Start_ForAGameWithNoModelOnDisk_StopsThePreviousDetector()
    {
        var (_, cts) = SeedRunningSession();

        try
        {
            await GameIntegrationService.Start(igdbId: null, gameName: "A Game That Ships No Model");

            Assert.Null(SessionField.GetValue(null));
            Assert.True(cts.IsCancellationRequested,
                "the previous game's detector was left running when the new game had no model");
        }
        finally
        {
            SessionField.SetValue(null, null);
            cts.Dispose();
        }
    }

    // Shutdown() is the other way a session ends; it shares the swap-and-stop path with Start().
    [Fact]
    public async Task Shutdown_StopsTheRunningDetector()
    {
        var (_, cts) = SeedRunningSession();

        try
        {
            await GameIntegrationService.Shutdown();

            Assert.Null(SessionField.GetValue(null));
            Assert.True(cts.IsCancellationRequested, "Shutdown() left the detector running");
        }
        finally
        {
            SessionField.SetValue(null, null);
            cts.Dispose();
        }
    }
}
