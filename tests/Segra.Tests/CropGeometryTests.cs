using System;
using System.Collections.Generic;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

// Two crop-geometry edge cases that produced wrong pixels and wrong coordinates in silence:
// a crop one pixel wide or tall, where ResizeGray's clamp went negative and sampled outside the
// crop, and a region overhanging a frame edge, where TryGetCropRect trimmed the rect the model saw
// but MapDetectionsToFullFrame scaled the boxes back by the untrimmed one.
public class CropGeometryTests
{
    private const int Dst = 4;

    private static byte[] Output(byte[] crop, int cropW, int cropH)
    {
        var dst = VisualEventDetector.ResizeGray(crop, cropW, cropH, Dst, Dst);
        // ArrayPool-rented, so the buffer may be longer than the destination.
        Assert.True(dst.Length >= Dst * Dst);
        return dst.AsSpan(0, Dst * Dst).ToArray();
    }

    // The source array is exactly one byte, so the old second tap at index 1 is an out-of-range
    // read rather than a silent pick-up of whatever followed the crop in a pooled buffer.
    [Fact]
    public void ResizeGray_On1x1Crop_ReadsOnlyThePixelItWasGiven()
    {
        var dst = Output([200], 1, 1);

        Assert.All(dst, b => Assert.Equal(200, b));
    }

    // Duplicating the only column cannot change the image, so it cannot change a single output
    // byte either. The 2px source takes the ordinary interpolation path, which makes it the
    // reference for what the degenerate one must produce.
    [Fact]
    public void ResizeGray_On1PxWideCrop_MatchesTheSameCropWithItsColumnDuplicated()
    {
        byte[] oneWide = [10, 100, 250];
        byte[] twoWide = [10, 10, 100, 100, 250, 250];

        Assert.Equal(Output(twoWide, 2, 3), Output(oneWide, 1, 3));
    }

    [Fact]
    public void ResizeGray_On1PxTallCrop_MatchesTheSameCropWithItsRowDuplicated()
    {
        byte[] oneTall = [10, 100, 250];
        byte[] twoTall = [10, 100, 250, 10, 100, 250];

        Assert.Equal(Output(twoTall, 3, 2), Output(oneTall, 3, 1));
    }

    [Fact]
    public void ResizeGray_On1x1Crop_MatchesA2x2CropOfTheSamePixel()
    {
        Assert.Equal(Output([77, 77, 77, 77], 2, 2), Output([77], 1, 1));
    }

    // A region wholly inside the frame is unaffected by the clamped-rect change.
    [Fact]
    public void MapDetectionsToFullFrame_ForARegionInsideTheFrame_ScalesByTheRegion()
    {
        const int frameW = 1000;
        const int frameH = 1000;
        var group = new RegionGroup { X = 0.25f, Y = 0.25f, W = 0.5f, H = 0.5f };

        Assert.True(VisualEventDetector.TryGetCropRect(group, frameW, frameH,
            out var cropX, out var cropY, out var cropW, out var cropH));
        Assert.Equal((250, 250, 500, 500), (cropX, cropY, cropW, cropH));

        var detections = new List<DetectionResult>
        {
            new() { X = 0.25f, Y = 0.25f, Width = 0.5f, Height = 0.5f }
        };

        VisualEventDetector.MapDetectionsToFullFrame(detections, cropX, cropY, cropW, cropH, frameW, frameH);

        var det = detections[0];
        Assert.Equal(0.375, det.X, 4);
        Assert.Equal(0.375, det.Y, 4);
        Assert.Equal(0.25, det.Width, 4);
        Assert.Equal(0.25, det.Height, 4);
    }

    // The regression: the crop handed to the model was 250px wide, not the 500px the group asks
    // for, so a box filling that crop covers the last quarter of the frame — not half of it
    // starting three quarters in, which would run 25% past the right edge.
    [Fact]
    public void MapDetectionsToFullFrame_ForARegionOverhangingTheFrameEdge_UsesTheClampedRect()
    {
        const int frameW = 1000;
        const int frameH = 1000;
        var group = new RegionGroup { X = 0.75f, Y = 0.5f, W = 0.5f, H = 0.75f };

        Assert.True(VisualEventDetector.TryGetCropRect(group, frameW, frameH,
            out var cropX, out var cropY, out var cropW, out var cropH));
        Assert.Equal((750, 500, 250, 500), (cropX, cropY, cropW, cropH));

        // A box filling the crop the model was actually given.
        var detections = new List<DetectionResult>
        {
            new() { X = 0f, Y = 0f, Width = 1f, Height = 1f }
        };

        VisualEventDetector.MapDetectionsToFullFrame(detections, cropX, cropY, cropW, cropH, frameW, frameH);

        var det = detections[0];
        Assert.Equal(0.75, det.X, 4);
        Assert.Equal(0.5, det.Y, 4);
        Assert.Equal(0.25, det.Width, 4);
        Assert.Equal(0.5, det.Height, 4);
        Assert.True(det.X + det.Width <= 1f, "box ran past the right edge of the frame");
        Assert.True(det.Y + det.Height <= 1f, "box ran past the bottom edge of the frame");
    }

    // A region-less event definition produces a (0, 0, 1, 1) group, whose crop is the frame, so the
    // mapping has to be the identity. Everything downstream reads these coordinates as frame-relative
    // — CooldownTracker matches instances by their overlap in exactly this space — so a full-frame
    // group that rescaled its boxes would put the whole class in the wrong place with no crop
    // geometry left to blame.
    [Fact]
    public void MapDetectionsToFullFrame_ForAFullFrameGroup_LeavesTheBoxUntouched()
    {
        const int frameW = 1920;
        const int frameH = 1080;
        var group = new RegionGroup { X = 0f, Y = 0f, W = 1f, H = 1f };

        Assert.True(VisualEventDetector.TryGetCropRect(group, frameW, frameH,
            out var cropX, out var cropY, out var cropW, out var cropH));

        var detections = new List<DetectionResult>
        {
            new() { X = 0.125f, Y = 0.75f, Width = 0.3f, Height = 0.2f }
        };

        VisualEventDetector.MapDetectionsToFullFrame(detections, cropX, cropY, cropW, cropH, frameW, frameH);

        var det = detections[0];
        Assert.Equal(0.125, det.X, 4);
        Assert.Equal(0.75, det.Y, 4);
        Assert.Equal(0.3, det.Width, 4);
        Assert.Equal(0.2, det.Height, 4);
    }
}
