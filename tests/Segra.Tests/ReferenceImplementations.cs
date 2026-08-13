using System;

namespace Segra.Tests;

// Frozen copies of the preprocessing algorithms as they stood before any optimisation
// work, kept so optimised versions can be diffed against them byte-for-byte.
// NEVER optimise this file. The only deviation from the originals is that ArrayPool
// rentals are plain allocations, so the reference carries no pooling semantics.
public static class ReferenceImplementations
{
    public static byte[] BgraToGray(byte[] bgra, int w, int h)
    {
        var pixels = w * h;
        var gray = new byte[pixels];
        for (int i = 0; i < pixels; i++)
        {
            var srcIdx = i * 4;
            var b = bgra[srcIdx];
            var g = bgra[srcIdx + 1];
            var r = bgra[srcIdx + 2];
            gray[i] = (byte)(0.299f * r + 0.587f * g + 0.114f * b);
        }
        return gray;
    }

    public static byte[] CropAndResizeGray(byte[] srcGray, int srcW, int srcH,
        int cropX, int cropY, int cropW, int cropH, int dstW, int dstH)
    {
        var crop = new byte[cropW * cropH];
        for (int y = 0; y < cropH; y++)
        {
            Array.Copy(srcGray, (cropY + y) * srcW + cropX, crop, y * cropW, cropW);
        }

        var dst = new byte[dstW * dstH];
        for (int dy = 0; dy < dstH; dy++)
        {
            float sy = (dy + 0.5f) * cropH / dstH - 0.5f;
            if (sy < 0) sy = 0;
            if (sy >= cropH - 1) sy = cropH - 1.001f;
            int sy0 = (int)sy, sy1 = sy0 + 1;
            float fy = sy - sy0;

            for (int dx = 0; dx < dstW; dx++)
            {
                float sx = (dx + 0.5f) * cropW / dstW - 0.5f;
                if (sx < 0) sx = 0;
                if (sx >= cropW - 1) sx = cropW - 1.001f;
                int sx0 = (int)sx, sx1 = sx0 + 1;
                float fx = sx - sx0;

                var v = (1 - fx) * (1 - fy) * crop[sy0 * cropW + sx0]
                      + fx * (1 - fy) * crop[sy0 * cropW + sx1]
                      + (1 - fx) * fy * crop[sy1 * cropW + sx0]
                      + fx * fy * crop[sy1 * cropW + sx1];
                dst[dy * dstW + dx] = (byte)v;
            }
        }
        return dst;
    }

    public static float[] BuildInputTensor(byte[] gray, int size)
    {
        var pixels = size * size;
        var t = new float[pixels * 3];
        for (int i = 0; i < pixels; i++)
        {
            var val = gray[i] / 255f;
            t[i] = val;
            t[i + pixels] = val;
            t[i + 2 * pixels] = val;
        }
        return t;
    }

    public static byte[] SyntheticBgra(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var buf = new byte[w * h * 4];
        rng.NextBytes(buf);
        return buf;
    }

    // The three region groups BuildRegionGroups produces from the 7 event definitions in
    // data/training/Overwatch/events.json. Group 1's W is the literal for the merged
    // Death Spectating + POTG regions, which BuildRegionGroups computes as 0.21669999f;
    // both round to the same integer crop width at every realistic frame size.
    public static readonly (float X, float Y, float W, float H)[] OverwatchGroups =
    {
        (0.2583f, 0.5101f, 0.5375f, 0.3161f),
        (0.0097f, 0.0114f, 0.2167f, 0.0691f),
        (0.4681f, 0.8706f, 0.0708f, 0.0543f),
    };
}
