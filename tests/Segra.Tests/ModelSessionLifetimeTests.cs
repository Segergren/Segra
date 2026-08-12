using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

// ModelService cached InferenceSessions in a ConcurrentDictionary keyed by game id, with two holes:
//
//   * GetOrAdd runs its factory outside the lock, so two callers racing on a cold cache each built a
//     ~10 MB native session and the loser was thrown away undisposed — a straight native leak.
//   * UnloadModel disposed the cached session with no notion of how many detectors were using it,
//     so stopping one detector pulled the session out from under another still running the game.
//
// The cache now builds through a Lazy (one construction, ever) and reference counts users (one
// disposal, after the last release). These tests load the real ONNX model — the contract is about
// native session lifetime and cannot be checked without the runtime.
[Collection(ModelSessionCollection.Name)]
public class ModelSessionLifetimeTests
{
    private const string GameId = "Overwatch";
    private const int ModelInput = 640;

    private static void AssertModelIsOnDisk()
    {
        // Fail loudly rather than skip, matching InputTensorReuseTests: a guard that quietly
        // disables itself where the model is absent is worse than no guard at all.
        var modelPath = ModelService.GetModelPath(GameId);
        Assert.True(File.Exists(modelPath),
            $"ONNX model not found at {modelPath}. This test guards native session lifetime and " +
            "cannot be verified without the real model. It must fail, not skip.");
    }

    // Proves the session is still alive, which a disposed handle cannot fake: Run on a disposed
    // InferenceSession throws rather than returning results.
    private static void AssertStillUsable(InferenceSession session)
    {
        var buffer = new float[ModelInput * ModelInput * 3];
        var tensor = new DenseTensor<float>(buffer.AsMemory(), new[] { 1, 3, ModelInput, ModelInput });
        var container = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(session.InputNames[0], tensor)
        };
        using var runOptions = new RunOptions();
        using var results = session.Run(container, session.OutputMetadata.Keys.ToList(), runOptions);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void UnloadingOneUser_LeavesTheSessionUsableForTheOther()
    {
        AssertModelIsOnDisk();

        var baseline = ModelService.GetSessionRefCount(GameId);
        var first = ModelService.LoadModel(GameId);
        var second = ModelService.LoadModel(GameId);

        try
        {
            Assert.Same(first, second);
            Assert.Equal(baseline + 2, ModelService.GetSessionRefCount(GameId));

            // The regression: this used to dispose the shared session outright, and the surviving
            // detector's next inference hit a freed native handle.
            ModelService.UnloadModel(GameId);
            Assert.Equal(baseline + 1, ModelService.GetSessionRefCount(GameId));
            AssertStillUsable(second);
        }
        finally
        {
            ModelService.UnloadModel(GameId);
        }

        Assert.Equal(baseline, ModelService.GetSessionRefCount(GameId));
    }

    // The GetOrAdd race, driven directly: every caller must come back holding the same native
    // session, and every caller must be counted so none of them can be freed underneath.
    [Fact]
    public void ConcurrentLoads_ShareOneSession_AndEachTakesAReference()
    {
        AssertModelIsOnDisk();

        const int callers = 8;
        var baseline = ModelService.GetSessionRefCount(GameId);
        var sessions = new InferenceSession?[callers];
        var failures = new Exception?[callers];
        var released = 0;

        using (var gate = new ManualResetEventSlim())
        {
            var threads = new Thread[callers];
            for (int i = 0; i < callers; i++)
            {
                var index = i;
                threads[index] = new Thread(() =>
                {
                    try
                    {
                        gate.Wait();
                        sessions[index] = ModelService.LoadModel(GameId);
                    }
                    catch (Exception ex)
                    {
                        failures[index] = ex;
                    }
                })
                { IsBackground = true, Name = $"ModelSessionLifetimeTests.loader{index}" };
                threads[index].Start();
            }

            // Released together so the loads genuinely overlap on a cold-ish cache rather than
            // queueing behind each other.
            gate.Set();

            foreach (var thread in threads)
                Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "a concurrent LoadModel never returned");
        }

        try
        {
            Assert.All(failures, Assert.Null);
            Assert.All(sessions, s => Assert.NotNull(s));
            Assert.All(sessions, s => Assert.Same(sessions[0], s));
            Assert.Equal(baseline + callers, ModelService.GetSessionRefCount(GameId));

            // Every release but the last must leave the session intact.
            for (; released < callers - 1; released++)
            {
                ModelService.UnloadModel(GameId);
                Assert.Equal(baseline + callers - released - 1, ModelService.GetSessionRefCount(GameId));
            }

            AssertStillUsable(sessions[0]!);
        }
        finally
        {
            for (; released < callers; released++)
                ModelService.UnloadModel(GameId);
        }

        Assert.Equal(baseline, ModelService.GetSessionRefCount(GameId));
    }

    // A construction that throws must leave nothing behind. Lazy caches the exception it produced,
    // so the failed entry has to be evicted along with its reference — otherwise a model that was
    // missing once (mid-download, mid-update) would keep replaying that same exception for the rest
    // of the process even after the file appeared.
    [Fact]
    public void FailedLoad_LeavesNoReferenceAndNoPoisonedEntry()
    {
        const string missing = "not-a-real-game-for-session-lifetime";

        var first = Assert.Throws<FileNotFoundException>(() => ModelService.LoadModel(missing));
        Assert.Equal(0, ModelService.GetSessionRefCount(missing));

        var second = Assert.Throws<FileNotFoundException>(() => ModelService.LoadModel(missing));
        Assert.NotSame(first, second);
        Assert.Equal(0, ModelService.GetSessionRefCount(missing));
    }

    // VisualEventDetector.Stop() calls UnloadModel on a game it may never have loaded (Start can
    // throw between setting _gameId and loading), so an unmatched release has to be inert rather
    // than driving a count negative.
    [Fact]
    public void UnloadWithoutLoad_IsInert()
    {
        const string missing = "not-a-real-game-never-loaded";

        ModelService.UnloadModel(missing);
        ModelService.UnloadModel(missing);

        Assert.Equal(0, ModelService.GetSessionRefCount(missing));
    }
}
