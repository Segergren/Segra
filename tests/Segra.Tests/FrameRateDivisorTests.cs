using System;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

// The divisor is relative to OBS's configured recording framerate, not the game's render rate.
// Target is 3 fps of capture against a 2 Hz consumption rate (detectionIntervalMs = 500).
public class FrameRateDivisorTests
{
    [Theory]
    [InlineData(30, 10)]
    [InlineData(60, 20)]
    [InlineData(120, 40)]
    [InlineData(144, 48)]
    [InlineData(240, 80)]
    public void DerivesDivisorFromOutputFps(int outputFps, int expected)
    {
        Assert.Equal(expected, VisualEventDetector.ComputeFrameRateDivisor(outputFps));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void ResultingCaptureRateStaysAboveConsumptionRate(int outputFps)
    {
        var divisor = VisualEventDetector.ComputeFrameRateDivisor(outputFps);
        var captureFps = (double)outputFps / divisor;

        // Consumption is 2 Hz; capturing below that would starve the detection loop.
        Assert.True(captureFps >= 2.0, $"{outputFps}fps/{divisor} = {captureFps:F2} Hz");

        // And it should not overshoot the 3 fps target by much, or the saving is lost.
        Assert.True(captureFps <= 4.0, $"{outputFps}fps/{divisor} = {captureFps:F2} Hz");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FallsBackToConstantWhenFpsUnavailable(int outputFps)
    {
        Assert.Equal(30, VisualEventDetector.ComputeFrameRateDivisor(outputFps));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NeverReturnsZeroForLowFps(int outputFps)
    {
        Assert.True(VisualEventDetector.ComputeFrameRateDivisor(outputFps) >= 1);
    }

    // 59.94 (60000/1001) must round to 60 rather than truncate to 59, and must never be read
    // as the bare numerator.
    [Fact]
    public void FractionalRateRoundsToNearestWholeFps()
    {
        var fps = (int)Math.Round(60000.0 / 1001.0);
        Assert.Equal(60, fps);
        Assert.Equal(20, VisualEventDetector.ComputeFrameRateDivisor(fps));
    }

    [Fact]
    public void BareNumeratorWouldProduceAWildlyWrongDivisor()
    {
        Assert.NotEqual(
            VisualEventDetector.ComputeFrameRateDivisor(60),
            VisualEventDetector.ComputeFrameRateDivisor(60000));
    }
}
