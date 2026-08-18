using Segra.Backend.Platform;

namespace Segra.Backend.App;

internal enum NotifyIconState
{
    Idle,
    Recording
}

/// <summary>
/// Routes tray icon state changes from the recorder to the platform tray icon
/// without coupling OBSService to a concrete implementation.
/// </summary>
internal static class NotifyIconService
{
    private static ITrayIcon? _trayIcon;
    private static readonly Lock Lock = new();

    public static void Initialize(ITrayIcon trayIcon)
    {
        lock (Lock)
        {
            _trayIcon = trayIcon;
        }
    }

    public static void SetNotifyIconStatus(NotifyIconState state)
    {
        lock (Lock)
        {
            if (_trayIcon == null) return;

            _trayIcon.SetRecording(state == NotifyIconState.Recording);
        }
    }
}
