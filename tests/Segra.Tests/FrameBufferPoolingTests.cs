using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Channels;
using Segra.Backend.Detection;
using Xunit;

namespace Segra.Tests;

// FrameData is a private nested type, so these tests reach it by reflection rather than
// widening its accessibility for the sake of testing.
public class FrameBufferPoolingTests
{
    private static readonly Type FrameDataType =
        typeof(VisualEventDetector).GetNestedType("FrameData", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("VisualEventDetector.FrameData not found");

    private static readonly FieldInfo BufferField =
        FrameDataType.GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("FrameData._buffer not found");

    private static readonly MethodInfo ReturnBufferMethod =
        FrameDataType.GetMethod("ReturnBuffer", BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException("FrameData.ReturnBuffer not found");

    private static object NewFrameData(byte[] buffer)
    {
        var frameData = Activator.CreateInstance(FrameDataType, nonPublic: true)!;
        BufferField.SetValue(frameData, buffer);
        return frameData;
    }

    private static byte[] BufferOf(object frameData) => (byte[])BufferField.GetValue(frameData)!;

    private static void ReturnBuffer(object frameData)
    {
        try
        {
            ReturnBufferMethod.Invoke(frameData, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    // The fix depends on the .NET 7+ Channel.CreateBounded(options, itemDropped) overload
    // firing for every item DropOldest evicts. Pin that contract before relying on it.
    [Fact]
    public void BoundedChannel_DropOldest_InvokesCallbackForEveryEvictedItem()
    {
        var dropped = new List<int>();
        var channel = Channel.CreateBounded<int>(
            new BoundedChannelOptions(2)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            },
            item => dropped.Add(item));

        for (int i = 1; i <= 5; i++)
        {
            Assert.True(channel.Writer.TryWrite(i));
        }

        Assert.Equal(new[] { 1, 2, 3 }, dropped);

        var remaining = new List<int>();
        while (channel.Reader.TryRead(out var value)) remaining.Add(value);
        Assert.Equal(new[] { 4, 5 }, remaining);
    }

    // Uses a foreign buffer (length 3) so ArrayPool.Return throws, making the first call
    // visible. Silence on the second call proves Interlocked.Exchange prevents double-return.
    [Fact]
    public void ReturnBuffer_AttemptsPoolReturnExactlyOnce()
    {
        // Length 3 maps to the 16-byte bucket, so the pool rejects it as foreign.
        var foreign = new byte[3];
        var frameData = NewFrameData(foreign);

        Assert.Throws<ArgumentException>(() => ReturnBuffer(frameData));

        // A second attempt would throw again; silence means the pool was never touched.
        ReturnBuffer(frameData);
        ReturnBuffer(frameData);
    }

    // ReturnBuffer clears the instance's reference, preventing use-after-return.
    [Fact]
    public void ReturnBuffer_ClearsBufferReference()
    {
        var rented = ArrayPool<byte>.Shared.Rent(1024);
        var frameData = NewFrameData(rented);

        Assert.Same(rented, BufferOf(frameData));

        ReturnBuffer(frameData);

        Assert.Empty(BufferOf(frameData));
        Assert.NotSame(rented, BufferOf(frameData));
    }

    // Regression guard: evicted frames get their buffers released rather than leaked.
    [Fact]
    public void FrameQueue_ReleasesBuffersOfDroppedFrames()
    {
        using var detector = new VisualEventDetector();

        var queueField = typeof(VisualEventDetector).GetField("_frameQueue",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(queueField);

        var channel = queueField!.GetValue(detector)!;
        var writer = channel.GetType().GetProperty("Writer")!.GetValue(channel)!;
        var tryWrite = writer.GetType().GetMethod("TryWrite")!;

        var frames = new List<object>();
        for (int i = 0; i < 5; i++)
        {
            var frameData = NewFrameData(ArrayPool<byte>.Shared.Rent(1024));
            frames.Add(frameData);
            Assert.True((bool)tryWrite.Invoke(writer, new[] { frameData })!);
        }

        // Capacity is 2, so the first three writes are evicted and must have been released.
        for (int i = 0; i < 3; i++)
        {
            Assert.Empty(BufferOf(frames[i]));
        }

        // The two still queued must retain their buffers.
        for (int i = 3; i < 5; i++)
        {
            Assert.NotEmpty(BufferOf(frames[i]));
        }

        foreach (var frameData in frames) ReturnBuffer(frameData);
    }
}
