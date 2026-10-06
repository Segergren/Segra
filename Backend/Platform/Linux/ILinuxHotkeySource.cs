using Segra.Backend.Core.Models;

namespace Segra.Backend.Platform.Linux
{
    /// <summary>A global hotkey source for Wayland sessions, where libobs never sees keys pressed in other apps.</summary>
    internal interface ILinuxHotkeySource : IDisposable
    {
        void SetBindings(IEnumerable<Hotkey> hotkeys);
    }
}
