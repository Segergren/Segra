using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

// Start() needs a real ONNX model and a live OBS subscription, so the full Start/Stop cycle is
// not reachable from a unit test. What is reachable is the never-started path through Stop(),
// which is exactly the branch the Task -> Thread swap introduced (_detectionThread == null),
// plus the threading contracts DetectionLoop depends on.
public class DetectionThreadLifecycleTests
{
    private static async Task<bool> CompletesWithin(Action action, TimeSpan timeout)
    {
        var work = Task.Run(action);
        return await Task.WhenAny(work, Task.Delay(timeout)) == work;
    }

    [Fact]
    public async Task Stop_WithoutStart_DoesNotThrowOrBlock()
    {
        var detector = new VisualEventDetector();

        Assert.True(
            await CompletesWithin(detector.Stop, TimeSpan.FromSeconds(5)),
            "Stop() on a never-started detector blocked");
    }

    [Fact]
    public async Task StopCycling_WithoutStart_IsIdempotent()
    {
        var detector = new VisualEventDetector();

        for (int i = 0; i < 15; i++)
        {
            Assert.True(
                await CompletesWithin(detector.Stop, TimeSpan.FromSeconds(5)),
                $"Stop() blocked on cycle {i}");
        }
    }

    [Fact]
    public async Task Dispose_WithoutStart_DoesNotThrowOrBlock()
    {
        var detector = new VisualEventDetector();

        Assert.True(
            await CompletesWithin(detector.Dispose, TimeSpan.FromSeconds(5)),
            "Dispose() on a never-started detector blocked");
    }

    // await resumes on the thread pool at Normal priority, undoing the dedicated thread's
    // BelowNormal setting. DetectionLoop is synchronous to avoid this.
    [Fact]
    public void AwaitResumesOnThreadPool_WhichIsWhyDetectionLoopIsSynchronous()
    {
        ThreadPriority? beforeAwait = null;
        ThreadPriority? afterAwait = null;
        bool poolThreadAfterAwait = false;

        var thread = new Thread(() =>
        {
            RunAsync().GetAwaiter().GetResult();

            async Task RunAsync()
            {
                beforeAwait = Thread.CurrentThread.Priority;
                await Task.Delay(20);
                afterAwait = Thread.CurrentThread.Priority;
                poolThreadAfterAwait = Thread.CurrentThread.IsThreadPoolThread;
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "probe thread did not exit");

        Assert.Equal(ThreadPriority.BelowNormal, beforeAwait);
        Assert.Equal(ThreadPriority.Normal, afterAwait);
        Assert.True(poolThreadAfterAwait, "expected the continuation to resume on the thread pool");
    }

    // The converse of the above, and the property DetectionLoop actually relies on: a synchronous
    // loop stays on the thread it was started on, so the BelowNormal priority applies to every
    // iteration rather than just the first.
    [Fact]
    public void SynchronousLoop_StaysOnItsOwnThreadEveryIteration()
    {
        const int iterations = 5;
        var observed = new List<(int Id, bool Pool, ThreadPriority Priority)>();

        var thread = new Thread(() =>
        {
            using var cts = new CancellationTokenSource();
            for (int i = 0; i < iterations; i++)
            {
                cts.Token.WaitHandle.WaitOne(5);
                var t = Thread.CurrentThread;
                observed.Add((t.ManagedThreadId, t.IsThreadPoolThread, t.Priority));
            }
        })
        {
            IsBackground = true,
            Name = "Segra.VisualEventDetector",
            Priority = ThreadPriority.BelowNormal,
        };

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "probe thread did not exit");

        Assert.Equal(iterations, observed.Count);
        Assert.All(observed, o =>
        {
            Assert.Equal(thread.ManagedThreadId, o.Id);
            Assert.False(o.Pool, "loop escaped onto the thread pool");
            Assert.Equal(ThreadPriority.BelowNormal, o.Priority);
        });
    }

    // DetectionLoop breaks on `true` and treats `false` as "interval elapsed, go run a detection".
    // Inverting that would either spin the loop flat out or exit it immediately, and neither shows
    // up in the tests above, so the contract is pinned here.
    [Fact]
    public void TokenWaitHandle_ReturnsTrueOnCancellation_AndFalseOnTimeout()
    {
        using var notCancelled = new CancellationTokenSource();
        Assert.False(notCancelled.Token.WaitHandle.WaitOne(50), "timeout should return false");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.True(cancelled.Token.WaitHandle.WaitOne(50), "cancellation should return true");
    }

    // Cancelling mid-wait must wake the loop promptly, not leave it sleeping out the interval —
    // this is what keeps Stop()'s 3s join from timing out.
    [Fact]
    public void TokenWaitHandle_WakesPromptlyWhenCancelledMidWait()
    {
        using var cts = new CancellationTokenSource();
        var released = new ManualResetEventSlim();
        bool cancelledResult = false;

        var thread = new Thread(() =>
        {
            cancelledResult = cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            released.Set();
        })
        { IsBackground = true };

        thread.Start();
        Thread.Sleep(50);
        cts.Cancel();

        Assert.True(released.Wait(TimeSpan.FromSeconds(3)), "wait did not wake on cancellation");
        Assert.True(cancelledResult, "expected true (cancelled) rather than a 30s timeout");
        Assert.True(thread.Join(TimeSpan.FromSeconds(3)));
    }
}
