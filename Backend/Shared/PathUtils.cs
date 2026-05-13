namespace Segra.Backend.Shared
{
    public static class PathUtils
    {
        // Paths cross JSON boundaries (metadata, settings, WebSocket) and end up in logs,
        // so they're standardized on forward slashes. The few consumers that require
        // backslashes (e.g. explorer.exe /select) convert at the call site.
        public static string Normalize(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }

        public static string? NormalizeOrNull(string? path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }

        public static string Combine(string path1, string path2)
        {
            return Normalize(Path.Combine(path1, path2));
        }

        public static string Combine(string path1, string path2, string path3)
        {
            return Normalize(Path.Combine(path1, path2, path3));
        }

        public static string Combine(string path1, string path2, string path3, string path4)
        {
            return Normalize(Path.Combine(path1, path2, path3, path4));
        }
    }
}
