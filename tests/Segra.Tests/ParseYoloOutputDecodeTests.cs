using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

// YoloOutputStrideTests pins the shape of the tensor ParseYoloOutput is handed. This file pins what
// it does with it, on hand-built tensors whose every value is known.
//
// The decode is five independent decisions, and every one of them fails silently when it is wrong:
//
//   * the channel-major row offsets — cx = output[i], cy = output[n + i], w = output[2n + i],
//     h = output[3n + i], conf(c) = output[(4 + c) * n + i];
//   * the 0.7f confidence cutoff;
//   * argmax over the class rows;
//   * the centre-to-corner conversion, X = cx / inputSize - w / (2 * inputSize);
//   * dividing box geometry by the model input size at all.
//
// Every read stays in range whichever offset is used, so a mutation to any of them — (3 + c) * n for
// the class row, a swapped cx/cy, a dropped /2 — throws nothing and logs nothing. It returns boxes
// in the wrong place, with the wrong class, for as long as nobody looks at a recording.
//
// ParseYoloOutput is private and takes a ReadOnlySpan<float>, so it is bound through
// MethodInfo.CreateDelegate rather than Invoke: byref-like parameters cannot be boxed into the
// object[] Invoke takes, but a delegate binds the signature directly.
public class ParseYoloOutputDecodeTests
{
    private delegate List<DetectionResult> ParseDelegate(
        ReadOnlySpan<float> output, int inputSize, int numClasses);

    // Bound lazily rather than in a field initializer: an Assert thrown while initializing a static
    // field surfaces as TypeInitializationException on every test in the class, which buries the
    // message. Lazy<T> rethrows the original exception from whichever test touched it first.
    private static readonly Lazy<ParseDelegate> LazyParse = new(BindParseYoloOutput);

    private static ParseDelegate Parse => LazyParse.Value;

    private static ParseDelegate BindParseYoloOutput()
    {
        var method = typeof(VisualEventDetector).GetMethod(
            "ParseYoloOutput", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.True(method != null,
            "VisualEventDetector.ParseYoloOutput is gone or no longer a private static method. " +
            "The decode contract below is not being checked against anything.");

        return (ParseDelegate)method!.CreateDelegate(typeof(ParseDelegate));
    }

    // Values are given per row, in the order the tensor stores them, so this builder describes the
    // channel-major layout by concatenation rather than by repeating ParseYoloOutput's own index
    // arithmetic — which would make any offset mutation agree with itself.
    private sealed record Anchor(float Cx, float Cy, float W, float H, float[] Confidences);

    private static float[] Tensor(params Anchor[] anchors)
    {
        var rows = new List<IEnumerable<float>>
        {
            anchors.Select(a => a.Cx),
            anchors.Select(a => a.Cy),
            anchors.Select(a => a.W),
            anchors.Select(a => a.H),
        };

        var numClasses = anchors[0].Confidences.Length;
        for (int c = 0; c < numClasses; c++)
        {
            var classIndex = c;
            rows.Add(anchors.Select(a => a.Confidences[classIndex]));
        }

        return rows.SelectMany(row => row).ToArray();
    }

    // The whole decode on one tensor: three classes, five anchors, every box value distinct so a
    // swapped or shifted row cannot land on the same number by accident. Two anchors are below the
    // cutoff and must not appear at all, which also pins that surviving anchors keep their own
    // geometry rather than the geometry of their position in the result list.
    [Fact]
    public void DecodesAnchorsAtTheChannelMajorRowOffsets()
    {
        var output = Tensor(
            new Anchor(50f, 40f, 20f, 10f, [0.10f, 0.90f, 0.20f]),
            new Anchor(10f, 90f, 4f, 6f, [0.69f, 0.10f, 0.20f]),
            new Anchor(30f, 70f, 8f, 12f, [0.10f, 0.20f, 0.71f]),
            new Anchor(80f, 20f, 40f, 4f, [0.72f, 0.30f, 0.71f]),
            new Anchor(0f, 0f, 0f, 0f, [0f, 0f, 0f]));

        var results = Parse(output, 100, 3);

        Assert.Equal(3, results.Count);

        Assert.Equal(1, results[0].ClassId);
        Assert.Equal(0.9, results[0].Confidence, 4);
        Assert.Equal(0.4, results[0].X, 4);
        Assert.Equal(0.35, results[0].Y, 4);
        Assert.Equal(0.2, results[0].Width, 4);
        Assert.Equal(0.1, results[0].Height, 4);

        Assert.Equal(2, results[1].ClassId);
        Assert.Equal(0.71, results[1].Confidence, 4);
        Assert.Equal(0.26, results[1].X, 4);
        Assert.Equal(0.64, results[1].Y, 4);
        Assert.Equal(0.08, results[1].Width, 4);
        Assert.Equal(0.12, results[1].Height, 4);

        Assert.Equal(0, results[2].ClassId);
        Assert.Equal(0.72, results[2].Confidence, 4);
        Assert.Equal(0.6, results[2].X, 4);
        Assert.Equal(0.18, results[2].Y, 4);
        Assert.Equal(0.4, results[2].Width, 4);
        Assert.Equal(0.04, results[2].Height, 4);
    }

    // The offset that matters most, written out as a literal so it shares no arithmetic at all with
    // the code under test. One class, two anchors, laid out row by row. The height row is 800 —
    // reading the class row one early, which is what (3 + c) * numDetections does, would report a
    // confidence of 800 for anchor 0 and 900 for anchor 1 instead of dropping anchor 0 outright.
    [Fact]
    public void ReadsConfidencesFromRowFour_NotFromTheHeightRow()
    {
        float[] output =
        [
            100f, 200f,   // cx
            300f, 400f,   // cy
            500f, 600f,   // w
            800f, 900f,   // h
            0.50f, 0.95f, // class 0 confidence
        ];

        var results = Parse(output, 1000, 1);

        var only = Assert.Single(results);
        Assert.Equal(0, only.ClassId);
        Assert.Equal(0.95, only.Confidence, 4);

        // Anchor 1: cx 200, cy 400, w 600, h 900 over an input size of 1000.
        Assert.Equal(0.2 - 0.3, only.X, 4);
        Assert.Equal(0.4 - 0.45, only.Y, 4);
        Assert.Equal(0.6, only.Width, 4);
        Assert.Equal(0.9, only.Height, 4);
    }

    // The cutoff is a strict "below 0.7 is dropped", so 0.7 exactly survives. All three anchors are
    // identical apart from their confidence, which means the count is the only thing that can move.
    [Fact]
    public void DropsAnchorsBelowThePointSevenCutoff_AndKeepsThoseAtOrAbove()
    {
        Assert.Empty(Parse(Tensor(new Anchor(50f, 50f, 10f, 10f, [0.69f])), 100, 1));
        Assert.Single(Parse(Tensor(new Anchor(50f, 50f, 10f, 10f, [0.70f])), 100, 1));
        Assert.Single(Parse(Tensor(new Anchor(50f, 50f, 10f, 10f, [0.71f])), 100, 1));
    }

    // Confidence is not the first class over the cutoff, nor the last one, but the highest — and the
    // reported Confidence is that maximum rather than the class-0 value or the running total. Each
    // anchor puts its winner in a different column so a hardcoded index cannot pass.
    [Fact]
    public void SelectsTheHighestScoringClassAcrossAllClassRows()
    {
        var output = Tensor(
            new Anchor(10f, 10f, 2f, 2f, [0.95f, 0.80f, 0.75f, 0.71f]),
            new Anchor(20f, 20f, 2f, 2f, [0.75f, 0.71f, 0.96f, 0.80f]),
            new Anchor(30f, 30f, 2f, 2f, [0.71f, 0.75f, 0.80f, 0.97f]));

        var results = Parse(output, 100, 4);

        Assert.Equal(new[] { 0, 2, 3 }, results.Select(r => r.ClassId).ToArray());
        Assert.Equal(0.95, results[0].Confidence, 4);
        Assert.Equal(0.96, results[1].Confidence, 4);
        Assert.Equal(0.97, results[2].Confidence, 4);
    }

    // A tie goes to the lower class id: the scan keeps the first strict maximum. Nothing downstream
    // depends on which one wins, but an unstable answer here would make a detection flicker between
    // two event definitions frame to frame, so the behaviour is pinned rather than left open.
    [Fact]
    public void OnATie_KeepsTheLowestClassId()
    {
        var results = Parse(Tensor(new Anchor(50f, 50f, 10f, 10f, [0.4f, 0.88f, 0.88f])), 100, 3);

        Assert.Equal(1, Assert.Single(results).ClassId);
    }

    // Boxes come out of the detect head as a centre plus a size in input pixels, and everything
    // downstream — MapDetectionsToFullFrame, CooldownTracker's overlap match — reads them as
    // normalized top-left corners. Half the width is subtracted, not the whole width and not none of
    // it, and the same input size divides both terms. A box centred at the origin proves the sign:
    // the corner has to go negative rather than clamp.
    [Fact]
    public void ConvertsCentreBoxesToNormalizedTopLeftCorners()
    {
        var results = Parse(Tensor(
            new Anchor(320f, 160f, 64f, 32f, [0.9f]),
            new Anchor(0f, 0f, 100f, 200f, [0.9f])), 640, 1);

        // 320/640 - 64/1280 = 0.5 - 0.05.
        Assert.Equal(0.45, results[0].X, 4);
        Assert.Equal(0.225, results[0].Y, 4);
        Assert.Equal(0.1, results[0].Width, 4);
        Assert.Equal(0.05, results[0].Height, 4);

        Assert.Equal(-0.078125, results[1].X, 6);
        Assert.Equal(-0.15625, results[1].Y, 6);
    }

    // numDetections is derived as output.Length / (4 + numClasses) rather than passed in, so the two
    // arguments are not independent: the same buffer decoded with a different class count is read at
    // a different stride, which slides every row against the data. This is the failure
    // ModelClassCountTests exists to prevent, shown end to end — an events.json entry added without
    // retraining does not throw, it silently relocates everything.
    //
    // The anchor count is chosen so the stride genuinely moves: 6 anchors x (4 + 3 classes) is 42
    // floats, so the correct decode walks 42/7 = 6 anchors while a 2-class decode walks 42/6 = 7.
    // Most sizes do not do this — 3 anchors x 3 classes is 21 floats and 21/7 == 21/6 == 3, where
    // the rows stay put and the only visible effect is anchors dropping off the end.
    [Fact]
    public void AWrongClassCount_SilentlyDecodesTheSameBufferToRelocatedBoxes()
    {
        // cx 10..15, cy 20..25, every w and h 2, class 0 confident on every anchor.
        var output = Tensor(
            new Anchor(10f, 20f, 2f, 2f, [0.9f, 0f, 0f]),
            new Anchor(11f, 21f, 2f, 2f, [0.9f, 0f, 0f]),
            new Anchor(12f, 22f, 2f, 2f, [0.9f, 0f, 0f]),
            new Anchor(13f, 23f, 2f, 2f, [0.9f, 0f, 0f]),
            new Anchor(14f, 24f, 2f, 2f, [0.9f, 0f, 0f]),
            new Anchor(15f, 25f, 2f, 2f, [0.9f, 0f, 0f]));

        Assert.Equal(42, output.Length);

        var correct = Parse(output, 100, 3);
        var wrong = Parse(output, 100, 2);

        // Stride 7: every anchor decodes where it was written. X = 10/100 - 2/200, Y = 20/100 - 2/200.
        Assert.Equal(6, correct.Count);
        Assert.Equal(0.09, correct[0].X, 4);
        Assert.Equal(0.19, correct[0].Y, 4);

        // Stride 6: the cy row now starts at index 7 instead of 6, so anchor 0 reads the *second* cy
        // (21, not 20) while its cx still reads 10. Same box, moved down by exactly one element's
        // worth — and only two anchors still land on a confidence above the cutoff at all.
        Assert.Equal(2, wrong.Count);
        Assert.Equal(0.09, wrong[0].X, 4);
        Assert.Equal(0.20, wrong[0].Y, 4);
        Assert.Equal(0.10, wrong[1].X, 4);
        Assert.Equal(0.21, wrong[1].Y, 4);
    }
}
