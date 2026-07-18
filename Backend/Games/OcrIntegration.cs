using Serilog;
using System.Drawing;
using Segra.Backend.Recorder;
using System.Drawing.Imaging;
using global::Windows.Media.Ocr;
using Segra.Backend.Core.Models;
using System.Runtime.InteropServices;
using global::Windows.Graphics.Imaging;

namespace Segra.Backend.Games
{
    /// <summary>
    /// Base class for game integrations that use OCR to detect on-screen events. Subclasses only
    /// need to provide configuration via <see cref="GetConfig"/>; this class owns the poll loop,
    /// keyword matching, and bookmark creation.
    ///
    /// Two OCR backends are supported, chosen per integration via <see cref="OcrConfig.UsePaddleOcr"/>:
    ///   - the built-in Windows.Media.Ocr (default) — fast, no download, but only reliable on clean
    ///     text, so its path binarizes (grayscale + <see cref="OcrConfig.Threshold"/>) first;
    ///   - <see cref="PaddleOcrEngine"/> — downloaded on first use (~400MB native runtime), reads
    ///     stylized, colored overlay text far better, at a higher CPU cost. Opt in only where the
    ///     built-in engine can't cope (e.g. MECCHA CHAMELEON's colored kill feed).
    /// </summary>
    internal abstract class OcrIntegration : Integration, IDisposable
    {
        private CancellationTokenSource? _cts;
        private readonly OcrEngine? _ocrEngine;
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

            /// <summary>
            /// When true, recognition uses the downloaded <see cref="PaddleOcrEngine"/> (color, no
            /// binarization). When false (default), uses the built-in Windows.Media.Ocr on a
            /// binarized crop. <see cref="Threshold"/> only applies to the built-in path.
            /// </summary>
            public bool UsePaddleOcr { get; init; }

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

            /// <summary>
            /// Optional: when set, called with the full OCR text once <see cref="Text"/> is matched,
            /// to decide which <see cref="BookmarkType"/> (if any) the event actually represents —
            /// e.g. disambiguating a shared kill-feed line ("X found Y") by checking which side the
            /// local player is on. Returning null means "matched, but not an event for us" and the
            /// frame is skipped (no bookmark, no cooldown applied). When set, <see cref="PossibleTypes"/>
            /// must list every <see cref="BookmarkType"/> this resolver can return, so cooldowns are
            /// tracked correctly.
            /// </summary>
            public Func<string, BookmarkType?>? Resolver { get; init; }
            public IReadOnlyList<BookmarkType> PossibleTypes { get; init; } = [];
        }

        protected abstract OcrConfig GetConfig();

        protected OcrIntegration()
        {
            _config = GetConfig();

            // The built-in Windows OCR engine is only needed for the non-Paddle path.
            if (!_config.UsePaddleOcr)
            {
                _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages()
                             ?? OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"))
                             ?? throw new InvalidOperationException("No OCR engine available");
            }

            // Initialize per-event-type cooldown tracking from configured keywords.
            // Resolver-based keywords can resolve to any of several types (see PossibleTypes),
            // so all of them need a cooldown entry, not just the keyword's default BookmarkType.
            _lastEventTime = _config.Keywords
                .SelectMany(k => k.Resolver != null && k.PossibleTypes.Count > 0 ? k.PossibleTypes : [k.BookmarkType])
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

            // For Paddle-backed integrations, ensure the engine is downloaded + loaded before polling.
            // This can take a while on first use (~400MB native runtime), during which recording
            // proceeds normally; if it fails, we skip OCR entirely rather than disturb the recording.
            if (_config.UsePaddleOcr &&
                !await PaddleOcrEngine.EnsureReadyAsync(token).ConfigureAwait(false))
            {
                Log.Warning($"[{_config.LogPrefix}] OCR engine unavailable, skipping OCR for this recording");
                return;
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
            string text = _config.UsePaddleOcr
                ? PaddleOcrEngine.Recognize(pixels, (int)width, (int)height)
                : await RecognizeWithWindowsOcr(pixels, (int)width, (int)height).ConfigureAwait(false);

            HandleText(text ?? "");
        }

        /// <summary>
        /// Built-in OCR path: grayscale + threshold the crop to isolate bright notification text,
        /// then hand it to Windows.Media.Ocr. Returns the recognized text (empty on failure).
        /// </summary>
        private async Task<string> RecognizeWithWindowsOcr(byte[] pixels, int w, int h)
        {
            int threshold = _config.Threshold;

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

            var softwareBitmap = await BitmapToSoftwareBitmap(bitmap).ConfigureAwait(false);
            if (softwareBitmap == null)
                return "";

            using (softwareBitmap)
            {
                var result = await _ocrEngine!.RecognizeAsync(softwareBitmap);
                return result.Text ?? "";
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

        private void HandleText(string text)
        {
            // Process pending events even when OCR text is empty
            ProcessPendingEvents(text);

            if (string.IsNullOrWhiteSpace(text))
                return;

            Log.Debug($"[{_config.LogPrefix}] OCR text: {text}");

            foreach (var keyword in _config.Keywords)
            {
                if (!FuzzyContains(text, keyword.Text))
                    continue;

                // Resolver keywords can match the text but decide it isn't an event for us
                // (e.g. neither side of a shared kill-feed line is the local player) — in
                // that case, skip this frame without starting a cooldown and keep polling.
                var resolvedType = keyword.Resolver != null ? keyword.Resolver(text) : keyword.BookmarkType;
                if (resolvedType == null)
                    break;

                var now = DateTime.UtcNow;
                if (now - _lastEventTime[resolvedType.Value] < _config.EventCooldown)
                    break;

                if (keyword.ExcludeFragments.Count > 0)
                {
                    // If exclude fragment already visible on this frame, skip entirely
                    if (keyword.ExcludeFragments.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase)))
                        break;

                    // Defer: wait for ExcludeCheckWindow before confirming
                    if (!_pendingEvents.ContainsKey(resolvedType.Value))
                    {
                        _pendingEvents[resolvedType.Value] = new PendingEvent(
                            keyword.Text, keyword.ExcludeFragments, now, DateTime.Now);
                        Log.Debug($"[{_config.LogPrefix}] Pending '{keyword.Text}' detection, waiting for confirmation");
                    }
                }
                else
                {
                    // No exclude fragments — confirm immediately
                    _lastEventTime[resolvedType.Value] = now;
                    AddBookmark(resolvedType.Value);
                    Log.Information($"[{_config.LogPrefix}] Detected '{keyword.Text}' in OCR text -> {resolvedType.Value}");
                }
                break;
            }
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
        protected static int LevenshteinDistance(string s, string t)
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
            var recording = AppState.Instance.Recording;
            if (recording == null)
            {
                Log.Warning($"[{_config.LogPrefix}] No recording active, skipping {type} bookmark");
                return;
            }

            var bookmark = new Bookmark
            {
                Type = type,
                Time = (detectionTime ?? DateTime.Now) - recording.StartTime - _config.TimeCompensation
            };
            recording.AddBookmark(bookmark);
            Log.Information($"[{_config.LogPrefix}] BOOKMARK ADDED: {type} at {bookmark.Time}");
        }
    }
}
