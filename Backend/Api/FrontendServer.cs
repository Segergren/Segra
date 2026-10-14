using Serilog;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.FileProviders;

namespace Segra.Backend.Api
{
    // Serves the embedded frontend build (Resources/wwwroot) to the webview.
    internal static class FrontendServer
    {
        private const int StartPort = 44040;
        private const int PortCount = 100;

        private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html",
            [".js"] = "text/javascript",
            [".css"] = "text/css",
            [".json"] = "application/json",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".ico"] = "image/x-icon",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
        };

        private static readonly IFileProvider Files = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "Resources/wwwroot");

        // Listens on the first free port from StartPort and returns the base url
        public static string Start()
        {
            for (int port = StartPort; port < StartPort + PortCount; port++)
            {
                if (!IsPortFree(port))
                    continue;

                var listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                try
                {
                    listener.Start();
                }
                catch (HttpListenerException)
                {
                    continue;
                }

                _ = Task.Run(() => AcceptRequestsAsync(listener));
                return $"http://localhost:{port}";
            }

            throw new IOException($"No free port for the frontend server in {StartPort}-{StartPort + PortCount - 1}");
        }

        private static bool IsPortFree(int port)
        {
            try
            {
                using var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private static async Task AcceptRequestsAsync(HttpListener listener)
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    _ = ServeAsync(context);
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Frontend server failed to accept a request");
                }
            }
        }

        private static async Task ServeAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                string path = Uri.UnescapeDataString(request.Url?.AbsolutePath ?? "/");
                if (path.EndsWith('/'))
                    path += "index.html";

                var file = Files.GetFileInfo(path);
                if (request.HttpMethod != "GET" || !file.Exists || file.IsDirectory ||
                    !ContentTypes.TryGetValue(Path.GetExtension(path), out var contentType))
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    return;
                }

                // Lets the webview revalidate its cached copy with a 304 instead of downloading it again
                string etag = $"\"{file.LastModified.UtcTicks:x}-{file.Length:x}\"";
                response.Headers[HttpResponseHeader.ETag] = etag;
                response.Headers[HttpResponseHeader.LastModified] = file.LastModified.ToString("R");
                if (request.Headers["If-None-Match"] == etag)
                {
                    response.StatusCode = (int)HttpStatusCode.NotModified;
                    return;
                }

                response.ContentType = contentType;
                response.ContentLength64 = file.Length;
                await using var stream = file.CreateReadStream();
                await stream.CopyToAsync(response.OutputStream);
            }
            catch (HttpListenerException)
            {
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error serving {Path}", request.Url?.AbsolutePath);
                try
                {
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                }
                catch
                {
                }
            }
            finally
            {
                try
                {
                    response.Close();
                }
                catch
                {
                }
            }
        }
    }
}
