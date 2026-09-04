using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Serilog;
using Segra.Backend.Platform;

namespace Segra.Backend.App
{
    /// <summary>
    /// Localhost WebSocket endpoint (ws://localhost:44031/) that receives the in-app clip audio
    /// from the webview as raw PCM and plays it through the Segra process itself (via
    /// <see cref="PlatformServices.StreamAudio"/>), so the WASAPI audio session belongs to
    /// Segra.exe instead of msedgewebview2.exe. Discord/OBS application-audio capture of the
    /// Segra window then includes the sound.
    ///
    /// Protocol: text frames carry control JSON, binary frames carry raw interleaved IEEE float32
    /// PCM for the stream started by the last "play" control message.
    /// </summary>
    public static class AudioStreamServer
    {
        private const string Prefix = "http://localhost:44031/";

        private static WebSocket? activeWebSocket;

        public static async Task StartAsync()
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add(Prefix);
            try
            {
                listener.Start();
                Log.Information("Audio stream server started at ws://localhost:44031/");
            }
            catch (Exception ex)
            {
                // The capture feed in the frontend falls back to webview-rendered audio when
                // this endpoint is unreachable, so a bind failure is non-fatal.
                Log.Warning($"Audio stream server failed to start on {Prefix}: {ex.Message}");
                return;
            }

            try
            {
                while (true)
                {
                    HttpListenerContext context = await listener.GetContextAsync();

                    if (context.Request.IsWebSocketRequest)
                    {
                        if (activeWebSocket != null && activeWebSocket.State == WebSocketState.Open)
                        {
                            await activeWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "New connection", CancellationToken.None);
                        }

                        // Drop whatever the replaced connection had buffered before the new
                        // stream starts; its own handler's cleanup flush is suppressed below.
                        PlatformServices.StreamAudio.Flush();

                        HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
                        activeWebSocket = wsContext.WebSocket;
                        _ = Task.Run(() => HandleWebSocketAsync(activeWebSocket));
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Exception in AudioStreamServer: {ex.Message}");
            }
        }

        private static async Task HandleWebSocketAsync(WebSocket webSocket)
        {
            byte[] buffer = new byte[8192];
            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        CancellationToken.None);

                    // PCM blocks (16 KB) exceed the receive buffer, so frames arrive fragmented;
                    // assemble the full message before interpreting it.
                    using var stream = new MemoryStream();
                    while (true)
                    {
                        stream.Write(buffer, 0, result.Count);
                        if (result.EndOfMessage) break;
                        result = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client initiated closure", CancellationToken.None);
                    }
                    else if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string message = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
                        HandleControlMessage(message);
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        PlatformServices.StreamAudio.Write(stream.ToArray());
                    }
                }
            }
            catch (WebSocketException wsEx)
            {
                Log.Information($"WebSocketException in AudioStreamServer: {wsEx.Message}");
            }
            catch (Exception ex)
            {
                Log.Information($"General exception in AudioStreamServer: {ex.Message}");
            }
            finally
            {
                // Only flush if this socket is still the active one. A replaced connection's
                // handler shuts down after the new connection is accepted, and flushing then
                // would discard the new stream's buffered audio.
                if (ReferenceEquals(activeWebSocket, webSocket))
                {
                    PlatformServices.StreamAudio.Flush();
                }
                if (webSocket.State != WebSocketState.Closed && webSocket.State != WebSocketState.Aborted)
                {
                    try
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Server-side error", CancellationToken.None);
                    }
                    catch
                    {
                        // ignore
                    }
                }
                Log.Information("Audio stream connection closed.");
            }
        }

        private static void HandleControlMessage(string message)
        {
            try
            {
                using var doc = JsonDocument.Parse(message);
                JsonElement root = doc.RootElement;
                string? type = root.TryGetProperty("type", out JsonElement typeEl) ? typeEl.GetString() : null;

                switch (type)
                {
                    case "play":
                        int sampleRate = root.TryGetProperty("sampleRate", out JsonElement rateEl) && rateEl.TryGetInt32(out int rate)
                            ? rate
                            : 48000;
                        int channels = root.TryGetProperty("channels", out JsonElement chEl) && chEl.TryGetInt32(out int chs)
                            ? chs
                            : 2;
                        PlatformServices.StreamAudio.Start(sampleRate, channels);
                        break;
                    case "flush":
                        PlatformServices.StreamAudio.Flush();
                        break;
                    default:
                        Log.Information($"Unknown audio stream control: {message}");
                        break;
                }
            }
            catch (JsonException ex)
            {
                Log.Error($"Failed to parse audio stream control: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log.Error($"Unhandled exception in audio stream control: {ex.Message}");
            }
        }
    }
}
