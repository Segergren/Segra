using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Segra.Hotkeys;

/// <summary>
/// Single-client named pipe server. Segra connects, pushes the current keybindings, and receives
/// an action message whenever a bound hotkey fires. Polling only runs while a client is connected,
/// and the process exits once no client has shown up for a while, so nothing lingers after Segra
/// closes.
/// </summary>
internal sealed class BrokerServer
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);
    private const int MaxConsecutiveFailures = 10;

    private readonly string _pipeName;
    private readonly string _ownPath = Environment.ProcessPath!;
    private readonly string? _ownSignerThumbprint;

    public BrokerServer(string pipeName)
    {
        _pipeName = pipeName;
        _ownSignerThumbprint = Authenticode.GetSignerThumbprint(_ownPath);
    }

    public void Run()
    {
        int failures = 0;
        while (failures < MaxConsecutiveFailures)
        {
            try
            {
                using var server = CreatePipe();
                BrokerLog.Info($"Waiting for client on {_pipeName}");
                if (!WaitForClient(server))
                {
                    BrokerLog.Info("No client connected within the idle window");
                    return;
                }

                failures = 0;
                if (!IsTrustedClient(server))
                    continue;

                BrokerLog.Info("Client connected");
                bool shutdownRequested = HandleClient(server);
                BrokerLog.Info("Client disconnected");

                if (shutdownRequested)
                {
                    BrokerLog.Info("Broker exiting on client request");
                    return;
                }
            }
            catch (Exception ex)
            {
                failures++;
                BrokerLog.Error(ex, "Broker loop error");
                Thread.Sleep(1000);
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        // Restrict the pipe to the current user (and SYSTEM) so other accounts on the machine
        // cannot talk to this session's broker.
        var user = WindowsIdentity.GetCurrent().User;
        if (user is not null)
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private static bool WaitForClient(NamedPipeServerStream server)
    {
        using var timeout = new CancellationTokenSource(IdleTimeout);
        try
        {
            server.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Only a Segra signed with the broker's own certificate may drive the broker; anything else
    /// could use it as a key-state oracle over elevated windows. An unsigned (development) broker
    /// accepts any client.
    /// </summary>
    private bool IsTrustedClient(NamedPipeServerStream server)
    {
        string? clientPath = NativeMethods.GetPipeClientImagePath(server.SafePipeHandle);
        if (clientPath is null)
        {
            BrokerLog.Info("Rejected client: could not resolve its executable");
            return false;
        }

        if (_ownSignerThumbprint is null)
        {
            BrokerLog.Info($"Broker is unsigned; accepting {clientPath} without verification");
            return true;
        }

        if (!Authenticode.IsSignedLike(clientPath, _ownPath))
        {
            BrokerLog.Info($"Rejected client {clientPath}: signature check failed");
            return false;
        }

        return true;
    }

    private static bool HandleClient(NamedPipeServerStream server)
    {
        using var reader = new BinaryReader(server, Encoding.UTF8, leaveOpen: true);
        using var writer = new BinaryWriter(server, Encoding.UTF8, leaveOpen: true);

        var writeLock = new object();
        var stop = new ManualResetEventSlim(false);
        HotkeyPoller? poller = null;
        Thread? pollThread = null;

        try
        {
            Write(() => writer.Write(HotkeyProtocol.Version), writeLock);

            while (true)
            {
                List<KeyBinding> bindings;
                try
                {
                    var message = HotkeyProtocol.ReadMessage(reader);
                    if (message == BrokerMessage.Shutdown)
                    {
                        BrokerLog.Info("Shutdown requested by client");
                        return true;
                    }
                    if (message != BrokerMessage.SetBindings)
                        continue;
                    bindings = HotkeyProtocol.ReadBindings(reader);
                }
                catch (EndOfStreamException) { break; }
                catch (IOException) { break; }
                catch (InvalidDataException ex)
                {
                    BrokerLog.Error(ex, "Dropping client after malformed message");
                    break;
                }

                if (poller is null)
                {
                    var activePoller = new HotkeyPoller(id =>
                        Write(() => HotkeyProtocol.WriteFired(writer, id), writeLock));
                    poller = activePoller;

                    pollThread = new Thread(() =>
                    {
                        while (!stop.IsSet)
                        {
                            activePoller.Tick();
                            stop.Wait(25);
                        }
                    })
                    {
                        IsBackground = true,
                        Name = "Segra.Hotkeys.Poll",
                    };
                    pollThread.Start();
                }

                poller.SetBindings(bindings);
                Write(() => HotkeyProtocol.WriteMessage(writer, BrokerMessage.BindingsAck), writeLock);
            }
        }
        finally
        {
            stop.Set();
            pollThread?.Join(1000);
            stop.Dispose();
        }

        return false;
    }

    private static void Write(Action write, object writeLock)
    {
        lock (writeLock)
        {
            try
            {
                write();
            }
            catch (IOException)
            {
                // The client may vanish mid-write; the read loop notices and ends the session.
            }
        }
    }
}
