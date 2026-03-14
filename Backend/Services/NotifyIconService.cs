namespace Segra.Backend.Services;

internal enum NotifyIconState
{
    Idle,
    Recording
}

internal static class NotifyIconService
{
    private static NotifyIcon? _trayIcon;
    private static readonly Lock Lock = new();

    public static void Initialize(NotifyIcon trayIcon)
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

            _trayIcon.Icon = state switch
            {
                NotifyIconState.Idle => Properties.Resources.icon,
                NotifyIconState.Recording => Properties.Resources.iconRecording,
                _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
            };
        }
    }
}
