using System;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

// Golden-output tests: the model's input must stay byte-for-byte identical across
// preprocessing optimisations. Every assertion here compares production against the
// frozen algorithms in ReferenceImplementations.
public class PreprocessingGoldenTests
{
    private const int W = 1920;
    private const int H = 1080;
    private const int ModelInput = 640;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public void BgraToGray_matches_reference(int seed)
    {
        var bgra = ReferenceImplementations.SyntheticBgra(W, H, seed);

        var expected = ReferenceImplementations.BgraToGray(bgra, W, H);
        var actual = VisualEventDetector.BgraToGray(bgra, W, H);

        // Production rents from ArrayPool, so the buffer may be longer than w*h.
        Assert.True(actual.Length >= W * H);
        Assert.Equal(expected, actual.AsSpan(0, W * H).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FullPipeline_matches_reference_for_each_real_region(int groupIndex)
    {
        var group = ReferenceImplementations.OverwatchGroups[groupIndex];
        var bgra = ReferenceImplementations.SyntheticBgra(W, H, 7 + groupIndex);

        var cropW = (int)(group.W * W);
        var cropH = (int)(group.H * H);
        var cropX = (int)(group.X * W);
        var cropY = (int)(group.Y * H);

        Assert.True(cropW > 0 && cropH > 0);
        if (cropX + cropW > W) cropW = W - cropX;
        if (cropY + cropH > H) cropH = H - cropY;
        Assert.True(cropW > 0 && cropH > 0);

        var expectedGray = ReferenceImplementations.BgraToGray(bgra, W, H);
        var expected = ReferenceImplementations.CropAndResizeGray(
            expectedGray, W, H, cropX, cropY, cropW, cropH, ModelInput, ModelInput);

        var actualGray = VisualEventDetector.BgraToGray(bgra, W, H);
        var actual = VisualEventDetector.CropAndResizeGray(
            actualGray, W, H, cropX, cropY, cropW, cropH, ModelInput, ModelInput);

        Assert.True(actual.Length >= ModelInput * ModelInput);
        Assert.Equal(
            expected.AsSpan(0, ModelInput * ModelInput).ToArray(),
            actual.AsSpan(0, ModelInput * ModelInput).ToArray());
    }

    // Cropping before the greyscale conversion must not change a single byte reaching the
    // model. Safe because the bilinear resize never samples outside the crop rectangle.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CropThenGray_equals_GrayThenCrop(int groupIndex)
    {
        var group = ReferenceImplementations.OverwatchGroups[groupIndex];
        var bgra = ReferenceImplementations.SyntheticBgra(W, H, 11);

        var cropW = (int)(group.W * W);
        var cropH = (int)(group.H * H);
        var cropX = (int)(group.X * W);
        var cropY = (int)(group.Y * H);

        Assert.True(cropW > 0 && cropH > 0);
        if (cropX + cropW > W) cropW = W - cropX;
        if (cropY + cropH > H) cropH = H - cropY;
        Assert.True(cropW > 0 && cropH > 0);

        var fullGray = ReferenceImplementations.BgraToGray(bgra, W, H);
        var expected = ReferenceImplementations.CropAndResizeGray(
            fullGray, W, H, cropX, cropY, cropW, cropH, ModelInput, ModelInput);

        var crop = VisualEventDetector.CropBgraToGray(bgra, W, cropX, cropY, cropW, cropH);
        var actual = VisualEventDetector.ResizeGray(crop, cropW, cropH, ModelInput, ModelInput);

        Assert.True(actual.Length >= ModelInput * ModelInput);
        Assert.Equal(
            expected.AsSpan(0, ModelInput * ModelInput).ToArray(),
            actual.AsSpan(0, ModelInput * ModelInput).ToArray());
    }

    [Fact]
    public void InputTensor_matches_reference()
    {
        var pixels = ModelInput * ModelInput;
        var gray = new byte[pixels];
        new Random(4242).NextBytes(gray);

        var expected = ReferenceImplementations.BuildInputTensor(gray, ModelInput);

        var actual = new float[pixels * 3];
        VisualEventDetector.FillInputTensor(gray, actual, ModelInput);

        // Identical arithmetic on identical inputs, so exact float equality is the contract.
        Assert.Equal(expected, actual);
    }
}
