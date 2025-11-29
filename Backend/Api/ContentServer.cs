using Serilog;
using System.Net;
using System.Web;
using Segra.Backend.Media;

namespace Segra.Backend.Api
{
    internal class ContentServer
    {
        private static readonly HttpListener _httpListener = new();
        private static CancellationTokenSource? _cancellationTokenSource;

        public static void StartServer(string prefix)
        {
            _httpListener.Prefixes.Add(prefix);
            _httpListener.Start();
            Log.Information("Server started at {Prefix}", prefix);
            
            _cancellationTokenSource = new CancellationTokenSource();
            _ = Task.Run(() => AcceptRequestsAsync(_cancellationTokenSource.Token));
        }

        private static async Task AcceptRequestsAsync(CancellationToken cancellationToken)
        {
            Log.Information("ContentServer now accepting requests");
            
            while (!cancellationToken.IsCancellationRequested && _httpListener.IsListening)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();
                    _ = ProcessRequestAsync(context);
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995)
                {
                    Log.Information("ContentServer listener stopped");
                    break;
                }
                catch (ObjectDisposedException)
                {
                    Log.Information("ContentServer listener disposed");
                    break;
                }

                catch (Exception ex)
                {
                    Log.Error(ex, "Error accepting request");
                }
            }
            
            Log.Information("ContentServer stopped accepting requests");
        }

        private static async Task ProcessRequestAsync(HttpListenerContext context)
        {
            var response = context.Response;
            
            try
            {
                var rawUrl = context.Request.RawUrl ?? "";

                if (rawUrl.StartsWith("/api/thumbnail"))
                {
                    await HandleThumbnailRequest(context);
                }
                else if (rawUrl.StartsWith("/api/content"))
                {
                    await HandleContentRequest(context);
                }
                else
                {
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    response.ContentType = "text/plain";
                    using (var writer = new StreamWriter(response.OutputStream))
                    {
                        await writer.WriteAsync("Invalid endpoint.");
                    }
                }
            }
            catch (HttpListenerException)
            {
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing request for {Url}", context.Request.RawUrl);
                try
                {
                    if (!response.OutputStream.CanWrite)
                        return;
                        
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    response.ContentType = "text/plain";
                    using (var writer = new StreamWriter(response.OutputStream))
                    {
                        await writer.WriteAsync("Internal server error");
                    }
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

        private static async Task HandleThumbnailRequest(HttpListenerContext context)
        {
            var query = HttpUtility.ParseQueryString(context.Request?.Url?.Query ?? "");
            string input = query["input"] ?? "";
            string timeParam = query["time"] ?? "";
            var response = context.Response;

            response.AddHeader("Access-Control-Allow-Origin", "*");

            if (!File.Exists(input))
            {
                Log.Warning("Thumbnail request file not found: {Input}", input);
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.ContentType = "text/plain";
                using (var writer = new StreamWriter(response.OutputStream))
                {
                    await writer.WriteAsync("File not found.");
                }
                return;
            }

            if (string.IsNullOrEmpty(timeParam))
            {
                response.ContentType = "image/jpeg";
                response.AddHeader("Cache-Control", "public, max-age=86400");
                response.AddHeader("Expires", DateTime.UtcNow.AddDays(7).ToString("R"));
                
                try
                {
                    var lastModified = File.GetLastWriteTimeUtc(input);
                    response.AddHeader("Last-Modified", lastModified.ToString("R"));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Could not get last modified time for {Input}", input);
                }

                using (var fs = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true))
                {
                    response.ContentLength64 = fs.Length;
                    await fs.CopyToAsync(response.OutputStream);
                }
            }
            else
            {
                if (!double.TryParse(timeParam, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out double timeSeconds))
                {
                    Log.Warning("Could not parse timeParam={TimeParam}, using 0.0", timeParam);
                    timeSeconds = 0.0;
                }

                if (!FFmpegService.FFmpegExists())
                {
                    Log.Error("FFmpeg executable not found");
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    response.ContentType = "text/plain";
                    using (var writer = new StreamWriter(response.OutputStream))
                    {
                        await writer.WriteAsync("FFmpeg not found on server.");
                    }
                    return;
                }

                byte[] jpegBytes = await FFmpegService.GenerateThumbnail(input, timeSeconds);

                if (jpegBytes != null && jpegBytes.Length > 0)
                {
                    response.ContentType = "image/jpeg";
                    response.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate");
                    response.AddHeader("Pragma", "no-cache");
                    response.AddHeader("Expires", "0");
                    response.ContentLength64 = jpegBytes.Length;
                    await response.OutputStream.WriteAsync(jpegBytes, 0, jpegBytes.Length);
                }
                else
                {
                    Log.Error("No thumbnail data received from FFmpeg");
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    response.ContentType = "text/plain";
                    using (var writer = new StreamWriter(response.OutputStream))
                    {
                        await writer.WriteAsync("Failed to generate thumbnail.");
                    }
                }
            }
        }

        private static async Task HandleContentRequest(HttpListenerContext context)
        {
            var query = HttpUtility.ParseQueryString(context.Request?.Url?.Query ?? "");
            string fileName = query["input"] ?? "";
            var response = context.Response;

            response.AddHeader("Access-Control-Allow-Origin", "*");

            if (!File.Exists(fileName))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.ContentType = "text/plain";
                using (var writer = new StreamWriter(response.OutputStream))
                {
                    await writer.WriteAsync("File not found.");
                }
                return;
            }

            if (fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                await StreamVideoFile(fileName, context);
            }
            else if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                await StreamJsonFile(fileName, response);
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                response.ContentType = "text/plain";
                using (var writer = new StreamWriter(response.OutputStream))
                {
                    await writer.WriteAsync("Unsupported file type.");
                }
            }
        }

        private static async Task StreamVideoFile(string fileName, HttpListenerContext context)
        {
            var response = context.Response;
            
            string rangeHeader = context.Request.Headers["Range"] ?? "";
            long start = 0;
            long end;
            
            using (var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 262144, 
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                long fileLength = fs.Length;
                end = fileLength - 1;

                if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                {
                    string[] rangeParts = rangeHeader.Substring(6).Split('-');
                    if (rangeParts.Length > 0 && !string.IsNullOrEmpty(rangeParts[0]))
                    {
                        long.TryParse(rangeParts[0], out start);
                    }
                    if (rangeParts.Length > 1 && !string.IsNullOrEmpty(rangeParts[1]))
                    {
                        long.TryParse(rangeParts[1], out end);
                    }
                }

                if (start > end || start < 0 || end >= fileLength)
                {
                    response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                    response.AddHeader("Content-Range", $"bytes */{fileLength}");
                    return;
                }

                long contentLength = end - start + 1;

                response.StatusCode = string.IsNullOrEmpty(rangeHeader) ? (int)HttpStatusCode.OK : (int)HttpStatusCode.PartialContent;
                response.ContentType = "video/mp4";
                response.AddHeader("Accept-Ranges", "bytes");
                
                if (!string.IsNullOrEmpty(rangeHeader))
                {
                    response.AddHeader("Content-Range", $"bytes {start}-{end}/{fileLength}");
                }
                
                response.ContentLength64 = contentLength;

                if (start > 0)
                {
                    fs.Seek(start, SeekOrigin.Begin);
                }

                byte[] buffer = new byte[262144];
                long bytesRemaining = contentLength;

                while (bytesRemaining > 0)
                {
                    int bytesToRead = (int)Math.Min(buffer.Length, bytesRemaining);
                    int bytesRead = await fs.ReadAsync(buffer, 0, bytesToRead);
                    
                    if (bytesRead == 0)
                        break;

                    await response.OutputStream.WriteAsync(buffer, 0, bytesRead);
                    bytesRemaining -= bytesRead;
                }
            }
        }

        private static async Task StreamJsonFile(string fileName, HttpListenerResponse response)
        {
            var fileInfo = new FileInfo(fileName);
            
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "application/json";
            response.AddHeader("Accept-Ranges", "bytes");
            response.ContentLength64 = fileInfo.Length;

            using (var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true))
            {
                await fs.CopyToAsync(response.OutputStream);
            }
        }

        public static void StopServer()
        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _httpListener.Stop();
                _httpListener.Close();
                Log.Information("ContentServer stopped");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stopping ContentServer");
            }
            finally
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }
    }
}
