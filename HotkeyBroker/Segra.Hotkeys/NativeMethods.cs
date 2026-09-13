using System.Runtime.InteropServices;

namespace Segra.Hotkeys;

internal static class NativeMethods
{
    private const int KeyDownMask = 0x8000;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// True while the given Win32 virtual key is physically down. With UIAccess this reports input
    /// originating from windows at any integrity level, including elevated games.
    /// </summary>
    public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & KeyDownMask) != 0;

    /// <summary>Resolves the executable path of the process on the other end of a connected pipe.</summary>
    public static string? GetPipeClientImagePath(SafeHandle pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe, out uint pid))
            return null;

        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
