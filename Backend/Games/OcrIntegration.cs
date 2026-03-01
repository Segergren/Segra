using Segra.Backend.Core.Models;
using Segra.Backend.Recorder;
using Serilog;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using global::Windows.Graphics.Imaging;
using global::Windows.Media.Ocr;

namespace Segra.Backend.Games
{
    /// <summary>
    /// Base class for game integrations that use OCR to detect on-screen events.
    /// Subclasses only need to provide configuration via <see cref="GetConfig"/>.
    /// </summary>
    internal abstract class OcrIntegration : Integration, IDisposable
    {
        private CancellationTokenSource? _cts;
        private readonly OcrEngine _ocrEngine;
        private readonly OcrConfig _config;
        private readonly Dictionary<BookmarkType, DateTime> _lastEventTime;
        private readonly Dictionary<BookmarkType, PendingEvent> _pendingEvents = new();

        private readonly record struct PendingEvent(
            string Keyword,
            IReadOnlyList<string> ExcludeFragments,
            DateTime DetectedAtUtc,
            DateTime DetectedAtLocal);

        protected record OcrConfig
        {
            public required string LogPrefix { get; init; }
            public required CropRegion CropRegion { get; init; }
            public required IReadOnlyList<OcrKeyword> Keywords { get; init; }
            public int Threshold { get; init; } = 150;
            public int PollIntervalMs { get; init; } = 250;
            public TimeSpan EventCooldown { get; init; } = TimeSpan.FromSeconds(5);
            public TimeSpan ExcludeCheckWindow { get; init; } = TimeSpan.FromSeconds(1.5);
            public TimeSpan TimeCompensation { get; init; } = TimeSpan.FromSeconds(1);
        }

        protected record CropRegion(double X, double Y, double Width, double Height);

        protected record OcrKeyword
        {
            public required string Text { get; init; }
            public required BookmarkType BookmarkType { get; init; }
            public IReadOnlyList<string> ExcludeFragments { get; init; } = [];
        }

        protected abstract OcrConfig GetConfig();

        protected OcrIntegration()
        {
            _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages()
                         ?? OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"))
                         ?? throw new InvalidOperationException("No OCR engine available");

            _config = GetConfig();

            // Initialize per-event-type cooldown tracking from configured keywords
            _lastEventTime = _config.Keywords
                .Select(k => k.BookmarkType)
                .Distinct()
                .ToDictionary(bt => bt, _ => DateTime.MinValue);
        }

        public override Task Start()
        {
            _cts = new CancellationTokenSource();
            Log.Information($"[{_config.LogPrefix}] Starting OCR integration");

            _ = Task.Run(() => MonitorLoop(_cts.Token));
            return Task.CompletedTask;
        }

        public override Task Shutdown()
        {
            Log.Information($"[{_config.LogPrefix}] Shutting down OCR integration");
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Shutdown().Wait();
        }

        private async Task MonitorLoop(CancellationToken token)
        {
            // Wait for game capture source to be available and hooked
            while (!token.IsCancellationRequested)
            {
                var source = OBSService.GameCaptureSource;
                if (source is { IsHooked: true })
                    break;

                await Task.Delay(1000, token).ConfigureAwait(false);
            }

            Log.Information($"[{_config.LogPrefix}] Game capture source hooked, starting OCR monitor");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var source = OBSService.GameCaptureSource;
                    if (source is not { IsHooked: true })
                    {
                        await Task.Delay(1000, token).ConfigureAwait(false);
                        continue;
                    }

                    var srcW = source.Width;
                    var srcH = source.Height;
                    if (srcW == 0 || srcH == 0)
                    {
                        await Task.Delay(_config.PollIntervalMs, token).ConfigureAwait(false);
                        continue;
                    }

                    var crop = _config.CropRegion;
                    var cropX = (uint)(srcW * crop.X);
                    var cropY = (uint)(srcH * crop.Y);
                    var cropW = (uint)(srcW * crop.Width);
                    var cropH = (uint)(srcH * crop.Height);

                    var screenshot = source.TakeScreenshot(cropX, cropY, cropW, cropH);
                    if (screenshot == null)
                    {
                        await Task.Delay(_config.PollIntervalMs, token).ConfigureAwait(false);
                        continue;
                    }

                    await ProcessScreenshot(screenshot.Pixels, screenshot.Width, screenshot.Height).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning($"[{_config.LogPrefix}] OCR monitor error: {ex.Message}");
                }

                await Task.Delay(_config.PollIntervalMs, token).ConfigureAwait(false);
            }
        }

        private async Task ProcessScreenshot(byte[] pixels, uint width, uint height)
        {
            int w = (int)width;
            int h = (int)height;
            int threshold = _config.Threshold;

            // Preprocess: grayscale + threshold to isolate bright notification text
            using var bitmap = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bmpData = bitmap.LockBits(
                new Rectangle(0, 0, w, h),
                ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                for (int y = 0; y < h; y++)
                {
                    var dstPtr = bmpData.Scan0 + y * bmpData.Stride;

                    for (int x = 0; x < w; x++)
                    {
                        int si = (y * w + x) * 4;
                        byte b = pixels[si];
                        byte g = pixels[si + 1];
                        byte r = pixels[si + 2];

                        byte gray = (byte)((r * 77 + g * 150 + b * 29) >> 8);
                        byte val = gray >= threshold ? (byte)255 : (byte)0;

                        Marshal.WriteByte(dstPtr, x * 4, val);       // B
                        Marshal.WriteByte(dstPtr, x * 4 + 1, val);   // G
                        Marshal.WriteByte(dstPtr, x * 4 + 2, val);   // R
                        Marshal.WriteByte(dstPtr, x * 4 + 3, 255);   // A
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }

            // Convert Bitmap to SoftwareBitmap for Windows OCR
            var softwareBitmap = await BitmapToSoftwareBitmap(bitmap).ConfigureAwait(false);
            if (softwareBitmap == null)
                return;

            using (softwareBitmap)
            {
                var result = await _ocrEngine.RecognizeAsync(softwareBitmap);
                var text = result.Text;

                // Process pending events even when OCR text is empty
                ProcessPendingEvents(text ?? "");

                if (string.IsNullOrWhiteSpace(text))
                    return;

                Log.Debug($"[{_config.LogPrefix}] OCR text: {text}");

                foreach (var keyword in _config.Keywords)
                {
                    if (!FuzzyContains(text, keyword.Text))
                        continue;

                    var now = DateTime.UtcNow;
                    if (now - _lastEventTime[keyword.BookmarkType] < _config.EventCooldown)
                        break;

                    if (keyword.ExcludeFragments.Count > 0)
                    {
                        // If exclude fragment already visible on this frame, skip entirely
                        if (keyword.ExcludeFragments.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase)))
                            break;

                        // Defer: wait for ExcludeCheckWindow before confirming
                        if (!_pendingEvents.ContainsKey(keyword.BookmarkType))
                        {
                            _pendingEvents[keyword.BookmarkType] = new PendingEvent(
                                keyword.Text, keyword.ExcludeFragments, now, DateTime.Now);
                            Log.Debug($"[{_config.LogPrefix}] Pending '{keyword.Text}' detection, waiting for confirmation");
                        }
                    }
                    else
                    {
                        // No exclude fragments — confirm immediately
                        _lastEventTime[keyword.BookmarkType] = now;
                        AddBookmark(keyword.BookmarkType);
                        Log.Information($"[{_config.LogPrefix}] Detected '{keyword.Text}' in OCR text -> {keyword.BookmarkType}");
                    }
                    break;
                }
            }
        }

        private static async Task<SoftwareBitmap?> BitmapToSoftwareBitmap(Bitmap bitmap)
        {
            using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using var memoryStream = new MemoryStream();

            bitmap.Save(memoryStream, ImageFormat.Bmp);
            memoryStream.Position = 0;

            await memoryStream.CopyToAsync(stream.AsStreamForWrite()).ConfigureAwait(false);
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            return softwareBitmap;
        }

        /// <summary>
        /// Checks if the OCR text contains a fuzzy match for the keyword.
        /// Splits OCR text into sliding windows of the keyword's word count and
        /// checks Levenshtein distance against a threshold.
        /// </summary>
        private static bool FuzzyContains(string text, string keyword)
        {
            // Exact match first (fast path)
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;

            // Short keywords (<=5 chars): exact match only to avoid false positives
            if (keyword.Length <= 5)
                return false;

            var keywordLower = keyword.ToLowerInvariant();
            int maxAllowed = keyword.Length / 4;

            // OCR sometimes splits words (e.g. "DEMOL ION" for "DEMOLITION")
            // Try matching with spaces removed for single-word keywords
            var keywordWords = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (keywordWords.Length == 1)
            {
                var textNoSpaces = text.Replace(" ", "").ToLowerInvariant();
                // Slide a character window over the spaceless text
                int kwLen = keywordLower.Length;
                for (int i = 0; i <= textNoSpaces.Length - kwLen; i++)
                {
                    var window = textNoSpaces.Substring(i, kwLen);
                    if (LevenshteinDistance(window, keywordLower) <= maxAllowed)
                        return true;
                }
            }

            // Fuzzy: slide a window of keyword's word count over OCR words
            var textWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int windowSize = keywordWords.Length;

            if (textWords.Length < windowSize)
                return false;

            for (int i = 0; i <= textWords.Length - windowSize; i++)
            {
                var window = string.Join(' ', textWords, i, windowSize);
                if (LevenshteinDistance(window.ToLowerInvariant(), keywordLower) <= maxAllowed)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Computes the Levenshtein edit distance between two strings.
        /// </summary>
        private static int LevenshteinDistance(string s, string t)
        {
            int n = s.Length, m = t.Length;
            if (n == 0) return m;
            if (m == 0) return n;

            // Use single-row optimization to avoid allocating full matrix
            var prev = new int[m + 1];
            var curr = new int[m + 1];

            for (int j = 0; j <= m; j++)
                prev[j] = j;

            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= m; j++)
                {
                    int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(prev[j] + 1, curr[j - 1] + 1),
                        prev[j - 1] + cost);
                }
                (prev, curr) = (curr, prev);
            }

            return prev[m];
        }

        private void ProcessPendingEvents(string ocrText)
        {
            var now = DateTime.UtcNow;
            var toRemove = new List<BookmarkType>();

            foreach (var (bookmarkType, pending) in _pendingEvents)
            {
                // Cancel if an exclude fragment appeared on a subsequent frame
                if (ocrText.Length > 0 &&
                    pending.ExcludeFragments.Any(f => ocrText.Contains(f, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Debug($"[{_config.LogPrefix}] Cancelled pending '{pending.Keyword}' — exclude fragment detected");
                    toRemove.Add(bookmarkType);
                    continue;
                }

                // Confirm after the check window passes without cancellation
                if (now - pending.DetectedAtUtc >= _config.ExcludeCheckWindow)
                {
                    // Another keyword (e.g. "+100") may have already fired for this type
                    if (now - _lastEventTime[bookmarkType] < _config.EventCooldown)
                    {
                        Log.Debug($"[{_config.LogPrefix}] Dropped pending '{pending.Keyword}' — already bookmarked by another keyword");
                        toRemove.Add(bookmarkType);
                        continue;
                    }

                    _lastEventTime[bookmarkType] = pending.DetectedAtUtc;
                    AddBookmark(bookmarkType, pending.DetectedAtLocal);
                    Log.Information($"[{_config.LogPrefix}] Confirmed '{pending.Keyword}' in OCR text -> {bookmarkType}");
                    toRemove.Add(bookmarkType);
                }
            }

            foreach (var key in toRemove)
                _pendingEvents.Remove(key);
        }

        private void AddBookmark(BookmarkType type, DateTime? detectionTime = null)
        {
            if (Settings.Instance.State.Recording == null)
            {
                Log.Warning($"[{_config.LogPrefix}] No recording active, skipping {type} bookmark");
                return;
            }

            var bookmark = new Bookmark
            {
                Type = type,
                Time = (detectionTime ?? DateTime.Now) - Settings.Instance.State.Recording.StartTime - _config.TimeCompensation
            };
            Settings.Instance.State.Recording.Bookmarks.Add(bookmark);
            Log.Information($"[{_config.LogPrefix}] BOOKMARK ADDED: {type} at {bookmark.Time}");
        }
    }
}
