using System.Runtime.InteropServices;
using Serilog;

namespace Segra.Backend.Windows.Input
{
    /// <summary>
    /// Clears libobs' strict_modifiers flag so a bare-key hotkey still fires while Shift, Ctrl,
    /// Alt or Win is held. obs_hotkey_enable_strict_modifiers() was removed in OBS 31.0, but the
    /// flag sits between two fields whose exported setters still encode their own offsets, so the
    /// address is recovered from their code rather than hardcoded.
    /// </summary>
    internal static class ObsStrictModifiers
    {
        public static bool Disable()
        {
            if (!OperatingSystem.IsWindows())
                return false;

            try
            {
                if (!NativeLibrary.TryLoad("obs.dll", out var lib))
                {
                    Log.Warning("Could not open obs.dll; leaving strict modifiers enabled.");
                    return false;
                }

                if (!TryReadSetter(lib, "obs_hotkey_enable_background_press", out nint global, out int pressOffset) ||
                    !TryReadSetter(lib, "obs_hotkey_enable_callback_rerouting", out nint rerouteGlobal, out int rerouteOffset))
                {
                    Log.Warning("Could not resolve the libobs hotkey flags; leaving strict modifiers enabled.");
                    return false;
                }

                if (global != rerouteGlobal || rerouteOffset - pressOffset != 2)
                {
                    Log.Warning("Unexpected libobs hotkey field layout; leaving strict modifiers enabled.");
                    return false;
                }

                nint obs = Marshal.ReadIntPtr(global);
                if (obs == 0)
                {
                    Log.Warning("libobs is not initialized yet; leaving strict modifiers enabled.");
                    return false;
                }

                int strictOffset = pressOffset + 1;
                byte press = Marshal.ReadByte(obs, pressOffset);
                byte strict = Marshal.ReadByte(obs, strictOffset);
                byte reroute = Marshal.ReadByte(obs, rerouteOffset);

                // Defaults are thread_disable_press=0, strict_modifiers=1, reroute_hotkeys=0.
                if (press != 0 || reroute != 0 || strict > 1)
                {
                    Log.Warning($"Unexpected libobs hotkey flag values ({press}, {strict}, {reroute}); leaving strict modifiers enabled.");
                    return false;
                }

                if (strict == 0)
                    return true;

                Marshal.WriteByte(obs, strictOffset, 0);
                Log.Information("Disabled libobs strict modifier matching for hotkeys.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to disable libobs strict modifiers");
                return false;
            }
        }

        /// <summary>
        /// Reads the address of libobs' internal obs pointer and the field offset written by an
        /// exported bool setter, from that setter's own instructions.
        /// </summary>
        private static bool TryReadSetter(nint lib, string export, out nint globalAddress, out int fieldOffset)
        {
            globalAddress = 0;
            fieldOffset = 0;

            if (!NativeLibrary.TryGetExport(lib, export, out nint function))
                return false;

            byte[] code = new byte[64];
            Marshal.Copy(function, code, 0, code.Length);

            for (int i = 0; i + 7 <= code.Length; i++)
            {
                // mov <reg64>, [rip+rel32]
                if (globalAddress == 0 && code[i] == 0x48 && code[i + 1] == 0x8B && (code[i + 2] & 0xC7) == 0x05)
                    globalAddress = function + i + 7 + BitConverter.ToInt32(code, i + 3);

                // mov byte ptr [rcx+disp32], <reg8>
                if (fieldOffset == 0 && code[i] == 0x88 && (code[i + 1] & 0xC7) == 0x81)
                    fieldOffset = BitConverter.ToInt32(code, i + 2);
            }

            return globalAddress != 0 && fieldOffset > 0;
        }
    }
}
