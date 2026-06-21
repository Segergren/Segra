using System.Drawing;
using System.Drawing.Imaging;
using Serilog;

namespace Segra.Backend.Shared
{
    public static class IconUtils
    {
        /// <summary>
        /// Extracts the executable's icon as a base64-encoded PNG, or null if it cannot be read.
        /// Used to give custom (non-catalog) games an icon in the UI.
        /// </summary>
        public static string? ExtractExeIconBase64(string exePath)
        {
            try
            {
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    return null;

                using Icon? icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon == null)
                    return null;

                using Bitmap bitmap = icon.ToBitmap();
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                return Convert.ToBase64String(stream.ToArray());
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to extract icon from '{exePath}': {ex.Message}");
                return null;
            }
        }
    }
}
