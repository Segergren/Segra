using Segra.Backend.Shared;
using Serilog;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Segra.Backend.Recorder
{
    /// <summary>
    /// Probes NVENC encoder capabilities by running OBS's obs-nvenc-test.exe (shipped with the
    /// OBS bundle) and caches the result in AppData. The cache is invalidated when the GPU,
    /// driver version, or the test executable itself changes, so the probe only runs once per
    /// hardware/driver configuration.
    /// </summary>
    internal static class NvencCapsService
    {
        public class CodecCaps
        {
            [JsonPropertyName("supported")]
            public bool Supported { get; set; }

            [JsonPropertyName("bframes")]
            public int BFrames { get; set; }
        }

        public class NvencCaps
        {
            [JsonPropertyName("fingerprint")]
            public string Fingerprint { get; set; } = string.Empty;

            [JsonPropertyName("probedAt")]
            public DateTime ProbedAt { get; set; }

            [JsonPropertyName("nvencSupported")]
            public bool NvencSupported { get; set; }

            [JsonPropertyName("codecs")]
            public Dictionary<string, CodecCaps> Codecs { get; set; } = new();
        }

        // Maps OBS NVENC encoder ids to the codec section names emitted by obs-nvenc-test
        private static readonly Dictionary<string, string> EncoderIdToCodec = new(StringComparer.OrdinalIgnoreCase)
        {
            ["jim_nvenc"] = "h264",
            ["obs_nvenc_h264_tex"] = "h264",
            ["ffmpeg_nvenc"] = "h264",
            ["jim_hevc_nvenc"] = "hevc",
            ["obs_nvenc_hevc_tex"] = "hevc",
            ["ffmpeg_hevc_nvenc"] = "hevc",
            ["jim_av1_nvenc"] = "av1",
            ["obs_nvenc_av1_tex"] = "av1",
        };

        private static readonly object _lock = new();
        private static Task<NvencCaps?>? _capsTask;

        private static string CacheFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra", "nvenc_caps.json");

        private static string TestExePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "obs-nvenc-test.exe");

        /// <summary>
        /// Starts loading/probing capabilities in the background. No-op on non-NVIDIA systems
        /// or if already started. Must be called after the OBS bundle is installed since the
        /// test executable ships with it.
        /// </summary>
        public static void StartProbe()
        {
            lock (_lock)
            {
                if (_capsTask != null)
                    return;

                if (GeneralUtils.DetectGpuVendor() != GeneralUtils.GpuVendor.Nvidia)
                    return;

                _capsTask = Task.Run(LoadOrProbeAsync);
            }
        }

        /// <summary>
        /// Returns the max number of consecutive b-frames the GPU supports for the given encoder id,
        /// or null if the encoder is not an NVENC encoder or capabilities are unavailable.
        /// Waits briefly if the probe is still running.
        /// </summary>
        public static int? GetMaxBFrames(string encoderId)
        {
            if (!EncoderIdToCodec.TryGetValue(encoderId, out string? codec))
                return null;

            var caps = GetCaps(TimeSpan.FromSeconds(15));
            if (caps == null)
            {
                Log.Warning($"NVENC capabilities unavailable for {encoderId}, leaving encoder b-frame defaults");
                return null;
            }

            if (!caps.NvencSupported)
            {
                Log.Warning($"NVENC reported as unsupported by probe, leaving b-frame defaults for {encoderId}");
                return null;
            }

            if (!caps.Codecs.TryGetValue(codec, out var codecCaps) || !codecCaps.Supported)
            {
                Log.Warning($"NVENC probe reports codec {codec} as unsupported, leaving b-frame defaults for {encoderId}");
                return null;
            }

            Log.Information($"NVENC {codec} supports up to {codecCaps.BFrames} b-frames on this GPU");
            return codecCaps.BFrames;
        }

        /// <summary>
        /// Returns a one-line summary of the probed capabilities without blocking, or null if
        /// the probe hasn't completed. Used for diagnostics logging.
        /// </summary>
        public static string? GetCapsSummaryOrNull()
        {
            Task<NvencCaps?>? task;
            lock (_lock)
            {
                task = _capsTask;
            }

            if (task == null || !task.IsCompletedSuccessfully || task.Result == null)
                return null;

            var caps = task.Result;
            string codecSummary = string.Join(", ", caps.Codecs.Select(c =>
                $"{c.Key}: supported={c.Value.Supported}, bframes={c.Value.BFrames}"));
            return $"supported={caps.NvencSupported} ({codecSummary})";
        }

        private static NvencCaps? GetCaps(TimeSpan timeout)
        {
            Task<NvencCaps?>? task;
            lock (_lock)
            {
                task = _capsTask;
            }

            if (task == null)
                return null;

            try
            {
                // Completes instantly on a cache hit; only the first run after a hardware or
                // driver change actually waits for the test process.
                if (!task.Wait(timeout))
                {
                    Log.Warning($"Timed out after {timeout.TotalSeconds}s waiting for the NVENC capability probe");
                    return null;
                }
                return task.Result;
            }
            catch (Exception ex)
            {
                Log.Warning($"NVENC capability probe failed: {ex.Message}");
                return null;
            }
        }

        private static async Task<NvencCaps?> LoadOrProbeAsync()
        {
            try
            {
                string exePath = TestExePath;
                if (!File.Exists(exePath))
                {
                    Log.Warning($"obs-nvenc-test.exe not found at {exePath}, NVENC capabilities unavailable");
                    return null;
                }

                string fingerprint = GetFingerprint(exePath);

                var cached = TryLoadCache(fingerprint);
                if (cached != null)
                {
                    Log.Information($"NVENC capabilities loaded from cache: {Summarize(cached)}");
                    return cached;
                }

                var probed = await ProbeAsync(exePath, fingerprint);
                if (probed != null)
                {
                    SaveCache(probed);
                    Log.Information($"NVENC capabilities probed: {Summarize(probed)}");
                }
                return probed;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to determine NVENC capabilities: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Builds a fingerprint from all video controllers (device id + driver version) plus the
        /// test executable's timestamp/size, so the cached result is re-probed after a GPU swap,
        /// driver update, or OBS update.
        /// </summary>
        private static string GetFingerprint(string exePath)
        {
            var parts = new List<string>();
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT PNPDeviceID, DriverVersion FROM Win32_VideoController");
                foreach (var gpu in searcher.Get())
                {
                    parts.Add($"{gpu["PNPDeviceID"]}|{gpu["DriverVersion"]}");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to enumerate video controllers for NVENC caps fingerprint: {ex.Message}");
            }
            parts.Sort(StringComparer.OrdinalIgnoreCase);

            var exeInfo = new FileInfo(exePath);
            parts.Add($"exe:{exeInfo.LastWriteTimeUtc.Ticks}|{exeInfo.Length}");

            return string.Join(";", parts);
        }

        private static NvencCaps? TryLoadCache(string fingerprint)
        {
            try
            {
                if (!File.Exists(CacheFilePath))
                    return null;

                var cached = JsonSerializer.Deserialize<NvencCaps>(File.ReadAllText(CacheFilePath));
                if (cached == null || cached.Fingerprint != fingerprint)
                {
                    Log.Information("NVENC capability cache is stale (hardware, driver or OBS changed), re-probing");
                    return null;
                }

                return cached;
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to read NVENC capability cache: {ex.Message}");
                return null;
            }
        }

        private static void SaveCache(NvencCaps caps)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath)!);
                File.WriteAllText(CacheFilePath, JsonSerializer.Serialize(caps, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to write NVENC capability cache: {ex.Message}");
            }
        }

        private static async Task<NvencCaps?> ProbeAsync(string exePath, string fingerprint)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };

            var stopwatch = Stopwatch.StartNew();
            using var process = Process.Start(psi);
            if (process == null)
            {
                Log.Warning("Failed to start obs-nvenc-test.exe");
                return null;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();

            // The test exe terminates itself after 2.5s internally; this is just a safety net
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(cts.Token);
                stopwatch.Stop();
                Log.Information($"obs-nvenc-test.exe finished in {stopwatch.ElapsedMilliseconds}ms (exit code {process.ExitCode})");
            }
            catch (OperationCanceledException)
            {
                Log.Warning($"obs-nvenc-test.exe did not exit within {stopwatch.ElapsedMilliseconds}ms, killing it");
                try { process.Kill(entireProcessTree: true); } catch { }
            }

            string output = await outputTask;
            if (string.IsNullOrWhiteSpace(output))
            {
                // Crashed before printing anything; don't cache so the next launch retries
                Log.Warning("obs-nvenc-test.exe produced no output (it may have crashed)");
                return null;
            }

            return ParseTestOutput(output, fingerprint);
        }

        /// <summary>
        /// Parses the INI-formatted stdout of obs-nvenc-test: a [general] section with
        /// nvenc_supported, then [h264]/[hevc]/[av1] sections with codec_supported, bframes etc.
        /// </summary>
        private static NvencCaps ParseTestOutput(string output, string fingerprint)
        {
            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            string currentSection = "general";

            foreach (string rawLine in output.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    currentSection = line[1..^1];
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                if (!sections.TryGetValue(currentSection, out var section))
                {
                    section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[currentSection] = section;
                }
                section[line[..eq]] = line[(eq + 1)..];
            }

            var caps = new NvencCaps
            {
                Fingerprint = fingerprint,
                ProbedAt = DateTime.UtcNow,
                NvencSupported = sections.TryGetValue("general", out var general) &&
                                 general.TryGetValue("nvenc_supported", out string? supported) &&
                                 supported.Equals("true", StringComparison.OrdinalIgnoreCase),
            };

            if (!caps.NvencSupported && general != null && general.TryGetValue("reason", out string? reason))
            {
                Log.Warning($"obs-nvenc-test reports NVENC unsupported: {reason}");
            }

            foreach (string codec in new[] { "h264", "hevc", "av1" })
            {
                var codecCaps = new CodecCaps();
                if (sections.TryGetValue(codec, out var section))
                {
                    codecCaps.Supported = section.TryGetValue("codec_supported", out string? cs) &&
                                          (cs == "1" || cs.Equals("true", StringComparison.OrdinalIgnoreCase));
                    if (section.TryGetValue("bframes", out string? bf) && int.TryParse(bf, out int bframes))
                        codecCaps.BFrames = bframes;
                }
                caps.Codecs[codec] = codecCaps;
            }

            return caps;
        }

        private static string Summarize(NvencCaps caps)
        {
            string codecSummary = string.Join(", ", caps.Codecs.Select(c =>
                $"{c.Key}: supported={c.Value.Supported}, bframes={c.Value.BFrames}"));
            return $"supported={caps.NvencSupported} ({codecSummary})";
        }
    }
}
