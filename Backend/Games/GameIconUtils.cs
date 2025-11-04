using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Serilog;

namespace Segra.Backend.Games
{
    public static class GameIconUtils
    {
        [DllImport("Shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        private static extern int ExtractIconEx(string sFile, int iIndex, out IntPtr piLargeVersion, out IntPtr piSmallVersion, int amountIcons);

        public static string? ExtractIconAsBase64(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
            {
                Log.Warning($"Cannot extract icon: File does not exist at path {executablePath}");
                return null;
            }

            try
            {
                IntPtr large;
                IntPtr small;

                int iconCount = ExtractIconEx(executablePath, 0, out large, out small, 1);

                // Only extract if there is an icon and it's large
                if (iconCount > 0 && large != IntPtr.Zero)
                {
                    using (Icon icon = Icon.FromHandle(large))
                    {
                        using (Bitmap bitmap = icon.ToBitmap())
                        {
                            using (MemoryStream stream = new MemoryStream())
                            {
                                bitmap.Save(stream, ImageFormat.Png);
                                byte[] imageBytes = stream.ToArray();
                                string base64String = Convert.ToBase64String(imageBytes);
                                return base64String;
                            }
                        }
                    }
                }
                else
                {
                    Log.Warning($"No icons found in executable: {executablePath}");
                }

                // Free the icon resources
                if (large != IntPtr.Zero)
                    DestroyIcon(large);
                if (small != IntPtr.Zero)
                    DestroyIcon(small);
            }
            catch (Exception ex)
            {
                Log.Error($"Error extracting icon from {executablePath}: {ex.Message}");
            }

            return null;
        }

        [DllImport("User32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
