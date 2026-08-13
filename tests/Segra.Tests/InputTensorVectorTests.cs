using System;
using System.Collections.Generic;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

// FillInputTensor divides four pixels at a time through Vector128 and falls back to a scalar
// loop for the tail and for hardware without vector acceleration. The optimisation is only
// legitimate if it is bit-identical to the scalar loop, so every test here compares raw float
// bits rather than values.
//
// The vector path MUST divide by Vector128.Create(255f). Multiplying by 1f/255f is the obvious
// rewrite and is wrong: the rounded reciprocal differs in the last ulp for many byte values.
public class InputTensorVectorTests
{
    private static void AssertBitIdentical(float[] expected, float[] actual, string context)
    {
        Assert.Equal(expected.Length, actual.Length);
        var diverged = new List<string>();
        for (int i = 0; i < expected.Length; i++)
        {
            if (BitConverter.SingleToInt32Bits(expected[i]) != BitConverter.SingleToInt32Bits(actual[i]))
            {
                diverged.Add($"index {i}: expected {expected[i]:R} " +
                             $"(0x{BitConverter.SingleToInt32Bits(expected[i]):X8}) but got {actual[i]:R} " +
                             $"(0x{BitConverter.SingleToInt32Bits(actual[i]):X8})");
                if (diverged.Count == 10) break;
            }
        }
        Assert.True(diverged.Count == 0,
            $"{context}: output is not bit-identical to the reference.\n" + string.Join("\n", diverged));
    }

    // 16x16 = 256 pixels holding each byte value exactly once, so one call covers the whole
    // input domain. Anything the vector path rounds differently shows up here.
    private static byte[] EveryByteValueOnce()
    {
        var gray = new byte[256];
        for (int b = 0; b < 256; b++) gray[b] = (byte)b;
        return gray;
    }

    [Fact]
    public void EveryByteValue_VectorPath_IsBitIdenticalToReference()
    {
        var gray = EveryByteValueOnce();
        var expected = ReferenceImplementations.BuildInputTensor(gray, 16);

        var actual = new float[256 * 3];
        VisualEventDetector.FillInputTensor(gray, actual, 16, useVectorPath: true);

        AssertBitIdentical(expected, actual, "vector path, all 256 byte values");
    }

    [Fact]
    public void EveryByteValue_ScalarFallback_IsBitIdenticalToReference()
    {
        var gray = EveryByteValueOnce();
        var expected = ReferenceImplementations.BuildInputTensor(gray, 16);

        var actual = new float[256 * 3];
        VisualEventDetector.FillInputTensor(gray, actual, 16, useVectorPath: false);

        AssertBitIdentical(expected, actual, "scalar fallback, all 256 byte values");
    }

    // The default entry point must agree with both explicit paths, otherwise the flag overload
    // is testing something production never runs.
    [Fact]
    public void DefaultEntryPoint_AgreesWithBothExplicitPaths()
    {
        var gray = EveryByteValueOnce();

        var byDefault = new float[256 * 3];
        var vector = new float[256 * 3];
        var scalar = new float[256 * 3];
        VisualEventDetector.FillInputTensor(gray, byDefault, 16);
        VisualEventDetector.FillInputTensor(gray, vector, 16, useVectorPath: true);
        VisualEventDetector.FillInputTensor(gray, scalar, 16, useVectorPath: false);

        AssertBitIdentical(scalar, byDefault, "default entry point vs scalar");
        AssertBitIdentical(scalar, vector, "vector path vs scalar");
    }

    // pixels is a perfect square, so pixels % 4 is 0 for even sizes and 1 for odd ones: an odd
    // inputSize is the only way to leave work for the tail. inputSize 1 is the degenerate case
    // where the vector loop never runs at all and the tail does everything.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(31)]
    [InlineData(63)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(64)]
    public void Tail_And_WholeVector_Sizes_AreBitIdenticalToReference(int inputSize)
    {
        var pixels = inputSize * inputSize;
        var gray = new byte[pixels];
        new Random(inputSize * 7919).NextBytes(gray);

        var expected = ReferenceImplementations.BuildInputTensor(gray, inputSize);

        var vector = new float[pixels * 3];
        var scalar = new float[pixels * 3];
        VisualEventDetector.FillInputTensor(gray, vector, inputSize, useVectorPath: true);
        VisualEventDetector.FillInputTensor(gray, scalar, inputSize, useVectorPath: false);

        AssertBitIdentical(expected, vector, $"vector path at inputSize {inputSize} (tail {pixels % 4})");
        AssertBitIdentical(expected, scalar, $"scalar fallback at inputSize {inputSize}");
    }

    // A tail of exactly one element, with every byte value taking a turn in that slot: the
    // boundary between the vector loop and the scalar remainder must not round differently.
    [Fact]
    public void OneElementTail_CoversEveryByteValue()
    {
        const int InputSize = 3;
        const int Pixels = InputSize * InputSize;
        Assert.Equal(1, Pixels % 4);

        for (int b = 0; b < 256; b++)
        {
            var gray = new byte[Pixels];
            new Random(b).NextBytes(gray);
            gray[Pixels - 1] = (byte)b;

            var expected = ReferenceImplementations.BuildInputTensor(gray, InputSize);
            var actual = new float[Pixels * 3];
            VisualEventDetector.FillInputTensor(gray, actual, InputSize, useVectorPath: true);

            AssertBitIdentical(expected, actual, $"tail element holding byte {b}");
        }
    }

    // The vector path writes through unchecked stores, so it has to reject undersized buffers
    // itself rather than relying on array bounds checks.
    [Fact]
    public void UndersizedBuffers_Throw()
    {
        Assert.Throws<ArgumentException>(() =>
            VisualEventDetector.FillInputTensor(new byte[15], new float[16 * 3], 4));
        Assert.Throws<ArgumentException>(() =>
            VisualEventDetector.FillInputTensor(new byte[16], new float[16 * 3 - 1], 4));
    }

    // Production buffers come from ArrayPool and are routinely longer than the frame needs.
    [Fact]
    public void OversizedSourceBuffer_IsAccepted()
    {
        const int InputSize = 8;
        const int Pixels = InputSize * InputSize;
        var gray = new byte[Pixels + 500];
        new Random(1234).NextBytes(gray);

        var expected = ReferenceImplementations.BuildInputTensor(gray, InputSize);
        var actual = new float[Pixels * 3];
        VisualEventDetector.FillInputTensor(gray, actual, InputSize, useVectorPath: true);

        AssertBitIdentical(expected, actual, "oversized source buffer");
    }
}
