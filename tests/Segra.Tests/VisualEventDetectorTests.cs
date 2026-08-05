using System;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

public class VisualEventDetectorTests
{
    [Fact]
    public void BgraToGray_WeightsChannelsInBgraOrder()
    {
        const int w = 2;
        const int h = 2;

        // Each pixel has distinct B/G/R so that swapping any two channel reads changes
        // the result. Bytes are laid out B, G, R, A.
        byte[] bgra =
        [
            10, 20, 200, 255,
            200, 20, 10, 255,
            0, 255, 0, 255,
            255, 0, 0, 255,
        ];

        // Hand-computed, truncated by the (byte) cast rather than rounded:
        //   (10, 20, 200): 0.299*200 + 0.587*20 + 0.114*10 = 59.80 + 11.74 + 1.14 = 72.68 -> 72
        //   (200, 20, 10): 0.299*10  + 0.587*20 + 0.114*200 =  2.99 + 11.74 + 22.80 = 37.53 -> 37
        //   (0, 255, 0)  : 0.587*255 = 149.685 -> 149
        //   (255, 0, 0)  : 0.114*255 =  29.070 ->  29
        byte[] expected = [72, 37, 149, 29];

        var gray = VisualEventDetector.BgraToGray(bgra, w, h);

        // ArrayPool-rented: the buffer may be longer than w*h, so slice before asserting.
        Assert.True(gray.Length >= w * h);
        Assert.Equal(expected, gray.AsSpan(0, w * h).ToArray());
    }
}
