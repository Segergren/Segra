using System;
using System.Collections.Generic;
using System.Linq;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

// A definition without a screen region makes BuildRegionGroups append a full-frame group.
// Converting that group's crop per-group means converting every pixel in the frame, on top of
// every other group's crop — more work than the whole-frame conversion the crop-before-greyscale
// path replaced. These tests pin the strategy choice and prove both strategies stay byte-identical.
public class FullFrameGroupTests
{
    private const int W = 1920;
    private const int H = 1080;
    private const int ModelInput = 640;

    // The 7 event definitions in data/training/Overwatch/events.json, the only shipped config.
    private static List<EventDefinition> OverwatchDefinitions() =>
    [
        Def(0, 0.2583f, 0.5101f, 0.5375f, 0.3161f),
        Def(1, 0.1028f, 0.0336f, 0.1236f, 0.0469f),
        Def(2, 0.2583f, 0.5101f, 0.5375f, 0.3161f),
        Def(3, 0.4681f, 0.8706f, 0.0708f, 0.0543f),
        Def(4, 0.2583f, 0.5101f, 0.5375f, 0.3161f),
        Def(5, 0.0097f, 0.0114f, 0.1514f, 0.0568f),
        Def(6, 0.45f, 0.6f, 0.2f, 0.15f),
    ];

    private static EventDefinition Def(int classId, float x, float y, float w, float h) =>
        new()
        {
            Id = classId,
            ClassId = classId,
            Type = EventType.Trigger,
            ScreenRegionX = x,
            ScreenRegionY = y,
            ScreenRegionW = w,
            ScreenRegionH = h
        };

    // No ScreenRegionW, so BuildRegionGroups treats it as covering the whole frame.
    private static EventDefinition FullFrameDef(int classId) =>
        new() { Id = classId, ClassId = classId, Type = EventType.Trigger };

    [Fact]
    public void BuildRegionGroups_appends_full_frame_group_for_region_less_definition()
    {
        var definitions = OverwatchDefinitions();
        definitions.Add(FullFrameDef(7));

        var groups = VisualEventDetector.BuildRegionGroups(definitions);

        Assert.Equal(4, groups.Count);
        Assert.Contains(groups, g => g.X == 0f && g.Y == 0f && g.W == 1f && g.H == 1f);

        // The three real groups survive alongside it rather than being absorbed.
        foreach (var (x, y, w, h) in ReferenceImplementations.OverwatchGroups)
        {
            Assert.Contains(groups, g =>
                Math.Abs(g.X - x) < 1e-4f && Math.Abs(g.Y - y) < 1e-4f &&
                Math.Abs(g.W - w) < 1e-4f && Math.Abs(g.H - h) < 1e-4f);
        }
    }

    // The regression guard. Greyscaling more pixels than the frame holds means the per-group
    // path is doing strictly more work than converting the frame once would.
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(1280, 720)]
    public void Cycle_never_converts_more_pixels_than_the_frame_holds(int frameW, int frameH)
    {
        var definitions = OverwatchDefinitions();
        definitions.Add(FullFrameDef(7));
        var groups = VisualEventDetector.BuildRegionGroups(definitions);

        var converted = VisualEventDetector.CountGrayscalePixels(groups, frameW, frameH);

        Assert.True(converted <= frameW * frameH,
            $"converted {converted} pixels, frame holds {frameW * frameH}");
    }

    [Fact]
    public void Full_frame_group_set_converts_the_frame_exactly_once()
    {
        var definitions = OverwatchDefinitions();
        definitions.Add(FullFrameDef(7));
        var groups = VisualEventDetector.BuildRegionGroups(definitions);

        Assert.Equal(VisualEventDetector.GrayscaleStrategy.WholeFrameOnce,
            VisualEventDetector.SelectGrayscaleStrategy(groups));
        Assert.Equal(W * H, VisualEventDetector.CountGrayscalePixels(groups, W, H));
    }

    // The only shipped config must keep the per-group path; this fix is for the case it does
    // not hit. 390,526 is the sum of the three real crop areas at 1920x1080.
    [Fact]
    public void Overwatch_config_keeps_the_per_group_path()
    {
        var groups = VisualEventDetector.BuildRegionGroups(OverwatchDefinitions());

        Assert.Equal(VisualEventDetector.GrayscaleStrategy.PerGroupCrop,
            VisualEventDetector.SelectGrayscaleStrategy(groups));
        Assert.Equal(390_526, VisualEventDetector.CountGrayscalePixels(groups, W, H));
    }

    // Overlapping groups can pass full coverage without any one of them being full-frame.
    [Fact]
    public void Coverage_above_the_frame_switches_strategy_without_a_full_frame_group()
    {
        var groups = new List<RegionGroup>
        {
            new() { X = 0f, Y = 0f, W = 0.8f, H = 0.9f },
            new() { X = 0.1f, Y = 0.05f, W = 0.8f, H = 0.9f },
        };

        Assert.Equal(VisualEventDetector.GrayscaleStrategy.WholeFrameOnce,
            VisualEventDetector.SelectGrayscaleStrategy(groups));
        Assert.Equal(W * H, VisualEventDetector.CountGrayscalePixels(groups, W, H));
    }

    // The same guarantee CropThenGray_equals_GrayThenCrop gives the real regions, extended to a
    // group set containing (0, 0, 1, 1) — the case neither golden test covered.
    [Fact]
    public void Both_strategies_produce_identical_buffers_for_a_full_frame_group_set()
    {
        var definitions = OverwatchDefinitions();
        definitions.Add(FullFrameDef(7));
        var groups = VisualEventDetector.BuildRegionGroups(definitions);
        var bgra = ReferenceImplementations.SyntheticBgra(W, H, 23);

        Assert.Contains(groups, g => g.W == 1f && g.H == 1f);

        var frameGray = VisualEventDetector.BgraToGray(bgra, W, H);

        foreach (var group in groups)
        {
            Assert.True(VisualEventDetector.TryGetCropRect(group, W, H,
                out var cropX, out var cropY, out var cropW, out var cropH));

            var perGroupCrop = VisualEventDetector.CropBgraToGray(bgra, W, cropX, cropY, cropW, cropH);
            var perGroup = VisualEventDetector.ResizeGray(perGroupCrop, cropW, cropH, ModelInput, ModelInput);
            var wholeFrame = VisualEventDetector.CropAndResizeGray(
                frameGray, W, H, cropX, cropY, cropW, cropH, ModelInput, ModelInput);

            Assert.Equal(
                perGroup.AsSpan(0, ModelInput * ModelInput).ToArray(),
                wholeFrame.AsSpan(0, ModelInput * ModelInput).ToArray());
        }
    }

    // The full-frame group is the one whose crop rectangle is the frame, so it is also the one
    // where a clamping slip would go unnoticed by the existing region tests.
    [Fact]
    public void Full_frame_group_crops_the_entire_frame()
    {
        var group = new RegionGroup { X = 0f, Y = 0f, W = 1f, H = 1f };

        Assert.True(VisualEventDetector.TryGetCropRect(group, W, H,
            out var cropX, out var cropY, out var cropW, out var cropH));

        Assert.Equal(0, cropX);
        Assert.Equal(0, cropY);
        Assert.Equal(W, cropW);
        Assert.Equal(H, cropH);
    }
}
