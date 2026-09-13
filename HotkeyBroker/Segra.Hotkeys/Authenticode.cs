using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Segra.Hotkeys
{
    /// <summary>
    /// Authenticode checks shared by Segra (before copying the broker into Program Files) and the
    /// broker (before accepting a pipe client). Compiled into both binaries like the protocol.
    /// </summary>
    public static class Authenticode
    {
        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionIgnore = 0;
        private const uint WtdRevocationCheckNone = 0x10;
        private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
        private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        /// <summary>
        /// True when <paramref name="path"/> carries a valid signature that chains to a trusted
        /// root and was made with the same certificate as <paramref name="referencePath"/>.
        /// </summary>
        public static bool IsSignedLike(string path, string referencePath)
        {
            string? expected = GetSignerThumbprint(referencePath);
            return expected is not null && HasValidSignature(path) && GetSignerThumbprint(path) == expected;
        }

        /// <summary>Thumbprint of the signing certificate, or null when the file is unsigned.</summary>
        public static string? GetSignerThumbprint(string path)
        {
            try
            {
                // X509CertificateLoader has no Authenticode reader, so the obsolete API stays.
#pragma warning disable SYSLIB0057
                using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                return certificate.GetCertHashString();
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        public static bool HasValidSignature(string path)
        {
            IntPtr pathPtr = Marshal.StringToHGlobalUni(path);
            IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            try
            {
                var fileInfo = new WintrustFileInfo
                {
                    cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                    pcwszFilePath = pathPtr,
                };
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

                var data = new WintrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeNone,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = fileInfoPtr,
                    dwStateAction = WtdStateActionIgnore,
                    dwProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
                };

                Guid action = WintrustActionGenericVerifyV2;
                return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(fileInfoPtr);
                Marshal.FreeHGlobal(pathPtr);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WintrustData pWVTData);
    }
}
