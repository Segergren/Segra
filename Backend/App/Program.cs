using NuGet.Versioning;
using Photino.NET;
using Photino.NET.Server;
using Segra.Backend.Api;
using Segra.Backend.Core.Models;
using Segra.Backend.Obs;
using Segra.Backend.Services;
using Segra.Backend.Utils;
using Segra.Backend.Windows.Storage;
using Serilog;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using Velopack;

namespace Segra.Backend.App
{
    class Program
    {
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        const int SW_HIDE = 0;
        private static readonly AutoResetEvent ShowWindowEvent = new AutoResetEvent(false);
        public static bool hasLoadedInitialSettings = false;
        public static PhotinoWindow? Window { get; private set; }
        private static readonly string LogFilePath =
          Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra", "logs.log");
        private const string PipeName = "Segra_SingleInstance";
        private static Mutex? singleInstanceMutex;
        private static Thread? pipeServerThread;
        private static string? appUrl;
        private const long maxFileSizeBytes = 10 * 1024 * 1024; // 10MB

        [STAThread]
        static void Main(string[] args)
        {
            // Try to create a named mutex - this will fail if another instance exists
            singleInstanceMutex = new Mutex(true, "SegraApplicationMutex", out bool createdNew);

            if (!createdNew)
            {
                // Another instance exists, send a message to it via named pipe
                try
                {
                    using (var pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                    {
                        pipeClient.Connect(3000);

                        using (var writer = new StreamWriter(pipeClient))
                        {
                            writer.WriteLine("SHOW_WINDOW");
                            writer.Flush();
                        }
                    }

                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to communicate with existing instance: {ex.Message}");
                }
            }

            StartNamedPipeServer();

            var logDirectory = Path.GetDirectoryName(LogFilePath);
            if (logDirectory != null && !Directory.Exists(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            ConfigureLogging();

            // Get the current version
            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;

            VelopackApp.Build()
                .OnBeforeUpdateFastCallback((v) =>
                {
                    if (UpdateService.UpdateManager == null)
                    {
                        Log.Error("UpdateManager is null");
                        return;
                    }
                    SemanticVersion? currentVersion = UpdateService.UpdateManager.CurrentVersion;
                    if (currentVersion == null)
                    {
                        Log.Error("Current version is null");
                        return;
                    }
                    Log.Information($"Updating from version {currentVersion} to {v}");
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "segra.tmp"), currentVersion.ToString());
                })
                .OnAfterUpdateFastCallback((v) =>
                {
                    string previousVersionPath = Path.Combine(Path.GetTempPath(), "segra.tmp");
                    if (File.Exists(previousVersionPath))
                    {
                        string previousVersion = File.ReadAllText(previousVersionPath);
                        Log.Information($"Updated from version {previousVersion} to {v}");
                        Task.Run(async () =>
                        {
                            await Task.Delay(5000);
                            _ = MessageService.SendFrontendMessage("ShowReleaseNotes", previousVersion);
                        });
                        File.Delete(previousVersionPath);
                    }
                })
                .OnFirstRun((v) =>
                {
                    Log.Information($"First run of Segra {v}");
                })
                .Run();

            try
            {
                Log.Information("Application starting up...");

                // Set up the PhotinoServer
                PhotinoServer
                    .CreateStaticFileServer(args, out string baseUrl)
                    .RunAsync();

                bool IsDebugMode = Debugger.IsAttached;
                appUrl = IsDebugMode ? "http://localhost:2882" : $"{baseUrl}/index.html";

                if (IsDebugMode)
                {
                    Task.Run(() =>
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = "/c npm run dev",
                            WorkingDirectory = Path.Join(GetSolutionPath(), @"Frontend")
                        };

                        using (HttpClient client = new())
                        {
                            client.DefaultRequestHeaders.ExpectContinue = false;
                            try
                            {
                                // Set a short timeout since we're just checking if the server is running
                                client.Timeout = TimeSpan.FromSeconds(1);
                                var response = client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "http://localhost:2882/index.html")).Result;
                            }
                            catch (Exception)
                            {
                                Process.Start(startInfo);
                            }
                        }
                    });
                }

                // Get the directory containing the executable
                Log.Information("Serving React app at {AppUrl}", appUrl);

                Task.Run(() =>
                {
                    string prefix = "http://localhost:2222/";
                    ContentServer.StartServer(prefix);
                });

                bool firstRun = !SettingsService.LoadSettings();
                hasLoadedInitialSettings = true;
                Settings.Instance.State.Initialize();
                SettingsService.SaveSettings();
                if (firstRun)
                {
                    _ = SettingsService.LoadContentFromFolderIntoState(true);
                    StartupService.SetStartupStatus(true);
                    Settings.Instance.State.GpuVendor = GeneralUtils.DetectGpuVendor();
                }

                // Ensure content folder exists
                if (!Directory.Exists(Settings.Instance.ContentFolder))
                {
                    Directory.CreateDirectory(Settings.Instance.ContentFolder);
                }

                // Run data migrations
                Task.Run(MigrationService.RunMigrations);

                // Try to login with stored credentials
                Task.Run(AuthService.TryAutoLogin);

                // Start WebSocket and Load Settings
                Task.Run(MessageService.StartWebsocket);
                Task.Run(StorageService.EnsureStorageBelowLimit);

                // Check for updates
                Task.Run(UpdateService.UpdateAppIfNecessary);

                // Check if application was launched from startup
                bool startMinimized = IsLaunchedFromStartup();
                Log.Information($"Starting application{(startMinimized ? " minimized from startup" : "")}");

                AddNotifyIcon();

                GameDetectionService.ForegroundHook.Start();

                // Run the OBS Initializer in a separate thread and application to make sure someting on the main thread doesn't block
                Task.Run(() => Application.Run(new OBSWindow()));

                if (!startMinimized)
                {
                    LoadFrontend();
                }

                // Wait for show window events
                while (true)
                {
                    int signalIndex = WaitHandle.WaitAny([ShowWindowEvent]);
                    Log.Information($"Signal received: {signalIndex}");
                    if (signalIndex == 0)
                    {
                        Log.Information("Show window event triggered");
                        ShowApplicationWindow().GetAwaiter().GetResult();
                        Log.Information("Show window event completed");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application terminated unexpectedly.");
            }
            finally
            {
                Log.Information("Application shutting down.");
                Log.CloseAndFlush(); // Ensure all logs are written before the application exits

                // Release the mutex when closing
                if (singleInstanceMutex != null)
                {
                    singleInstanceMutex.ReleaseMutex();
                    singleInstanceMutex.Dispose();
                }
            }
        }

        public static void ConfigureLogging()
        {
            PurgeOldLogs();

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.Debug()
                .WriteTo.File(
                    path: LogFilePath,
                    fileSizeLimitBytes: maxFileSizeBytes,
                    rollOnFileSizeLimit: false,
                    shared: true
                )
                .CreateLogger();
        }

        private static void PurgeOldLogs()
        {
            try
            {
                var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra");

                if (!Directory.Exists(logDirectory))
                    return;

                var logFiles = Directory.GetFiles(logDirectory, "*.log");

                if (logFiles.Length == 0)
                    return;

                // Get the first .log file found
                var logFilePath = logFiles[0];
                var fileInfo = new FileInfo(logFilePath);

                if (!fileInfo.Exists || fileInfo.Length <= maxFileSizeBytes)
                    return;

                var lines = File.ReadAllLines(logFilePath).ToList();
                var targetSize = (long)(maxFileSizeBytes * 0.9);
                var avgLineSize = fileInfo.Length / lines.Count;
                var linesToKeep = (int)(targetSize / avgLineSize);

                if (linesToKeep < lines.Count)
                {
                    var recentLines = lines.Skip(lines.Count - linesToKeep).ToList();
                    File.WriteAllLines(logFilePath, recentLines);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Error purging logs: {ex.Message}");
            }
        }

        public static void SetFullscreen(bool enabled)
        {
            try
            {
                if (Window == null) return;
                Window.SetMaximized(enabled);
                Log.Information($"Set maximized (fullscreen-mode): {enabled}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error setting fullscreen state");
            }
        }

        private static async Task ShowApplicationWindow()
        {
            Log.Information("Showing application window. Window is " + (Window == null ? "null" : "not null"));
            if (Window == null)
            {
                // Schedule the foreground operations with a delay before calling LoadFrontend
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200);
                    Log.Information("Bringing application window to foreground from scheduled task");
                    if (Window != null)
                    {
                        Window.SetMinimized(false);
                        Window.SetTopMost(true);
                        await Task.Delay(200);
                        Window.SetTopMost(false);
                        Log.Information("Application window brought to foreground");
                    }
                });

                LoadFrontend();
            }
            else
            {
                Log.Information("Bringing application window to foreground. Window is not null");
                Window.SetMinimized(false);
                Window.SetTopMost(true);
                await Task.Delay(200);
                Window.SetTopMost(false);
                Log.Information("Application window brought to foreground");
            }
        }

        private static void HideApplicationWindow()
        {
            Window?.SetMinimized(true);

            IntPtr hWnd = Process.GetCurrentProcess().MainWindowHandle;
            ShowWindow(hWnd, SW_HIDE); // Hides the window from the taskbar

            Log.Information("Application window hidden");
        }

        private static void LoadFrontend()
        {
            Log.Information("Loading frontend, app url is " + appUrl);
            // Initialize the PhotinoWindow
            Window = new PhotinoWindow()
                .SetNotificationsEnabled(false) // Disabled due to it creating a second start menu entry with incorrect start path. See https://github.com/tryphotino/photino.NET/issues/85
                .SetUseOsDefaultSize(false)
                .SetIconFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico"))
                .SetSize(new Size(1280, 720))
                .Center()
                .SetResizable(true)
                .RegisterWebMessageReceivedHandler((sender, message) =>
                {
                    Window = (PhotinoWindow)sender!;
                    _ = MessageService.HandleMessage(message);
                })
                .Load(appUrl);

            Log.Information("Window variable has been set");

            // intentional space after name because of https://github.com/tryphotino/photino.NET/issues/106
            Window.SetTitle("Segra ");

            Window.RegisterWindowClosingHandler((sender, eventArgs) =>
            {
                HideApplicationWindow();
                return true;
            });

            Window.WaitForClose();
        }

        private static void StartNamedPipeServer()
        {
            pipeServerThread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var pipeServer = new NamedPipeServerStream(PipeName, PipeDirection.In))
                        {
                            pipeServer.WaitForConnection();

                            using (var reader = new StreamReader(pipeServer))
                            {
                                string? message = reader.ReadLine();
                                if (message == "SHOW_WINDOW")
                                {
                                    if (Window != null)
                                    {
                                        Window.SetMinimized(false);
                                        Window.SetTopMost(true);
                                        Thread.Sleep(200);
                                        Window.SetTopMost(false);
                                        Log.Information("Window brought to foreground directly from pipe server");
                                    }
                                    else
                                    {
                                        // Only signal the main thread to create the window if it doesn't exist
                                        ShowWindowEvent.Set();
                                        Log.Information("ShowWindowEvent set");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (Log.Logger != null)
                        {
                            Log.Error(ex, "Error in named pipe server");
                        }
                        else
                        {
                            Console.WriteLine($"Error in named pipe server: {ex.Message}");
                        }

                        Thread.Sleep(1000);
                    }
                }
            });

            pipeServerThread.IsBackground = true;
            pipeServerThread.Start();
        }

        // Check if the application was launched from startup
        private static bool IsLaunchedFromStartup()
        {
            return Environment.GetCommandLineArgs().Contains("--from-startup");
        }

        private static void AddNotifyIcon()
        {
            var trayThread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (var icon = new NotifyIcon())
                {
                    icon.Icon = new Icon(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico"));
                    icon.Text = "Segra";
                    icon.Visible = true;

                    var menu = new ContextMenuStrip();
                    menu.Items.Add("Open", null, async (s, e) => await ShowApplicationWindow());
                    menu.Items.Add("Exit", null, (s, e) => Environment.Exit(0));
                    icon.ContextMenuStrip = menu;

                    icon.MouseDoubleClick += async (s, e) =>
                    {
                        if (e.Button == MouseButtons.Left)
                            await ShowApplicationWindow();
                    };

                    Application.Run();
                }
            });
            trayThread.SetApartmentState(ApartmentState.STA);
            trayThread.IsBackground = true;
            trayThread.Start();
        }

        private static string GetSolutionPath()
        {
            string currentDirectory = Directory.GetCurrentDirectory();

            string directory = currentDirectory;
            while (!string.IsNullOrEmpty(directory) && !Directory.GetFiles(directory, "*.sln").Any())
            {
                directory = Directory.GetParent(directory)?.FullName!;
            }

            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("Solution path could not be found. Ensure you are running this application within a solution directory.");
            }

            return directory;
        }
    }
}
