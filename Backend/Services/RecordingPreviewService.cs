using System.Buffers;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ObsKit.NET;
using ObsKit.NET.Native.Types;
using ObsKit.NET.Video;
using Segra.Backend.App;
using Serilog;

namespace Segra.Backend.Services
{
    /// <summary>
    /// Streams a low-resolution JPEG preview of the active OBS canvas to the frontend.
    /// Off by default per recording — toggled on by the user (issue #138 / #127).
    /// </summary>
    public static class RecordingPreviewService
    {
        private const uint PreviewWidth = 480;
        private const uint PreviewHeight = 270;
        private const int TargetFps = 10;
        private const long JpegQuality = 65L;

        private static readonly object _lock = new();
        private static RawVideoSubscription? _subscription;
        private static int _isEncoding;
        private static uint _recordingFps;
        private static bool _recordingActive;
        private static bool _enabled;
        private static readonly ImageCodecInfo? _jpegCodec =
            ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/jpeg");

        /// <summary>
        /// Whether the preview is currently streaming frames.
        /// </summary>
        public static bool IsEnabled => _enabled;

        /// <summary>
        /// Called when a recording starts. Caches the recording fps so a later toggle can pick the right divisor.
        /// Preview always starts disabled; the user toggles it via the keybind.
        /// </summary>
        public static void OnRecordingStarted(uint recordingFps)
        {
            lock (_lock)
            {
                _recordingFps = recordingFps;
                _recordingActive = true;
                _enabled = false;
            }

            BroadcastState();
        }

        /// <summary>
        /// Called when a recording stops. Tears down any active subscription.
        /// </summary>
        public static void OnRecordingStopped()
        {
            lock (_lock)
            {
                _recordingActive = false;
                _enabled = false;
                DisposeSubscriptionLocked();
            }

            BroadcastState();
        }

        /// <summary>
        /// Toggles the preview on/off. No-op if no recording is active.
        /// </summary>
        public static void Toggle()
        {
            lock (_lock)
            {
                if (!_recordingActive)
                    return;

                if (_enabled)
                {
                    _enabled = false;
                    DisposeSubscriptionLocked();
                }
                else
                {
                    _enabled = StartSubscriptionLocked();
                }
            }

            BroadcastState();
        }

        private static bool StartSubscriptionLocked()
        {
            DisposeSubscriptionLocked();

            uint divisor = _recordingFps == 0 ? 1u : Math.Max(1u, _recordingFps / (uint)TargetFps);
            try
            {
                _subscription = Obs.SubscribeRawVideo(
                    VideoFormat.BGRA,
                    PreviewWidth,
                    PreviewHeight,
                    OnFrame,
                    frameRateDivisor: divisor);
                Log.Information("Recording preview enabled ({W}x{H}, divisor={Divisor} from {Fps}fps)",
                    PreviewWidth, PreviewHeight, divisor, _recordingFps);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to enable recording preview");
                _subscription = null;
                return false;
            }
        }

        private static void DisposeSubscriptionLocked()
        {
            var sub = _subscription;
            if (sub == null) return;
            _subscription = null;
            try
            {
                sub.Dispose();
                Log.Information("Recording preview disabled");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error disposing recording preview subscription");
            }
        }

        private static void BroadcastState()
        {
            _ = MessageService.SendFrontendMessage("RecordingPreviewState", new { enabled = _enabled });
        }

        private static void OnFrame(in RawVideoFrame frame)
        {
            if (!_enabled)
                return;

            // Drop frames if the previous one is still encoding/sending.
            if (Interlocked.CompareExchange(ref _isEncoding, 1, 0) != 0)
                return;

            int width = (int)frame.Width;
            int height = (int)frame.Height;
            int srcStride = (int)frame.GetLinesize(0);
            int rowBytes = width * 4;
            int packedSize = rowBytes * height;

            // Copy out of the native buffer (only valid during this callback) into a tightly-packed array.
            var buffer = ArrayPool<byte>.Shared.Rent(packedSize);
            try
            {
                var src = frame.GetPlane(0, frame.Height);
                if (src.IsEmpty)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    Interlocked.Exchange(ref _isEncoding, 0);
                    return;
                }

                for (int y = 0; y < height; y++)
                {
                    src.Slice(y * srcStride, rowBytes).CopyTo(buffer.AsSpan(y * rowBytes, rowBytes));
                }
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                Interlocked.Exchange(ref _isEncoding, 0);
                throw;
            }

            // Off-thread: encode to JPEG and ship over WebSocket. Free the buffer when done.
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_enabled)
                        await EncodeAndSendAsync(buffer, width, height, packedSize);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Preview frame encode/send failed");
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    Interlocked.Exchange(ref _isEncoding, 0);
                }
            });
        }

        private static async Task EncodeAndSendAsync(byte[] bgra, int width, int height, int packedSize)
        {
            if (_jpegCodec == null)
                return;

            byte[] jpegBytes;
            using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            {
                var rect = new Rectangle(0, 0, width, height);
                var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int srcRow = width * 4;
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(bgra, y * srcRow, data.Scan0 + y * data.Stride, srcRow);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                using var ms = new MemoryStream(packedSize / 4);
                using var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
                bmp.Save(ms, _jpegCodec, encoderParams);
                jpegBytes = ms.ToArray();
            }

            var b64 = Convert.ToBase64String(jpegBytes);
            await MessageService.SendFrontendMessage("RecordingPreviewFrame", new
            {
                jpegBase64 = b64,
                width,
                height
            });
        }
    }
}
