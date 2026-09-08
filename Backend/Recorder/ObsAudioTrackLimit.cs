using System.Runtime.InteropServices;
using ObsKit.NET.Encoders;
using ObsKit.NET.Outputs;
using Serilog;

namespace Segra.Backend.Recorder
{
    /// <summary>
    /// How many audio tracks the loaded OBS build accepts. Stock OBS caps at 6
    /// (MAX_OUTPUT_AUDIO_ENCODERS); Segra's rebuilt bundles raise it. Probed instead of
    /// hardcoded so one binary works against every bundled OBS version.
    /// </summary>
    internal static partial class ObsAudioTrackLimit
    {
        // Mixer masks are a uint32, so no build can exceed 32 tracks.
        private const int ProbeCeiling = 32;
        private const int Fallback = 6;

        private static int _cached;

        public static int Value => _cached != 0 ? _cached : _cached = Probe();

        private static int Probe()
        {
            try
            {
                using var encoder = AudioEncoder.CreateAac("segra_track_probe", 128, 0);
                using var output = new RecordingOutput("segra_track_probe");
                try
                {
                    output.SetFormat(RecordingFormat.HybridMp4);
                }
                catch (NotSupportedException)
                {
                    // Older bundles have no mp4_output; ffmpeg_muxer has the same limit.
                }

                // Set through libobs directly: ObsKit's SetAudioEncoder tracks one encoder
                // per slot, and the probe deliberately reuses a single encoder for all of them.
                int count = 0;
                for (int i = 0; i < ProbeCeiling; i++)
                {
                    obs_output_set_audio_encoder(output.NativeHandle, encoder.NativeHandle, (nuint)i);
                    if (obs_output_get_audio_encoder(output.NativeHandle, (nuint)i) == 0)
                        break;
                    count = i + 1;
                }

                if (count < Fallback)
                {
                    Log.Warning($"Audio track probe returned {count}; using {Fallback}.");
                    return Fallback;
                }

                Log.Information($"OBS accepts {count} audio tracks.");
                return count;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"Audio track probe failed; using {Fallback}");
                return Fallback;
            }
        }

        [LibraryImport("obs", EntryPoint = "obs_output_set_audio_encoder")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static partial void obs_output_set_audio_encoder(nint output, nint encoder, nuint idx);

        [LibraryImport("obs", EntryPoint = "obs_output_get_audio_encoder")]
        [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
        private static partial nint obs_output_get_audio_encoder(nint output, nuint idx);
    }
}
