using System.Text;

namespace Segra.Hotkeys;

/// <summary>
/// Minimal append-only log so UIAccess/launch problems on end-user machines are diagnosable even
/// though the broker has no console. Kept deliberately small and self-contained.
/// </summary>
internal static class BrokerLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Segra",
        "hotkey-broker.log");

    public static void Info(string message) => Write("INF", message);
    public static void Error(Exception ex, string message) => Write("ERR", $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                TrimIfNeeded();
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the broker down.
        }
    }

    private static void TrimIfNeeded()
    {
        const long maxBytes = 1 * 1024 * 1024;
        var info = new FileInfo(LogPath);
        if (!info.Exists || info.Length < maxBytes)
            return;

        var lines = File.ReadAllLines(LogPath);
        File.WriteAllLines(LogPath, lines.Skip(lines.Length / 2), Encoding.UTF8);
    }
}
