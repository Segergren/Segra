using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.IO;

namespace Segra.Backend.Shared
{
    public static class RecycleBinUtils
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pFrom;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)]
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpszProgressTitle;
        }

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_SILENT = 0x0004;

        /// <summary>
        /// Sends a file to the Windows recycle bin.
        /// Throws an exception if the recycle bin operation fails.
        /// </summary>
        /// <param name="filePath">The full path to the file to recycle</param>
        /// <exception cref="ArgumentException">Thrown if filePath is invalid</exception>
        /// <exception cref="FileNotFoundException">Thrown if the file does not exist</exception>
        /// <exception cref="IOException">Thrown if the file is locked or I/O error occurs</exception>
        public static void SendToRecycleBin(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
            }

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File does not exist: {filePath}", filePath);
            }

            // Normalize the file path - SHFileOperation requires double null-terminated string
            string normalizedPath = Path.GetFullPath(filePath);
            string doubleNullTerminatedPath = normalizedPath + "\0\0";

            SHFILEOPSTRUCT fileOp = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = doubleNullTerminatedPath,
                pTo = null,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT,
                fAnyOperationsAborted = false,
                hNameMappings = IntPtr.Zero,
                lpszProgressTitle = null
            };

            int result = SHFileOperation(ref fileOp);

            if (result == 0 && !fileOp.fAnyOperationsAborted)
            {
                return;
            }

            throw new IOException($"Failed to send file to recycle bin (result: {result}, aborted: {fileOp.fAnyOperationsAborted}): {filePath}");
        }
    }
}

