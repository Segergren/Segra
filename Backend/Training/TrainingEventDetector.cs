#if ENABLE_TRAINING

using System.Buffers;
using System.Threading.Channels;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ObsKit.NET;
using ObsKit.NET.Native.Types;
using ObsKit.NET.Video;
using Segra.Backend.Detection;
using Serilog;

namespace Segra.Backend.Training;

public class TrainingEventDetector : IDisposable
{
    private const int ModelInputSize = 640;
    private const int FpsDivisor = 60;
    private const int ObsSubscribeWidth = 1920;
    private const int ObsSubscribeHeight = 1080;

    private readonly int _detectionIntervalMs;
    private RawVideoSubscription? _subscription;
    private CancellationTokenSource? _cts;
    private Task? _detectionLoop;
    private readonly Channel<FrameData> _frameQueue = Channel.CreateBounded<FrameData>(
        new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private InferenceSession? _session;
    private int _isProcessing;
    private List<RegionGroup> _regionGroups = new();
    private int _numClasses;
    private bool _dumpedFrame;

    public event Action<List<DetectionResult>>? DetectionsAvailable;

    public TrainingEventDetector(int detectionIntervalMs = 1000)
    {
        _detectionIntervalMs = detectionIntervalMs;
    }

    private sealed class FrameData
    {
        public byte[] Buffer { get; set; } = Array.Empty<byte>();
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public bool Start(string gameId)
    {
        _session = TrainingEventService.LoadModel(gameId);
        if (_session == null)
        {
            Log.Warning("TrainingEventDetector: No model found for game {GameId}", gameId);
            return false;
        }

        var definitions = TrainingEventService.LoadEventDefinitions(gameId);
        _numClasses = definitions.Count;
        _regionGroups = BuildRegionGroups(definitions);

        _subscription = Obs.SubscribeRawVideo(
            VideoFormat.BGRA,
            width: ObsSubscribeWidth,
            height: ObsSubscribeHeight,
            callback: OnFrame,
            frameRateDivisor: FpsDivisor);

        _cts = new CancellationTokenSource();
        _detectionLoop = Task.Run(() => DetectionLoopAsync(_cts.Token));

        Log.Information("TrainingEventDetector: Started for game {GameId} with {RegionGroupCount} region groups",
            gameId, _regionGroups.Count);
        return true;
    }

    public void Stop()
    {
        _cts?.Cancel();

        var sub = Interlocked.Exchange(ref _subscription, null);
        sub?.Dispose();

        try { _detectionLoop?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warning(ex, "TrainingEventDetector: detection loop exit"); }

        _session?.Dispose();
        _session = null;

        Log.Information("TrainingEventDetector: Stopped");
    }

    private void OnFrame(in RawVideoFrame frame)
    {
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0)
            return;

        try
        {
            var srcStride = (int)frame.GetLinesize(0);
            var width = (int)frame.Width;
            var height = (int)frame.Height;
            var rowBytes = width * 4;
            var buffer = ArrayPool<byte>.Shared.Rent(height * rowBytes);

            var src = frame.GetPlane(0, (uint)height);
            for (int y = 0; y < height; y++)
            {
                src.Slice(y * srcStride, rowBytes)
                   .CopyTo(new Span<byte>(buffer, y * rowBytes, rowBytes));
            }

            _frameQueue.Writer.TryWrite(new FrameData
            {
                Buffer = buffer,
                Width = width,
                Height = height
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "TrainingEventDetector: frame copy error");
            Interlocked.Exchange(ref _isProcessing, 0);
        }
    }

    private async Task DetectionLoopAsync(CancellationToken ct)
    {
        var session = _session;
        if (session == null) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_detectionIntervalMs, ct);

                if (!_frameQueue.Reader.TryRead(out var frameData))
                {
                    Log.Debug("DetectionLoop: no frame available");
                    continue;
                }

                Log.Debug("DetectionLoop: processing frame {W}x{H}", frameData.Width, frameData.Height);

                try
                {
                    var allResults = new List<DetectionResult>();
                    var fW = frameData.Width;
                    var fH = frameData.Height;
                    var fPixels = fW * fH;

                    // Full-frame grayscale once (not per-region)
                    var grayFrame = BgraToGray(frameData.Buffer, fW, fH);

                    foreach (var group in _regionGroups)
                    {
                        var cropW = (int)(group.W * fW);
                        var cropH = (int)(group.H * fH);
                        var cropX = (int)(group.X * fW);
                        var cropY = (int)(group.Y * fH);

                        if (cropW <= 0 || cropH <= 0) continue;
                        if (cropX + cropW > fW) cropW = fW - cropX;
                        if (cropY + cropH > fH) cropH = fH - cropY;
                        if (cropW <= 0 || cropH <= 0) continue;

                        var resized = CropAndResizeGray(grayFrame, fW, fH, cropX, cropY, cropW, cropH, ModelInputSize, ModelInputSize);

                        // DEBUG: dump first region crop once for Python comparison
                        if (!_dumpedFrame)
                        {
                            _dumpedFrame = true;
                            var dumpDir = Path.Combine(AppContext.BaseDirectory, "data", "training", "debug");
                            Directory.CreateDirectory(dumpDir);
                            var dumpPath = Path.Combine(dumpDir, "live_crop.raw");
                            File.WriteAllBytes(dumpPath, resized[..(ModelInputSize * ModelInputSize)]);
                            Log.Information("FRAME DUMP: region [{X:F4},{Y:F4},{W:F4},{H:F4}] crop {CropW}x{CropH} -> {Dst}x{Dst} saved to {Path}",
                                group.X, group.Y, group.W, group.H, cropW, cropH, ModelInputSize, ModelInputSize, dumpPath);
                        }

                        try
                        {
                            var results = RunInferenceOnGray(session, resized);
                            MapDetectionsToFullFrame(results, group, fW, fH);
                            allResults.AddRange(results);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(resized);
                        }
                    }

                    Log.Debug("DetectionLoop: {Count} results across {Groups} groups", allResults.Count, _regionGroups.Count);
                    DetectionsAvailable?.Invoke(allResults);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(frameData.Buffer);
                    Interlocked.Exchange(ref _isProcessing, 0);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warning(ex, "TrainingEventDetector: detection error");
                Interlocked.Exchange(ref _isProcessing, 0);
            }
        }
    }

    internal static byte[]? CropFrame(byte[] src, int srcW, int srcH, RegionGroup region)
    {
        var cropX = (int)(region.X * srcW);
        var cropY = (int)(region.Y * srcH);
        var cropW = (int)(region.W * srcW);
        var cropH = (int)(region.H * srcH);

        if (cropW <= 0 || cropH <= 0) return null;

        if (cropX + cropW > srcW) cropW = srcW - cropX;
        if (cropY + cropH > srcH) cropH = srcH - cropY;
        if (cropW <= 0 || cropH <= 0) return null;

        var cropBuffer = ArrayPool<byte>.Shared.Rent(cropW * cropH * 4);
        var srcRowStride = srcW * 4;

        for (int y = 0; y < cropH; y++)
        {
            var srcOffset = (cropY + y) * srcRowStride + cropX * 4;
            var dstOffset = y * cropW * 4;
            Buffer.BlockCopy(src, srcOffset, cropBuffer, dstOffset, cropW * 4);
        }

        return cropBuffer;
    }

    internal static byte[] ResizeBgra(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = ArrayPool<byte>.Shared.Rent(dstW * dstH * 4);
        var srcBytesPerRow = srcW * 4;

        for (int dy = 0; dy < dstH; dy++)
        {
            float sy = (dy + 0.5f) * srcH / dstH - 0.5f;
            if (sy < 0) sy = 0;
            if (sy >= srcH - 1) sy = srcH - 1.001f;
            int sy0 = (int)sy;
            int sy1 = sy0 + 1;
            float fy = sy - sy0;

            for (int dx = 0; dx < dstW; dx++)
            {
                float sx = (dx + 0.5f) * srcW / dstW - 0.5f;
                if (sx < 0) sx = 0;
                if (sx >= srcW - 1) sx = srcW - 1.001f;
                int sx0 = (int)sx;
                int sx1 = sx0 + 1;
                float fx = sx - sx0;

                var idx00 = sy0 * srcBytesPerRow + sx0 * 4;
                var idx01 = sy0 * srcBytesPerRow + sx1 * 4;
                var idx10 = sy1 * srcBytesPerRow + sx0 * 4;
                var idx11 = sy1 * srcBytesPerRow + sx1 * 4;

                var dstIdx = (dy * dstW + dx) * 4;

                for (int c = 0; c < 4; c++)
                {
                    float v =
                        (1 - fx) * (1 - fy) * src[idx00 + c] +
                        fx * (1 - fy) * src[idx01 + c] +
                        (1 - fx) * fy * src[idx10 + c] +
                        fx * fy * src[idx11 + c];
                    dst[dstIdx + c] = (byte)v;
                }
            }
        }

        return dst;
    }

    internal static void MapDetectionsToFullFrame(List<DetectionResult> detections,
        RegionGroup group, int frameW, int frameH)
    {
        float cropW = group.W * frameW;
        float cropH = group.H * frameH;
        float regionLeft = group.X * frameW;
        float regionTop = group.Y * frameH;

        foreach (var det in detections)
        {
            float fullX = det.X * cropW + regionLeft;
            float fullY = det.Y * cropH + regionTop;
            float fullW = det.Width * cropW;
            float fullH = det.Height * cropH;

            det.X = fullX / frameW;
            det.Y = fullY / frameH;
            det.Width = fullW / frameW;
            det.Height = fullH / frameH;
        }
    }

    internal static List<RegionGroup> BuildRegionGroups(List<EventDefinition> definitions)
    {
        var groups = new List<RegionGroup>();
        bool hasFullFrame = false;

        foreach (var def in definitions)
        {
            if (def.ScreenRegionW.HasValue && def.ScreenRegionW.Value > 0)
            {
                var r = new RegionGroup
                {
                    X = def.ScreenRegionX ?? 0,
                    Y = def.ScreenRegionY ?? 0,
                    W = def.ScreenRegionW.Value,
                    H = def.ScreenRegionH ?? 0
                };

                bool merged = false;
                foreach (var g in groups)
                {
                    if (RegionsOverlap(g, r))
                    {
                        MergeRegions(g, r);
                        merged = true;
                        break;
                    }
                }

                if (!merged)
                    groups.Add(r);
            }
            else
            {
                hasFullFrame = true;
            }
        }

        if (hasFullFrame)
            groups.Add(new RegionGroup { X = 0, Y = 0, W = 1, H = 1 });

        if (groups.Count == 0)
            groups.Add(new RegionGroup { X = 0, Y = 0, W = 1, H = 1 });

        return groups;
    }

    internal static bool RegionsOverlap(RegionGroup a, RegionGroup b)
    {
        if (a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y)
            return true;

        if (a.X >= b.X && a.Y >= b.Y && a.X + a.W <= b.X + b.W && a.Y + a.H <= b.Y + b.H)
            return true;

        if (b.X >= a.X && b.Y >= a.Y && b.X + b.W <= a.X + a.W && b.Y + b.H <= a.Y + a.H)
            return true;

        return false;
    }

    internal static void MergeRegions(RegionGroup a, RegionGroup b)
    {
        float x = Math.Min(a.X, b.X);
        float y = Math.Min(a.Y, b.Y);
        a.W = Math.Max(a.X + a.W, b.X + b.W) - x;
        a.H = Math.Max(a.Y + a.H, b.Y + b.H) - y;
        a.X = x;
        a.Y = y;
    }

    /// <summary>Convert full BGRA frame to grayscale luminance (one byte per pixel).</summary>
    internal static byte[] BgraToGray(byte[] bgra, int w, int h)
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

    /// <summary>Crop region from full-frame grayscale then bilinear resize (matches OpenCV's INTER_LINEAR).</summary>
    internal static byte[] CropAndResizeGray(byte[] srcGray, int srcW, int srcH,
        int cropX, int cropY, int cropW, int cropH, int dstW, int dstH)
    {
        // Step 1: crop rect from grayscale
        var crop = new byte[cropW * cropH];
        for (int y = 0; y < cropH; y++)
        {
            Array.Copy(srcGray, (cropY + y) * srcW + cropX, crop, y * cropW, cropW);
        }

        // Step 2: bilinear resize (OpenCV INTER_LINEAR convention)
        var dst = ArrayPool<byte>.Shared.Rent(dstW * dstH);
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

    private static List<DetectionResult> RunInferenceOnGray(
        InferenceSession session, byte[] grayData)
    {
        const int inputSize = ModelInputSize;
        var pixels = inputSize * inputSize;
        var inputTensor = new float[pixels * 3];

        for (int i = 0; i < pixels; i++)
        {
            var val = grayData[i] / 255f;
            inputTensor[i] = val;
            inputTensor[i + pixels] = val;
            inputTensor[i + 2 * pixels] = val;
        }

        var tensor = new DenseTensor<float>(inputTensor, new[] { 1, 3, inputSize, inputSize });
        var inputName = session.InputNames[0];
        var inputValue = NamedOnnxValue.CreateFromTensor(inputName, tensor);
        var container = new List<NamedOnnxValue> { inputValue };

        var outputNames = session.OutputMetadata.Keys.ToList();

        using (var runOptions = new RunOptions())
        using (var results = session.Run(container, outputNames, runOptions))
        {
            var result = results.First().AsTensor<float>().ToArray();
            return ParseYoloOutput(result.AsSpan(), inputSize);
        }
    }

    private static List<DetectionResult> ParseYoloOutput(
        ReadOnlySpan<float> output, int inputSize)
    {
        var results = new List<DetectionResult>();
        var numDetections = 8400;
        var numClasses = output.Length / numDetections - 4;

        for (int i = 0; i < numDetections; i++)
        {
            var classId = 0;
            var maxConf = 0f;
            for (int c = 0; c < numClasses; c++)
            {
                var conf = output[(4 + c) * numDetections + i];
                if (conf > maxConf)
                {
                    maxConf = conf;
                    classId = c;
                }
            }

            if (maxConf < 0.7f) continue;

            var cx = output[i] / inputSize;
            var cy = output[1 * numDetections + i] / inputSize;
            var w = output[2 * numDetections + i] / inputSize;
            var h = output[3 * numDetections + i] / inputSize;

            results.Add(new DetectionResult
            {
                ClassId = classId,
                Confidence = maxConf,
                X = cx - w / 2,
                Y = cy - h / 2,
                Width = w,
                Height = h,
                Timestamp = DateTime.Now
            });
        }

        float highestConf = 0;
        if (results.Count == 0)
        {
            for (int i = 0; i < numDetections; i++)
            {
                for (int c = 0; c < numClasses; c++)
                {
                    var conf = output[(4 + c) * numDetections + i];
                    if (conf > highestConf) highestConf = conf;
                }
            }
        }
        Log.Debug("ParseYoloOutput: {Results} results, highestConf={Conf:F4}, {Total} detections, numClasses={Classes}",
            results.Count, highestConf, numDetections, numClasses);
        return results;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
#endif
