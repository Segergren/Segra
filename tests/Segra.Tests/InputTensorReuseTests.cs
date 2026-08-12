using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

// VisualEventDetector.Start allocates one float[640*640*3], wraps it in a DenseTensor that
// aliases that memory, and hands the tensor to a single NamedOnnxValue reused for every
// inference. Each cycle overwrites the buffer in place; nothing rebuilds the tensor or the
// container. That reuse keeps allocations off the LOH, and is only correct because ORT reads
// the aliased memory at Run time rather than snapshotting it when the NamedOnnxValue is created.
//
// If that contract ever changes — most plausibly via an ORT package upgrade — every inference
// after the first would score a stale frame. The failure is silent. This test exists to make
// that loud. It loads the real ONNX model; the contract cannot be verified without the runtime.
// Shares ModelService's reference-counted session cache with ModelSessionLifetimeTests, whose
// assertions are about that cache's counts — so the two must not run at the same time.
[Collection(ModelSessionCollection.Name)]
public class InputTensorReuseTests
{
    private const string GameId = "Overwatch";
    private const int ModelInput = 640;

    private static byte[] SyntheticGray(int seed)
    {
        var buf = new byte[ModelInput * ModelInput];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    private static bool BitwiseEqual(float[] a, float[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i]))
                return false;
        }
        return true;
    }

    // Verifies that a NamedOnnxValue reused across Run calls feeds the model whatever the
    // buffer holds at Run time. Start subscribes to OBS capture (not available under test),
    // so this pins the runtime contract separately.
    [Fact]
    public void ReusedInputTensor_ReflectsBufferMutationsBetweenRuns()
    {
        // Fail loudly rather than skip. A regression guard that quietly disables itself where
        // the model is absent is worse than no guard at all, so there is no Assert.Skip and no
        // conditional fact here: a missing model is a red test.
        var modelPath = ModelService.GetModelPath(GameId);
        Assert.True(File.Exists(modelPath),
            $"ONNX model not found at {modelPath}. This test guards a runtime contract and " +
            "cannot be verified without the real model. It must fail, not skip.");

        var session = ModelService.LoadModel(GameId);
        try
        {
            // Constructed exactly as VisualEventDetector.Start constructs it.
            var buffer = new float[ModelInput * ModelInput * 3];
            var tensor = new DenseTensor<float>(
                buffer.AsMemory(), new[] { 1, 3, ModelInput, ModelInput });
            var container = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(session.InputNames[0], tensor)
            };
            var outputNames = session.OutputMetadata.Keys.ToList();
            using var runOptions = new RunOptions();

            float[] RunWithSeed(int seed)
            {
                VisualEventDetector.FillInputTensor(SyntheticGray(seed), buffer, ModelInput);
                using var results = session.Run(container, outputNames, runOptions);
                return results[0].AsTensor<float>().ToArray();
            }

            var a1 = RunWithSeed(1);
            var b = RunWithSeed(200);
            var a2 = RunWithSeed(1);

            // Determinism: identical buffer contents must reproduce identical output. On its
            // own this would also hold for an implementation returning garbage, which is why
            // it is paired with the inequality below.
            Assert.True(BitwiseEqual(a1, a2),
                "Reusing the container was not deterministic: the same input produced different " +
                "output on the first and third run.");

            // The assertion that actually catches staleness: overwriting the buffer between
            // runs must change the output. If this fails, the model is being fed a snapshot
            // taken when the NamedOnnxValue was created and detections are frozen.
            Assert.False(BitwiseEqual(a1, b),
                "Two different inputs produced bit-identical output: the reused NamedOnnxValue " +
                "is not picking up mutations to the underlying buffer, so inference is running " +
                "on a stale frame.");
        }
        finally
        {
            ModelService.UnloadModel(GameId);
        }
    }
}
