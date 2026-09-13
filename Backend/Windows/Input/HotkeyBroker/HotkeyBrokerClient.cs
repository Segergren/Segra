using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Serilog;
using Segra.Backend.Core.Models;
using Segra.Hotkeys;

namespace Segra.Backend.Windows.Input.HotkeyBroker
{
    /// <summary>
    /// Talks to the privileged <c>Segra.Hotkeys</c> broker. When connected, the broker becomes the
    /// source of hotkey presses (it can read input over elevated games where the in-process OBS
    /// hotkeys cannot); otherwise the caller falls back to the OBS hotkey system.
    ///
    /// The client owns the broker's lifetime: it launches the installed exe on demand (the broker
    /// exits by itself once Segra disconnects) and runs two background threads, a connect loop and
    /// a read loop that raises <see cref="ActionFired"/>.
    /// </summary>
    internal sealed class HotkeyBrokerClient : IDisposable
    {
        private static readonly TimeSpan LaunchInterval = TimeSpan.FromSeconds(10);

        private readonly object _sync = new();
        private NamedPipeClientStream? _pipe;
        private BinaryReader? _reader;
        private BinaryWriter? _writer;
        private Thread? _connectThread;
        private Thread? _readThread;
        private volatile bool _active;
        private volatile bool _disposed;
        private DateTime _lastLaunchAttemptUtc = DateTime.MinValue;
        private DateTime? _rejectedInstallStamp;
        private bool _launchFailureLogged;

        /// <summary>True while a broker session is established.</summary>
        public bool IsActive => _active;

        /// <summary>True when the installed broker answered with the wrong protocol version.</summary>
        public bool HasRejectedInstall => IsRejectedInstall();

        /// <summary>Raised when the broker connection is established or lost, or an installed broker is rejected.</summary>
        public event Action? StateChanged;

        /// <summary>Raised on the broker's read thread for each hotkey the broker reports.</summary>
        public event Action<KeybindAction>? ActionFired;

        public void Start()
        {
            if (_connectThread is not null)
                return;

            _connectThread = new Thread(ConnectLoop)
            {
                IsBackground = true,
                Name = "Segra.HotkeyBroker.Connect",
            };
            _connectThread.Start();
        }

        /// <summary>Pushes the enabled keybindings to a connected broker. No-op when disconnected.</summary>
        public void UpdateKeybindings(IReadOnlyList<Keybind> keybindings)
        {
            lock (_sync)
            {
                if (_writer is null)
                    return;

                try
                {
                    HotkeyProtocol.WriteBindings(_writer, ToBindings(keybindings));
                }
                catch (Exception ex)
                {
                    // The read loop will observe the broken pipe and drop the session.
                    Log.Debug(ex, "Failed to send keybindings to hotkey broker");
                }
            }
        }

        private void ConnectLoop()
        {
            while (!_disposed)
            {
                if (!_active && HotkeyBrokerPaths.IsInstalled && !IsRejectedInstall())
                {
                    TryLaunchBroker();
                    if (TryConnect())
                    {
                        // StateChanged subscribers push the enabled keybindings for the new session.
                        SetActive(true);
                        _readThread = new Thread(ReadLoop)
                        {
                            IsBackground = true,
                            Name = "Segra.HotkeyBroker.Read",
                        };
                        _readThread.Start();
                    }
                }

                Thread.Sleep(2000);
            }
        }

        // An installed broker that speaks the wrong protocol is left alone until the file changes.
        private bool IsRejectedInstall() =>
            _rejectedInstallStamp is { } stamp && File.GetLastWriteTimeUtc(HotkeyBrokerPaths.InstalledBrokerPath) == stamp;

        private void TryLaunchBroker()
        {
            if (DateTime.UtcNow - _lastLaunchAttemptUtc < LaunchInterval)
                return;
            _lastLaunchAttemptUtc = DateTime.UtcNow;

            try
            {
                // ShellExecute so the shell's AppInfo path can grant the broker UIAccess. If a broker
                // is already running, the new instance exits again on its own.
                Process.Start(new ProcessStartInfo
                {
                    FileName = HotkeyBrokerPaths.InstalledBrokerPath,
                    UseShellExecute = true,
                })?.Dispose();
                _launchFailureLogged = false;
            }
            catch (Exception ex)
            {
                if (_launchFailureLogged)
                    return;
                _launchFailureLogged = true;
                Log.Warning(ex, "Failed to launch hotkey broker");
            }
        }

        private bool TryConnect()
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                // Overlapped handle: a synchronous handle serializes I/O, so a write would wait
                // behind the read loop's pending ReadFile and deadlock.
                pipe = new NamedPipeClientStream(".", HotkeyBrokerPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(300);

                var reader = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
                var writer = new BinaryWriter(pipe, Encoding.UTF8, leaveOpen: true);

                int version = reader.ReadByte();
                if (version != HotkeyProtocol.Version)
                {
                    pipe.Dispose();
                    _rejectedInstallStamp = File.GetLastWriteTimeUtc(HotkeyBrokerPaths.InstalledBrokerPath);
                    Log.Warning("Installed hotkey broker uses protocol {Actual}, expected {Expected}", version, HotkeyProtocol.Version);
                    StateChanged?.Invoke();
                    return false;
                }

                lock (_sync)
                {
                    _pipe = pipe;
                    _reader = reader;
                    _writer = writer;
                }
                return true;
            }
            catch
            {
                try { pipe?.Dispose(); } catch { /* ignore */ }
                return false;
            }
        }

        private void ReadLoop()
        {
            BinaryReader? reader;
            lock (_sync)
                reader = _reader;

            try
            {
                while (!_disposed && reader is not null)
                {
                    BrokerMessage message;
                    try
                    {
                        message = HotkeyProtocol.ReadMessage(reader);
                    }
                    catch
                    {
                        break;
                    }

                    if (message != BrokerMessage.Fired)
                        continue;

                    int id;
                    try
                    {
                        id = reader.ReadInt32();
                    }
                    catch
                    {
                        break;
                    }

                    // The broker echoes back the id we gave it (our KeybindAction value).
                    if (Enum.IsDefined(typeof(KeybindAction), id))
                        ActionFired?.Invoke((KeybindAction)id);
                }
            }
            finally
            {
                Disconnect();
            }
        }

        private void Disconnect()
        {
            lock (_sync)
            {
                try { _reader?.Dispose(); } catch { /* ignore */ }
                try { _writer?.Dispose(); } catch { /* ignore */ }
                try { _pipe?.Dispose(); } catch { /* ignore */ }
                _reader = null;
                _writer = null;
                _pipe = null;
            }

            SetActive(false);
        }

        private void SetActive(bool value)
        {
            if (_active == value)
                return;

            _active = value;
            StateChanged?.Invoke();
        }

        public void Dispose()
        {
            _disposed = true;
            Disconnect();

            try { _connectThread?.Join(500); } catch { /* ignore */ }
            try { _readThread?.Join(500); } catch { /* ignore */ }
        }

        private static List<KeyBinding> ToBindings(IReadOnlyList<Keybind> keybindings)
        {
            var bindings = new List<KeyBinding>(keybindings.Count);
            foreach (var keybind in keybindings)
            {
                if (keybind.Keys is null || keybind.Keys.Count == 0)
                    continue;

                // The id is simply Segra's own action value; the broker stores and echoes it back.
                bindings.Add(new KeyBinding((int)keybind.Action, keybind.Keys.ToArray()));
            }
            return bindings;
        }
    }
}
