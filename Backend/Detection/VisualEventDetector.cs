using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ObsKit.NET;
using ObsKit.NET.Native.Types;
using ObsKit.NET.Video;
using Serilog;

namespace Segra.Backend.Detection;

public class VisualEventDetector : IDisposable
{
    private const int ModelInputSize = 640;
    private const int FpsDivisor = 30;
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
    private string? _gameId;
    private int _isProcessing;
    private List<RegionGroup> _regionGroups = new();
    private int _numClasses;

    public event Action<List<DetectionResult>>? DetectionsAvailable;

    public VisualEventDetector(int detectionIntervalMs = 1000)
    {
        _detectionIntervalMs = detectionIntervalMs;
    }

    private sealed class FrameData
    {
        public byte[] Buffer { get; set; } = Array.Empty<byte>();
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public void Start(string gameId)
    {
        _gameId = gameId;
        _session = ModelService.LoadModel(gameId);

        var definitions = ModelService.LoadEventDefinitions(gameId);
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

        Log.Information("VisualEventDetector: Started for game {GameId} with {RegionGroupCount} region groups",
            gameId, _regionGroups.Count);
    }

    public void Stop()
    {
        _cts?.Cancel();

        var sub = Interlocked.Exchange(ref _subscription, null);
        sub?.Dispose();

        if (_detectionLoop != null)
        {
            try
            {
                if (!_detectionLoop.Wait(TimeSpan.FromSeconds(3)))
                {
                    Log.Warning("VisualEventDetector: detection loop did not exit within 3s");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warning(ex, "VisualEventDetector: detection loop exit"); }
        }

        if (_gameId != null)
        {
            ModelService.UnloadModel(_gameId);
        }
        _session = null;
        _gameId = null;

        Log.Information("VisualEventDetector: Stopped");
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
            Log.Warning(ex, "VisualEventDetector: frame copy error");
        }
        finally
        {
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

                    var grayFrame = BgraToGray(frameData.Buffer, fW, fH);
                    try
                    {
                        // Skip near-black frames (loading screens, transitions) — they can produce NaN in the model
                        int brightPixels = 0;
                        int totalPixels = fW * fH;
                        for (int i = 0; i < totalPixels && brightPixels <= 10; i++)
                        {
                            if (grayFrame[i] > 15) brightPixels++;
                        }
                        if (brightPixels <= 10)
                        {
                            Log.Debug("DetectionLoop: skipping near-black frame");
                            continue;
                        }

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

                            try
                            {
                                var results = RunInferenceOnGray(session, resized);
                                if (results != null)
                                {
                                    MapDetectionsToFullFrame(results, group, fW, fH);
                                    allResults.AddRange(results);
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(resized);
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(grayFrame);
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
                Log.Warning(ex, "VisualEventDetector: detection error");
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

    internal static byte[] BgraToGray(byte[] bgra, int w, int h)
    {
        var pixels = w * h;
        var gray = ArrayPool<byte>.Shared.Rent(pixels);
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

    internal static byte[] CropAndResizeGray(byte[] srcGray, int srcW, int srcH,
        int cropX, int cropY, int cropW, int cropH, int dstW, int dstH)
    {
        var crop = ArrayPool<byte>.Shared.Rent(cropW * cropH);
        for (int y = 0; y < cropH; y++)
        {
            Array.Copy(srcGray, (cropY + y) * srcW + cropX, crop, y * cropW, cropW);
        }

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
        ArrayPool<byte>.Shared.Return(crop);
        return dst;
    }

    private List<DetectionResult>? RunInferenceOnGray(
        InferenceSession session, byte[] grayData)
    {
        try
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

            // Pin tensor memory to prevent GC from collecting it during native inference
            var handle = GCHandle.Alloc(inputTensor, GCHandleType.Pinned);
            try
            {
                var tensor = new DenseTensor<float>(inputTensor, new[] { 1, 3, inputSize, inputSize });
                var inputName = session.InputNames[0];
                var inputValue = NamedOnnxValue.CreateFromTensor(inputName, tensor);
                var container = new List<NamedOnnxValue> { inputValue };

                var outputNames = session.OutputMetadata.Keys.ToList();

                    using (var runOptions = new RunOptions())
                    using (var results = session.Run(container, outputNames, runOptions))
                    {
                        var result = results.First().AsTensor<float>().ToArray();
                        return ParseYoloOutput(result.AsSpan(), inputSize, _numClasses);
                    }
            }
            finally
            {
                handle.Free();
            }
        }
        catch (ObjectDisposedException)
        {
            Log.Debug("RunInferenceOnGray: session disposed");
            return null;
        }
        catch (OnnxRuntimeException ex)
        {
            Log.Warning(ex, "RunInferenceOnGray: ONNX error");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "RunInferenceOnGray: inference error");
            return null;
        }
    }

    private static List<DetectionResult> ParseYoloOutput(
        ReadOnlySpan<float> output, int inputSize, int numClasses)
    {
        var results = new List<DetectionResult>();
        var numDetections = output.Length / (4 + numClasses);

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

        var highestConf = results.Count > 0
            ? results.Max(r => r.Confidence)
            : 0f;

        if (results.Count == 0 && numDetections > 0)
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

        var classIds = results.Count > 0
            ? string.Join(",", results.Select(r => $"{r.ClassId}({r.Confidence:F2})"))
            : "none";
        Log.Debug("ParseYoloOutput: {Results} results, highestConf={Conf:F4}, classIds=[{ClassIds}], {Total} detections, numClasses={Classes}",
            results.Count, highestConf, classIds, numDetections, numClasses);
        return results;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}

