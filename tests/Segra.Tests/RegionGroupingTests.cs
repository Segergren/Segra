using System.Collections.Generic;
using System.Linq;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

public class RegionGroupingTests
{
    private const int W = 1920;
    private const int H = 1080;

    private static EventDefinition Region(int classId, float x, float y, float w, float h) =>
        new()
        {
            ClassId = classId,
            ScreenRegionX = x,
            ScreenRegionY = y,
            ScreenRegionW = w,
            ScreenRegionH = h,
        };

    private static List<EventDefinition> OverwatchDefinitions() =>
    [
        Region(0, 0.2583f, 0.5101f, 0.5375f, 0.3161f),
        Region(1, 0.1028f, 0.0336f, 0.1236f, 0.0469f),
        Region(2, 0.2583f, 0.5101f, 0.5375f, 0.3161f),
        Region(3, 0.4681f, 0.8706f, 0.0708f, 0.0543f),
        Region(4, 0.2583f, 0.5101f, 0.5375f, 0.3161f),
        Region(5, 0.0097f, 0.0114f, 0.1514f, 0.0568f),
        Region(6, 0.4500f, 0.6000f, 0.2000f, 0.1500f),
    ];

    private static List<(int X, int Y, int W, int H)> CropRects(List<RegionGroup> groups) =>
        groups
            .Select(g => ((int)(g.X * W), (int)(g.Y * H), (int)(g.W * W), (int)(g.H * H)))
            .OrderBy(r => r.Item1).ThenBy(r => r.Item2)
            .ToList();

    [Fact]
    public void Transitive_overlaps_merge_regardless_of_order()
    {
        // A overlaps B, B overlaps C, but A does not overlap C. A first-match-wins pass
        // resolves this differently depending on which is seen first.
        var a = Region(0, 0.00f, 0.0f, 0.20f, 0.1f);
        var b = Region(1, 0.15f, 0.0f, 0.20f, 0.1f);
        var c = Region(2, 0.30f, 0.0f, 0.20f, 0.1f);

        Assert.Single(VisualEventDetector.BuildRegionGroups([a, b, c]));
        Assert.Single(VisualEventDetector.BuildRegionGroups([c, b, a]));
        Assert.Single(VisualEventDetector.BuildRegionGroups([a, c, b]));
        Assert.Single(VisualEventDetector.BuildRegionGroups([b, a, c]));
    }

    [Fact]
    public void Disjoint_regions_stay_separate()
    {
        var a = Region(0, 0.0f, 0.0f, 0.1f, 0.1f);
        var b = Region(1, 0.8f, 0.8f, 0.1f, 0.1f);

        Assert.Equal(2, VisualEventDetector.BuildRegionGroups([a, b]).Count);
    }

    [Fact]
    public void Real_overwatch_events_still_produce_three_groups()
    {
        Assert.Equal(3, VisualEventDetector.BuildRegionGroups(OverwatchDefinitions()).Count);
    }

    [Fact]
    public void Real_overwatch_grouping_is_order_independent()
    {
        var forward = CropRects(VisualEventDetector.BuildRegionGroups(OverwatchDefinitions()));

        var reversed = OverwatchDefinitions();
        reversed.Reverse();

        Assert.Equal(forward, CropRects(VisualEventDetector.BuildRegionGroups(reversed)));
    }

    [Fact]
    public void Overwatch_groups_match_the_golden_test_fixture()
    {
        // ReferenceImplementations.OverwatchGroups is a hand-written literal that the
        // preprocessing golden tests crop against. Nothing else ties it to the real
        // algorithm, so a change to merging could silently leave those tests exercising
        // rectangles production no longer uses. Compared as integer crop rects because
        // group 1's width computes as 0.21669999f against the literal 0.2167f — a
        // 1.5e-08 artifact of 0.1028f + 0.1236f - 0.0097f that truncates away.
        var fromAlgorithm = CropRects(VisualEventDetector.BuildRegionGroups(OverwatchDefinitions()));

        var fromFixture = ReferenceImplementations.OverwatchGroups
            .Select(g => ((int)(g.X * W), (int)(g.Y * H), (int)(g.W * W), (int)(g.H * H)))
            .OrderBy(r => r.Item1).ThenBy(r => r.Item2)
            .ToList();

        Assert.Equal(fromFixture, fromAlgorithm);
    }
}
