namespace Segra.Hotkeys;

/// <summary>
/// Polls the global key state and reports the id of a binding once per press (rising edge).
///
/// Unrelated held keys never block a match, but when two bindings overlap (e.g. F8 and Ctrl+F8)
/// only the most specific one fires for a given press: within a single tick the binding with the
/// most keys wins.
/// </summary>
internal sealed class HotkeyPoller
{
    private sealed class Binding
    {
        public required int Id { get; init; }
        public required int[] Keys { get; init; }
        public bool WasDown { get; set; }
    }

    private readonly object _lock = new();
    private readonly Action<int> _onFired;
    private Binding[] _bindings = [];

    public HotkeyPoller(Action<int> onFired) => _onFired = onFired;

    public void SetBindings(IReadOnlyList<KeyBinding> bindings)
    {
        var next = bindings
            .Where(b => b.Keys.Length > 0)
            .Select(b => new Binding
            {
                Id = b.Id,
                Keys = b.Keys,
                // Seed from the current physical state so a key already held when the binding is
                // applied does not fire immediately.
                WasDown = AreAllDown(b.Keys),
            })
            .ToArray();

        lock (_lock)
            _bindings = next;
    }

    public void Tick()
    {
        Binding[] bindings;
        lock (_lock)
            bindings = _bindings;

        if (bindings.Length == 0)
            return;

        Binding? fired = null;
        foreach (var binding in bindings)
        {
            bool down = AreAllDown(binding.Keys);
            if (down && !binding.WasDown && (fired is null || binding.Keys.Length > fired.Keys.Length))
                fired = binding;

            binding.WasDown = down;
        }

        if (fired is not null)
            _onFired(fired.Id);
    }

    private static bool AreAllDown(int[] keys)
    {
        foreach (int key in keys)
        {
            if (!NativeMethods.IsKeyDown(key))
                return false;
        }
        return true;
    }
}
