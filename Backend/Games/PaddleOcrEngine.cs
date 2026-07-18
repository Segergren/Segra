using Serilog;
using OpenCvSharp;
using Segra.Backend.App;
using Segra.Backend.Shared;
using Sdcb.PaddleOCR;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Segra.Backend.Games
{
    /// <summary>
    /// Shared PaddleOCR engine used by every <see cref="OcrIntegration"/>. PaddleOCR reads stylized,
    /// colored game-overlay text (kill feeds, "WASTED", etc.) far more reliably than the built-in
    /// Windows.Media.Ocr it replaced, but its native runtime is ~400MB, so it is NOT bundled in the
    /// installer. On first use this downloads:
    ///   - the native runtime DLLs, straight from nuget.org (a .nupkg is a zip), into an AppData
    ///     cache folder, loaded via <c>AddDllDirectory</c> so the whole transitive native dependency
    ///     chain (mkldnn, mklml, phi, ...) resolves;
    ///   - the English recognition model (~15MB) via PaddleOCR's own online-models downloader.
    /// Download failures are non-fatal: recording continues, OCR bookmarks just don't happen.
    /// CPU-only (Mkldnn) — no CUDA/GPU package is referenced anywhere.
    /// </summary>
    internal static class PaddleOcrEngine
    {
        private record NativePackage(string Id, string Version);

        // Pinned to match the managed assembly versions in Segra.csproj. CPU/MKL runtime only.
        private static readonly NativePackage[] NativePackages =
        [
            new("sdcb.paddleinference.runtime.win64.mkl", "3.3.1.70"),
            new("opencvsharp4.runtime.win", "4.13.0.20260627"),
        ];

        // Entry-point DLLs whose presence means the native runtime is already installed.
        private static readonly string[] SentinelDlls = ["paddle_inference_c.dll", "OpenCvSharpExtern.dll"];

        // Cap PaddleOCR's CPU math threads so inference doesn't fight the game for cores (see below).
        private const int OcrCpuThreads = 1;

        private static string RootDir => PathUtils.Combine(FolderNames.CacheFolder, "paddleocr");
        private static string NativeDir => PathUtils.Combine(RootDir, "runtime");
        private static string ModelDir => PathUtils.Combine(RootDir, "models");

        // Recognition-only (no detection stage): our crops are already tuned tight around a single
        // line of overlay text, so we skip the expensive text-DETECTION network and just run the
        // cheap RECOGNITION network on the whole crop. About 2x faster on CPU (measured ~193ms vs
        // ~395ms) with identical output on the tuned crops — detection is the part that hurt FPS.
        // Caveat: this assumes the crop is essentially one clean line; a loose crop with lots of
        // background may read poorly, so per-game CropRegions must stay tight.
        private static PaddleOcrRecognizer? _recognizer;
        private static bool _searchPathRegistered;
        private static readonly SemaphoreSlim _initLock = new(1, 1);

        public static bool IsReady => _recognizer != null;

        /// <summary>
        /// Ensures the native runtime + model are downloaded and the engine is loaded. Safe to call
        /// repeatedly and from multiple integrations; the heavy work happens once. Returns false if
        /// setup failed (caller should skip OCR and leave recording untouched).
        /// </summary>
        public static async Task<bool> EnsureReadyAsync(CancellationToken token)
        {
            if (_recognizer != null)
                return true;

            await _initLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_recognizer != null)
                    return true;

                await EnsureNativeRuntimeAsync(token).ConfigureAwait(false);
                RegisterSearchPath();

                await MessageService.SendFrontendMessage("OcrDownloadProgress", new { progress = 100, status = "preparing" });

                // Redirect PaddleOCR's model cache under Segra's AppData instead of its default
                // top-level %APPDATA%\paddleocr-models folder.
                Directory.CreateDirectory(ModelDir);
                global::Sdcb.PaddleOCR.Models.Online.Settings.GlobalModelDirectory = ModelDir;

                FullOcrModel model = await OnlineFullModels.EnglishV5.DownloadAsync(token).ConfigureAwait(false);

                // Recognition-only: use just the model's recognition network, skipping detection
                // (see field comment). cpuMathThreadCount defaults to 0 = "use every core", which
                // makes each run briefly saturate the whole CPU and tanks in-game FPS, so it's
                // capped low — the game keeps its cores and each run just takes a little longer
                // (harmless: the poll loop is serial and self-spacing).
                _recognizer = new PaddleOcrRecognizer(
                    model.RecognizationModel,
                    PaddleDevice.Mkldnn(cpuMathThreadCount: OcrCpuThreads));

                Log.Information("[PaddleOCR] Engine ready (recognition-only)");
                await MessageService.SendFrontendMessage("OcrDownloadProgress", new { progress = 100, status = "ready" });
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[PaddleOCR] Failed to initialize engine");
                await MessageService.SendFrontendMessage("OcrDownloadProgress", new { progress = 0, status = "error" });
                return false;
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Runs recognition on a raw BGRA pixel buffer (as returned by GameCapture.TakeScreenshot),
        /// treating the whole crop as a single line of text, and returns the recognized string.
        /// Must only be called once <see cref="IsReady"/>.
        /// </summary>
        public static string Recognize(byte[] bgraPixels, int width, int height)
        {
            var recognizer = _recognizer;
            if (recognizer == null)
                return "";

            // Inference is CPU-heavy native work that shares cores with the game and OBS. Drop this
            // thread's priority for the duration so that, on a saturated CPU, the OS scheduler starves
            // OCR (letting each run just take a little longer) rather than the game/encode threads.
            var thread = Thread.CurrentThread;
            var previousPriority = thread.Priority;
            thread.Priority = ThreadPriority.BelowNormal;
            try
            {
                // Wrap the BGRA buffer, drop alpha to BGR (PaddleOCR requires 3 or 1 channels).
                using var bgra = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgraPixels);
                using var bgr = new Mat();
                Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);

                return recognizer.Run(bgr).Text;
            }
            finally
            {
                thread.Priority = previousPriority;
            }
        }

        private static bool NativeRuntimeInstalled =>
            SentinelDlls.All(dll => File.Exists(Path.Combine(NativeDir, dll)));

        private static async Task EnsureNativeRuntimeAsync(CancellationToken token)
        {
            if (NativeRuntimeInstalled)
                return;

            Log.Information("[PaddleOCR] Downloading native runtime (~400MB, first use only)");
            Directory.CreateDirectory(NativeDir);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

            for (int i = 0; i < NativePackages.Length; i++)
            {
                var pkg = NativePackages[i];
                var url = $"https://api.nuget.org/v3-flatcontainer/{pkg.Id}/{pkg.Version}/{pkg.Id}.{pkg.Version}.nupkg";

                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? -1L;

                var tmp = Path.Combine(NativeDir, pkg.Id + ".nupkg.tmp");
                await using (var src = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    long read = 0;
                    int lastReported = -1;
                    int n;
                    while ((n = await src.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
                        read += n;
                        if (total > 0)
                        {
                            // Scale each package's local progress into an overall 0-100 across all packages.
                            double packageFraction = (double)read / total;
                            int overall = (int)((i + packageFraction) / NativePackages.Length * 100);
                            if (overall != lastReported)
                            {
                                lastReported = overall;
                                await MessageService.SendFrontendMessage("OcrDownloadProgress",
                                    new { progress = overall, status = "downloading" });
                            }
                        }
                    }
                }

                ExtractNativeDlls(tmp);
                File.Delete(tmp);
            }

            Log.Information("[PaddleOCR] Native runtime downloaded");
        }

        // Copies just the runtimes/win-x64/native/*.dll entries out of the nupkg, flattened into
        // NativeDir so a single AddDllDirectory covers the whole set.
        private static void ExtractNativeDlls(string nupkgPath)
        {
            using var zip = ZipFile.OpenRead(nupkgPath);
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith("runtimes/win-x64/native/", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;

                entry.ExtractToFile(Path.Combine(NativeDir, Path.GetFileName(name)), overwrite: true);
            }
        }

        // Adds NativeDir to the Win32 loader search path so both the P/Invoke entry-point DLLs and
        // their transitive native dependencies resolve from the downloaded location. Process-global
        // state, so it's done once per process before the first Paddle/OpenCV P/Invoke.
        private static void RegisterSearchPath()
        {
            if (_searchPathRegistered)
                return;

            SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
            var cookie = AddDllDirectory(NativeDir);
            if (cookie == IntPtr.Zero)
                throw new InvalidOperationException($"AddDllDirectory failed for {NativeDir} (error {Marshal.GetLastWin32Error()})");

            _searchPathRegistered = true;
        }

        private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDefaultDllDirectories(uint DirectoryFlags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr AddDllDirectory(string NewDirectory);
    }
}
