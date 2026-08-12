using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

// ParseYoloOutput reads the detect head as (4 + numClasses) contiguous rows of numDetections floats,
// with numDetections derived as output.Length / (4 + numClasses):
//
//     cx = output[i]                        w  = output[2 * numDetections + i]
//     cy = output[1 * numDetections + i]    h  = output[3 * numDetections + i]
//     conf(c) = output[(4 + c) * numDetections + i]
//
// Every one of those reads is in range for any class count, so a wrong one does not throw — it
// shifts the whole tensor and decodes each box to garbage in silence. Three things have to hold for
// the stride to be right, and none of them is checked anywhere else:
//
//   * numClasses comes from the exported graph (ModelClassCountTests pins that), and the anchor
//     count the length-based derivation recovers really is the graph's;
//   * the span ParseYoloOutput is handed is exactly the tensor, not a longer pooled buffer — its
//     length is the dividend of that derivation;
//   * the layout is channel-major with the four box rows first, which is what makes row 4 onwards
//     confidences rather than geometry.
//
// These run against the shipped model's real output, which is the only place that contract exists.
// What ParseYoloOutput does once the stride is right — the row offsets, the cutoff, argmax, the
// centre-to-corner conversion — is pinned on hand-built tensors in ParseYoloOutputDecodeTests.
// Loads its own session rather than ModelService's cached one: test classes run in parallel and
// ModelService.UnloadModel disposes the shared session out from under whoever else holds it.
public class YoloOutputStrideTests
{
    private const string GameId = "Overwatch";
    private const int ModelInput = 640;

    // Mirrors ParseYoloOutput's own arithmetic, so a change to the row layout has to be made here too.
    private const int YoloBoxChannels = 4;

    private sealed record ShippedOutput(Tensor<float> Tensor, float[] Values, int NumClasses, int Anchors);

    // Fail loudly rather than skip, matching InputTensorReuseTests: a guard that quietly disables
    // itself where the model is absent is worse than no guard at all.
    private static string ModelPath()
    {
        var modelPath = ModelService.GetModelPath(GameId);
        Assert.True(File.Exists(modelPath),
            $"ONNX model not found at {modelPath}. These tests pin the tensor layout ParseYoloOutput " +
            "walks and cannot be checked without the real model. They must fail, not skip.");
        return modelPath;
    }

    // The body runs while the run results are still alive: the output tensor is backed by memory the
    // results own, exactly as it is in RunInferenceOnGray, so reading it after disposal would be
    // testing freed native memory rather than the contract.
    private static void WithShippedModelOutput(Action<ShippedOutput> body)
    {
        using var session = new InferenceSession(ModelPath());

        // A real inference on a real frame-shaped input. Zeros would exercise the same layout, but a
        // near-black frame is exactly what DetectionLoop refuses to run, so noise keeps the output
        // representative of what ParseYoloOutput actually sees.
        var gray = new byte[ModelInput * ModelInput];
        new Random(7).NextBytes(gray);

        var buffer = new float[ModelInput * ModelInput * 3];
        VisualEventDetector.FillInputTensor(gray, buffer, ModelInput);

        var input = new DenseTensor<float>(buffer.AsMemory(), new[] { 1, 3, ModelInput, ModelInput });
        var container = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(session.InputNames[0], input)
        };

        var outputName = session.OutputMetadata.Keys.First();
        var dimensions = session.OutputMetadata[outputName].Dimensions;

        Assert.True(VisualEventDetector.TryDeriveClassCount(dimensions, out var numClasses),
            $"Output {outputName} has shape [{string.Join(',', dimensions)}] and carries no static " +
            "class dimension, so the stride under test is not defined.");

        using var runOptions = new RunOptions();
        using var results = session.Run(container, new[] { outputName }, runOptions);

        var tensor = results[0].AsTensor<float>();
        body(new ShippedOutput(tensor, tensor.ToArray(), numClasses, dimensions[2]));
    }

    // numDetections is recovered by division rather than read from the shape, so the shape and the
    // element count have to agree exactly — a length that is not a whole number of rows means every
    // row after the first is read at the wrong offset.
    [Fact]
    public void DerivedDetectionCount_EqualsTheGraphsAnchorDimension()
    {
        WithShippedModelOutput(output =>
        {
            var stride = YoloBoxChannels + output.NumClasses;

            Assert.Equal(0, output.Values.Length % stride);
            Assert.Equal(output.Anchors, output.Values.Length / stride);
            Assert.Equal(stride * output.Anchors, output.Values.Length);
        });
    }

    // The reason the class count may not be taken from events.json: an entry added or removed there
    // does not change the tensor, it changes the divisor, and every row boundary moves with it. This
    // pins that the damage is real for the shipped model rather than theoretical.
    [Fact]
    public void AnOffByOneClassCount_MovesEveryRowBoundary()
    {
        WithShippedModelOutput(output =>
        {
            var correct = output.Values.Length / (YoloBoxChannels + output.NumClasses);

            Assert.NotEqual(correct, output.Values.Length / (YoloBoxChannels + output.NumClasses - 1));
            Assert.NotEqual(correct, output.Values.Length / (YoloBoxChannels + output.NumClasses + 1));
        });
    }

    // RunInferenceOnGray hands ParseYoloOutput dense.Buffer.Span when the output is a DenseTensor, to
    // avoid copying the whole tensor per region per cycle. That shortcut is only equivalent to
    // ToArray() while the buffer is exactly the tensor: a longer one would inflate output.Length and
    // therefore numDetections, shifting every read.
    [Fact]
    public void OutputTensor_IsDenseAndItsBufferIsExactlyTheTensor()
    {
        WithShippedModelOutput(output =>
        {
            var dense = Assert.IsType<DenseTensor<float>>(output.Tensor);

            Assert.Equal(output.Values.Length, dense.Buffer.Length);
            Assert.Equal(output.Values, dense.Buffer.Span.ToArray());
        });
    }

    // The layout itself. Rows 4 and up are per-class confidences, which the detect head sigmoids, so
    // every value in them sits within [0, 1]; the four rows before them are box geometry in input
    // pixels, which is why ParseYoloOutput divides them by inputSize. Reading the class rows one row
    // early — what an over-counted numClasses does — pulls the height row in and breaks that bound,
    // so the check discriminates rather than holding whatever the offset.
    [Fact]
    public void ClassRowsAreConfidences_AndBoxRowsAreInputPixels_AtTheDerivedStride()
    {
        WithShippedModelOutput(output =>
        {
            var anchors = output.Values.Length / (YoloBoxChannels + output.NumClasses);

            foreach (var confidence in output.Values.AsSpan(YoloBoxChannels * anchors))
            {
                Assert.InRange(confidence, 0f, 1f);
            }

            Assert.True(Max(output.Values.AsSpan(0, YoloBoxChannels * anchors)) > 1f,
                "Box rows are already normalized — ParseYoloOutput divides them by the model input " +
                "size and would shrink every box to nothing.");

            // One row early: the box row pulled in carries pixel-space values, so the bound above
            // cannot hold at this offset.
            Assert.True(Max(output.Values.AsSpan((YoloBoxChannels - 1) * anchors)) > 1f,
                "Reading the class rows at the wrong offset stayed within [0,1], so the bound above " +
                "does not actually pin the row boundary.");
        });
    }

    private static float Max(ReadOnlySpan<float> values)
    {
        var max = float.NegativeInfinity;
        foreach (var value in values)
        {
            if (value > max) max = value;
        }
        return max;
    }
}
