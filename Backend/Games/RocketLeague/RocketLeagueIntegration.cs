using Segra.Backend.Core.Models;
using Segra.Backend.Recorder;
using Serilog;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using global::Windows.Graphics.Imaging;
using global::Windows.Media.Ocr;

namespace Segra.Backend.Games.RocketLeague
{
    /// <summary>
    /// Rocket League integration using OBS game capture screenshots + Windows OCR.
    /// Detects on-screen event notifications (Goal, Assist, Epic Save, etc.)
    /// by periodically screenshotting the game capture source and running OCR.
    /// </summary>
    internal class RocketLeagueIntegration : Integration, IDisposable
    {
        private CancellationTokenSource? _cts;
        private readonly OcrEngine _ocrEngine;

        // Fragments of "SHOT ON GOAL" — if any appear in OCR text, skip goal detection
        private static readonly string[] GoalExcludeFragments = ["shot", "on", "sh"];

        private static readonly (string Keyword, BookmarkType Type)[] Keywords =
        [
            ("+100", BookmarkType.Goal),    // Goal reward points — most reliable indicator
            ("goal", BookmarkType.Goal),    // Exact match only (fuzzy disabled for short words)
            ("assist", BookmarkType.Assist),
        ];

        // Per-event-type cooldown to prevent duplicate bookmarks
        private readonly Dictionary<BookmarkType, DateTime> _lastEventTime = new()
        {
            { BookmarkType.Goal, DateTime.MinValue },
            { BookmarkType.Assist, DateTime.MinValue },
        };

        private static readonly TimeSpan EventCooldown = TimeSpan.FromSeconds(5);
        private const int PollIntervalMs = 250;


        public RocketLeagueIntegration()
        {
            _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages()
                         ?? OcrEngine.TryCreateFromLanguage(new global::Windows.Globalization.Language("en-US"))
                         ?? throw new InvalidOperationException("No OCR engine available");
        }

        public override Task Start()
        {
            _cts = new CancellationTokenSource();
            Log.Information("[RL] Starting Rocket League CV integration");

            _ = Task.Run(() => MonitorLoop(_cts.Token));
            return Task.CompletedTask;
        }

        public override Task Shutdown()
        {
            Log.Information("[RL] Shutting down Rocket League CV integration");
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

            Log.Information("[RL] Game capture source hooked, starting OCR monitor");

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
                        await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
                        continue;
                    }

                    // Crop at GPU level: tight center where RL shows event text
                    // (15-25% from top, 38-62% wide)
                    var cropX = (uint)(srcW * 0.38);
                    var cropY = (uint)(srcH * 0.15);
                    var cropW = (uint)(srcW * 0.24); // 0.62 - 0.38
                    var cropH = (uint)(srcH * 0.10); // 0.25 - 0.15

                    var screenshot = source.TakeScreenshot(cropX, cropY, cropW, cropH);
                    if (screenshot == null)
                    {
                        await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
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
                    Log.Warning($"[RL] OCR monitor error: {ex.Message}");
                }

                await Task.Delay(PollIntervalMs, token).ConfigureAwait(false);
            }
        }

        private async Task ProcessScreenshot(byte[] pixels, uint width, uint height)
        {
            int w = (int)width;
            int h = (int)height;

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
                        byte val = gray >= 150 ? (byte)255 : (byte)0;

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

                if (string.IsNullOrWhiteSpace(text))
                    return;

                Log.Debug($"[RL] OCR text: {text}");

                foreach (var (keyword, bookmarkType) in Keywords)
                {
                    if (!FuzzyContains(text, keyword))
                        continue;

                    // For goals, reject if text contains shot-on-goal fragments
                    if (bookmarkType == BookmarkType.Goal &&
                        GoalExcludeFragments.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var now = DateTime.UtcNow;
                    if (now - _lastEventTime[bookmarkType] >= EventCooldown)
                    {
                        _lastEventTime[bookmarkType] = now;
                        AddBookmark(bookmarkType);
                        Log.Information($"[RL] Detected '{keyword}' in OCR text -> {bookmarkType}");
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

            // Short keywords (≤5 chars): exact match only to avoid false positives
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

        private static void AddBookmark(BookmarkType type)
        {
            if (Settings.Instance.State.Recording == null)
            {
                Log.Warning($"[RL] No recording active, skipping {type} bookmark");
                return;
            }

            var bookmark = new Bookmark
            {
                Type = type,
                // Subtract 1.5s to compensate for notification render delay
                Time = DateTime.Now - Settings.Instance.State.Recording.StartTime - TimeSpan.FromSeconds(1.5)
            };
            Settings.Instance.State.Recording.Bookmarks.Add(bookmark);
            Log.Information($"[RL] BOOKMARK ADDED: {type} at {bookmark.Time}");
        }
    }
}
