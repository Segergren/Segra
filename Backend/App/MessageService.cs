using System.Net.WebSockets;
using System.Net;
using System.Text;
using System.Text.Json;
using Serilog;
using System.Diagnostics;
using Segra.Backend.Services;
using Segra.Backend.Core.Models;
using Segra.Backend.Media;
using Segra.Backend.Utils;
using Segra.Backend.Obs;

namespace Segra.Backend.App
{
    public class Selection
    {
        public long Id { get; set; }
        // TODO (os): make this of type ContentType
        public required string Type { get; set; }
        public double StartTime { get; set; }
        public double EndTime { get; set; }
        public required string FileName { get; set; }
        public required string Game { get; set; }
        public string Title { get; set; } = string.Empty;
    }

    public static class MessageService
    {
        private static WebSocket? activeWebSocket;
        private static readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);
        private static readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static async Task HandleMessage(string message)
        {
            Log.Information("Websocket message received: " + GeneralUtils.RedactSensitiveInfo(message));
            if (string.IsNullOrEmpty(message))
            {
                Log.Information("Received empty message.");
                return;
            }

            try
            {
                var jsonDoc = JsonDocument.Parse(message);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("Method", out JsonElement methodElement))
                {
                    string? method = methodElement.GetString();

                    if (method == null)
                    {
                        Log.Warning("Received message with null method.");
                        return;
                    }

                    switch (method)
                    {
                        case "ToggleFullscreen":
                            if (root.TryGetProperty("Parameters", out var fsParams) &&
                                fsParams.TryGetProperty("enabled", out var enabledEl))
                            {
                                bool enabled = enabledEl.GetBoolean();
                                try
                                {
                                    Program.SetFullscreen(enabled);
                                }
                                catch (Exception ex)
                                {
                                    Log.Error(ex, "Failed to toggle fullscreen");
                                }
                            }
                            break;
                        case "Login":
                            root.TryGetProperty("Parameters", out JsonElement loginParameterElement);
                            string accessToken = loginParameterElement.GetProperty("accessToken").GetString()!;
                            string refreshToken = loginParameterElement.GetProperty("refreshToken").GetString()!;
                            await AuthService.Login(accessToken, refreshToken);
                            break;
                        case "Logout":
                            await AuthService.Logout();
                            break;
                        case "CancelClip":
                            if (root.TryGetProperty("Parameters", out var cancelClipParams) &&
                                cancelClipParams.TryGetProperty("id", out var clipId))
                            {
                                ClipService.CancelClip(clipId.GetInt32());
                            }
                            break;
                        case "CreateClip":
                            root.TryGetProperty("Parameters", out JsonElement clipParameterElement);
                            await HandleCreateClip(clipParameterElement);
                            break;
                        case "CreateAiClip":
                            root.TryGetProperty("Parameters", out JsonElement aiClipParameterElement);
                            await HandleCreateAiClip(aiClipParameterElement);
                            break;
                        case "ApplyUpdate":
                            UpdateService.ApplyUpdate();
                            break;
                        case "CheckForUpdates":
                            Log.Information("CheckForUpdates command received.");
                            _ = Task.Run(UpdateService.UpdateAppIfNecessary);
                            break;
                        case "AddToWhitelist":
                            root.TryGetProperty("Parameters", out JsonElement addWhitelistParameterElement);
                            await HandleAddToWhitelist(addWhitelistParameterElement);
                            break;
                        case "RemoveFromWhitelist":
                            root.TryGetProperty("Parameters", out JsonElement removeWhitelistParameterElement);
                            await HandleRemoveFromWhitelist(removeWhitelistParameterElement);
                            break;
                        case "AddToBlacklist":
                            root.TryGetProperty("Parameters", out JsonElement addBlacklistParameterElement);
                            await HandleAddToBlacklist(addBlacklistParameterElement);
                            break;
                        case "RemoveFromBlacklist":
                            root.TryGetProperty("Parameters", out JsonElement removeBlacklistParameterElement);
                            await HandleRemoveFromBlacklist(removeBlacklistParameterElement);
                            break;
                        case "DeleteContent":
                            root.TryGetProperty("Parameters", out JsonElement deleteContentParameterElement);
                            await HandleDeleteContent(deleteContentParameterElement);
                            break;
                        case "UploadContent":
                            root.TryGetProperty("Parameters", out JsonElement uploadContentParameterElement);
                            await UploadService.HandleUploadContent(uploadContentParameterElement);
                            break;
                        case "OpenFileLocation":
                            root.TryGetProperty("Parameters", out JsonElement openFileLocationParameterElement);
                            openFileLocationParameterElement.TryGetProperty("FilePath", out JsonElement filePathElement);
                            Process.Start("explorer.exe", $"/select,\"{filePathElement.ToString().Replace("/", "\\")}\"");
                            break;
                        case "OpenInBrowser":
                            root.TryGetProperty("Parameters", out JsonElement openInBrowserParameterElement);
                            if (openInBrowserParameterElement.TryGetProperty("Url", out JsonElement urlElement))
                            {
                                string url = urlElement.GetString()!;
                                Log.Information($"Opening URL in browser: {url}");
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = url,
                                    UseShellExecute = true
                                });
                            }
                            else
                            {
                                Log.Error("URL parameter not found in OpenInBrowser message");
                            }
                            break;
                        case "OpenLogsLocation":
                            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra");
                            string? logFilePath = Directory.GetFiles(logDir, "*.log").FirstOrDefault();
                            if (!string.IsNullOrEmpty(logFilePath))
                            {
                                Process.Start("explorer.exe", $"/select,\"{logFilePath}\"");
                            }
                            else
                            {
                                Log.Warning("No log files found in the Segra directory");
                            }
                            break;
                        case "SelectGameExecutable":
                            await HandleSelectGameExecutable();
                            break;
                        case "StartRecording":
                            if (Settings.Instance.State.Recording != null || Settings.Instance.State.PreRecording != null)
                            {
                                Log.Information("Recording already in progress. Skipping...");
                                return;
                            }

                            await Task.Run(() => OBSService.StartRecording(startManually: true));
                            break;
                        case "StopRecording":
                            await Task.Run(OBSService.StopRecording);
                            break;
                        case "NewConnection":
                            Log.Information("NewConnection command received.");
                            await SendSettingsToFrontend("New connection");

                            // Get current version
                            if (UpdateService.UpdateManager.CurrentVersion != null)
                            {
                                string appVersion = UpdateService.UpdateManager.CurrentVersion.ToString();

                                // Send version to frontend to prevent mismatch
                                await SendFrontendMessage("AppVersion", new
                                {
                                    version = appVersion
                                });
                            }

                            _ = Task.Run(UpdateService.GetReleaseNotes);
                            break;
                        case "SetVideoLocation":
                            await SetVideoLocationAsync();
                            Log.Information("SetVideoLocation command received.");
                            break;
                        case "UpdateSettings":
                            root.TryGetProperty("Parameters", out JsonElement settingsParameterElement);
                            Log.Information("UpdateSettings command received.");
                            SettingsService.HandleUpdateSettings(settingsParameterElement);
                            break;
                        case "AddBookmark":
                            root.TryGetProperty("Parameters", out JsonElement bookmarkParameterElement);
                            await ContentService.HandleAddBookmark(bookmarkParameterElement);
                            Log.Information("AddBookmark command received.");
                            break;
                        case "DeleteBookmark":
                            root.TryGetProperty("Parameters", out JsonElement deleteBookmarkParameterElement);
                            await ContentService.HandleDeleteBookmark(deleteBookmarkParameterElement);
                            Log.Information("DeleteBookmark command received.");
                            break;
                        case "RenameContent":
                            root.TryGetProperty("Parameters", out JsonElement renameContentParameterElement);
                            await ContentService.HandleRenameContent(renameContentParameterElement);
                            Log.Information("RenameContent command received.");
                            break;
                        case "ImportFile":
                            root.TryGetProperty("Parameters", out JsonElement importParameterElement);
                            await ImportService.HandleImportFile(importParameterElement);
                            Log.Information("ImportFile command received.");
                            break;
                        case "ping":
                            await SendFrontendMessage("pong", new { });
                            break;
                        default:
                            Log.Information($"Unknown method: {method}");
                            break;
                    }
                }
                else
                {
                    Log.Information("Method property not found in message.");
                }
            }
            catch (JsonException ex)
            {
                Log.Error($"Failed to parse message as JSON: {ex.Message}");
            }
        }
        private static async Task HandleCreateAiClip(JsonElement message)
        {
            Log.Information($"{message}");
            message.TryGetProperty("FileName", out JsonElement fileNameElement);
            await AiService.AnalyzeVideo(fileNameElement.GetString()!);
        }

        private static async Task HandleCreateClip(JsonElement message)
        {
            Log.Information($"{message}");

            if (message.TryGetProperty("Selections", out JsonElement selectionsElement))
            {
                var selections = new List<Selection>();
                foreach (var selectionElement in selectionsElement.EnumerateArray())
                {
                    if (selectionElement.TryGetProperty("id", out JsonElement idElement) &&
                        selectionElement.TryGetProperty("startTime", out JsonElement startTimeElement) &&
                        selectionElement.TryGetProperty("endTime", out JsonElement endTimeElement) &&
                        selectionElement.TryGetProperty("fileName", out JsonElement fileNameElement) &&
                        selectionElement.TryGetProperty("type", out JsonElement videoTypeElement) &&
                        selectionElement.TryGetProperty("game", out JsonElement gameElement) &&
                        selectionElement.TryGetProperty("title", out JsonElement titleElement))
                    {
                        long id = idElement.GetInt64();
                        double startTime = startTimeElement.GetDouble();
                        double endTime = endTimeElement.GetDouble();
                        string fileName = fileNameElement.GetString()!;
                        string type = videoTypeElement.GetString()!;
                        string game = gameElement.GetString()!;
                        string title = titleElement.GetString() ?? string.Empty;

                        // Create a new Selection instance with all required properties.
                        selections.Add(new Selection
                        {
                            Id = id,
                            Type = type,
                            StartTime = startTime,
                            EndTime = endTime,
                            FileName = fileName,
                            Game = game,
                            Title = title
                        });
                    }
                }

                await ClipService.CreateClips(selections);
            }
            else
            {
                Log.Information("Selections property not found in CreateClip message.");
            }
        }

        public static async Task HandleDeleteContent(JsonElement message)
        {
            Log.Information($"Handling DeleteContent with message: {message}");

            // Extract FileName and ContentType
            if (message.TryGetProperty("FileName", out JsonElement fileNameElement) &&
                message.TryGetProperty("ContentType", out JsonElement contentTypeElement))
            {
                string fileName = fileNameElement.GetString()!;
                string contentTypeStr = contentTypeElement.GetString()!;

                if (Enum.TryParse(contentTypeStr, true, out Content.ContentType contentType))
                {
                    // Construct the full file path based on the file name and content type
                    string videoFolder = Settings.Instance.ContentFolder;
                    string contentTypeFolder = Path.Combine(videoFolder, contentType.ToString().ToLower() + "s");
                    string filePath = Path.Combine(contentTypeFolder, $"{fileName}.mp4"); // Assuming .mp4 extension

                    // Invoke the deletion asynchronously
                    await ContentService.DeleteContent(filePath, contentType);
                }
                else
                {
                    Log.Error($"Invalid ContentType provided: {contentTypeStr}");
                }
            }
            else
            {
                Log.Information("FileName or ContentType property not found in DeleteContent message.");
            }
        }

        private static Task SetVideoLocationAsync()
        {
            using (var fbd = new FolderBrowserDialog())
            {
                // Set an initial description or instruction for the dialog
                fbd.Description = "Select a folder to set as the video location.";

                // Optionally, set the root folder for the dialog (e.g., My Computer or Desktop)
                fbd.RootFolder = Environment.SpecialFolder.Desktop;

                // Show the dialog and check if the user selected a folder
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    // Get the selected folder path
                    string selectedPath = fbd.SelectedPath;
                    Log.Information($"Selected Folder: {selectedPath}");

                    // Update settings with the selected folder path
                    Settings.Instance.ContentFolder = selectedPath;
                }
                else
                {
                    Log.Information("Folder selection was canceled.");
                }
            }

            return Task.CompletedTask;
        }

        public static async Task StartWebsocket()
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:5000/");
            listener.Start();
            Log.Information("WebSocket server started at ws://localhost:5000/");

            try
            {
                while (true)
                {
                    HttpListenerContext context = await listener.GetContextAsync();

                    if (context.Request.IsWebSocketRequest)
                    {
                        Log.Information("Received WebSocket connection request");

                        // Close the current WebSocket if already active
                        if (activeWebSocket != null && activeWebSocket.State == WebSocketState.Open)
                        {
                            await activeWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "New connection", CancellationToken.None);
                            Log.Information("Closed previous WebSocket connection.");
                        }

                        HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null);
                        activeWebSocket = wsContext.WebSocket;

                        Log.Information("WebSocket connection established");
                        await HandleWebSocketAsync(activeWebSocket);
                    }
                    else
                    {
                        Log.Information("Invalid request: Not a WebSocket request");
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Information($"Exception in StartWebsocket: {ex.Message}");
                if (ex.StackTrace != null)
                {
                    Log.Information(ex.StackTrace);
                }
            }
        }

        private static async Task HandleWebSocketAsync(WebSocket webSocket)
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Log.Information("Client initiated WebSocket closure.");
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client initiated closure", CancellationToken.None);
                    }
                    else
                    {
                        string receivedMessage = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        Log.Information($"Received message: {receivedMessage}");
                        await HandleMessage(receivedMessage);
                    }
                }
            }
            catch (WebSocketException wsEx)
            {
                Log.Information($"WebSocketException in HandleWebSocketAsync: {wsEx.Message}");
                Log.Information($"WebSocket state at exception: {webSocket.State}");
                if (wsEx.InnerException != null)
                {
                    Log.Information($"Inner exception: {wsEx.InnerException.Message}");
                }
            }
            catch (Exception ex)
            {
                Log.Information($"General exception in HandleWebSocketAsync: {ex.Message}");
            }
            finally
            {
                if (webSocket.State != WebSocketState.Closed && webSocket.State != WebSocketState.Aborted)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, "Server-side error", CancellationToken.None);
                }
                Log.Information("WebSocket connection closed.");
            }
        }

        public static async Task SendFrontendMessage(string method, object content)
        {
            await sendLock.WaitAsync();
            try
            {
                // Wait for up to 10 seconds for the websocket to be open
                int maxWaitTimeMs = 10000;
                int waitIntervalMs = 100;
                int elapsedTime = 0;

                while ((activeWebSocket == null || activeWebSocket.State != WebSocketState.Open)
                    && elapsedTime < maxWaitTimeMs)
                {
                    await Task.Delay(waitIntervalMs);
                    elapsedTime += waitIntervalMs;
                }

                if (activeWebSocket?.State == WebSocketState.Open)
                {
                    var message = new { method, content };
                    byte[] buffer = JsonSerializer.SerializeToUtf8Bytes(message, jsonOptions);
                    await activeWebSocket.SendAsync(
                        buffer,
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        cancellationToken: CancellationToken.None
                    );
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error sending message: {ex.Message}");
            }
            finally
            {
                sendLock.Release();
            }
        }

        public static async Task ShowModal(string title, string description, string type = "info", string? subtitle = null)
        {
            // Validate the modal type
            if (type != "info" && type != "warning" && type != "error")
            {
                Log.Warning($"Invalid modal type '{type}'. Defaulting to 'info'.");
                type = "info";
            }

            var modalContent = new
            {
                title,
                subtitle,
                description,
                type
            };

            await SendFrontendMessage("ShowModal", modalContent);
            Log.Information($"Sent modal to frontend: {title} ({type})");
        }

        public static async Task SendSettingsToFrontend(string cause)
        {
            if (!Program.hasLoadedInitialSettings || Settings.Instance._isBulkUpdating)
                return;

            Log.Information("Sending settings to frontend ({Cause})", cause);
            await SendFrontendMessage("Settings", Settings.Instance);
        }

        private static async Task HandleAddToWhitelist(JsonElement parameters)
        {
            try
            {
                if (parameters.TryGetProperty("game", out JsonElement gameElement))
                {
                    var game = JsonSerializer.Deserialize<Game>(gameElement.GetRawText());
                    if (game != null && !string.IsNullOrEmpty(game.Name) && !string.IsNullOrEmpty(game.Path))
                    {
                        var comparer = new GameEqualityComparer();
                        bool exists = Settings.Instance.Whitelist.Any(g => comparer.Equals(g, game));

                        if (!exists)
                        {
                            var whitelist = new List<Game>(Settings.Instance.Whitelist);
                            whitelist.Add(game);
                            Settings.Instance.Whitelist = whitelist;
                            Log.Information($"Added game {game.Name} to whitelist");
                        }
                        else
                        {
                            Log.Information($"Game {game.Name} already exists in whitelist");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error adding to whitelist: {ex.Message}");
                await ShowModal("Error", $"Failed to add game to whitelist: {ex.Message}", "error");
            }
        }

        private static async Task HandleRemoveFromWhitelist(JsonElement parameters)
        {
            try
            {
                if (parameters.TryGetProperty("game", out JsonElement gameElement))
                {
                    var game = JsonSerializer.Deserialize<Game>(gameElement.GetRawText());
                    if (game != null && !string.IsNullOrEmpty(game.Name) && !string.IsNullOrEmpty(game.Path))
                    {
                        var comparer = new GameEqualityComparer();
                        var existingGame = Settings.Instance.Whitelist.FirstOrDefault(g => comparer.Equals(g, game));

                        if (existingGame != null)
                        {
                            var whitelist = new List<Game>(Settings.Instance.Whitelist);
                            whitelist.Remove(existingGame);
                            Settings.Instance.Whitelist = whitelist;
                            Log.Information($"Removed game {game.Name} from whitelist");
                        }
                        else
                        {
                            Log.Information($"Game {game.Name} does not exist in whitelist");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error removing from whitelist: {ex.Message}");
                await ShowModal("Error", $"Failed to remove game from whitelist: {ex.Message}", "error");
            }
        }

        private static async Task HandleAddToBlacklist(JsonElement parameters)
        {
            try
            {
                if (parameters.TryGetProperty("game", out JsonElement gameElement))
                {
                    var game = JsonSerializer.Deserialize<Game>(gameElement.GetRawText());
                    if (game != null && !string.IsNullOrEmpty(game.Name) && !string.IsNullOrEmpty(game.Path))
                    {
                        var comparer = new GameEqualityComparer();
                        bool exists = Settings.Instance.Blacklist.Any(g => comparer.Equals(g, game));

                        if (!exists)
                        {
                            var blacklist = new List<Game>(Settings.Instance.Blacklist);
                            blacklist.Add(game);
                            Settings.Instance.Blacklist = blacklist;
                            Log.Information($"Added game {game.Name} to blacklist");
                        }
                        else
                        {
                            Log.Information($"Game {game.Name} already exists in blacklist");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error adding to blacklist: {ex.Message}");
                await ShowModal("Error", $"Failed to add game to blacklist: {ex.Message}", "error");
            }
        }

        private static async Task HandleRemoveFromBlacklist(JsonElement parameters)
        {
            try
            {
                if (parameters.TryGetProperty("game", out JsonElement gameElement))
                {
                    var game = JsonSerializer.Deserialize<Game>(gameElement.GetRawText());
                    if (game != null && !string.IsNullOrEmpty(game.Name) && !string.IsNullOrEmpty(game.Path))
                    {
                        var comparer = new GameEqualityComparer();
                        var existingGame = Settings.Instance.Blacklist.FirstOrDefault(g => comparer.Equals(g, game));

                        if (existingGame != null)
                        {
                            var blacklist = new List<Game>(Settings.Instance.Blacklist);
                            blacklist.Remove(existingGame);
                            Settings.Instance.Blacklist = blacklist;
                            Log.Information($"Removed game {game.Name} from blacklist");
                        }
                        else
                        {
                            Log.Information($"Game {game.Name} does not exist in blacklist");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error removing from blacklist: {ex.Message}");
                await ShowModal("Error", $"Failed to remove game from blacklist: {ex.Message}", "error");
            }
        }

        private static async Task HandleSelectGameExecutable()
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "Executable Files (*.exe)|*.exe",
                    Title = "Select Game Executable",
                    CheckFileExists = true,
                    CheckPathExists = true,
                    Multiselect = false
                };

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    string filePath = openFileDialog.FileName;
                    string fileName = Path.GetFileNameWithoutExtension(filePath);

                    // Create a game object with lowercase property names to match frontend expectations
                    var gameObject = new
                    {
                        name = fileName,
                        path = filePath
                    };

                    // Send the selected game back to the frontend
                    await SendFrontendMessage("SelectedGameExecutable", gameObject);
                    Log.Information($"Selected game executable: {filePath}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error selecting game executable: {ex.Message}");
                await ShowModal("Error", $"Failed to select game executable: {ex.Message}", "error");
            }
        }
    }
}
