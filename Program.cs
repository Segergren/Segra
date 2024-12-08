using Photino.NET;
using Photino.NET.Server;
using ReCaps.Backend.ContentServer;
using ReCaps.Backend.Utils;
using ReCaps.Models;
using Serilog;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using Velopack;

namespace ReCaps
{
    class Program
    {
        public static bool hasLoadedInitialSettings = false;
        public static PhotinoWindow window { get; private set; }
        private static readonly string LogFilePath =
          Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReCaps", "logs.log");

        [STAThread]
        static void Main(string[] args)
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Configure Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.Debug()
                .WriteTo.File(LogFilePath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            VelopackApp.Build().Run();

            Task.Run(() =>
            {
                UpdateUtils.UpdateAppIfNecessary();
            });

            try
            {
                Log.Information("Application starting up...");

                // Set up the PhotinoServer
                PhotinoServer
                    .CreateStaticFileServer(args, out string baseUrl)
                    .RunAsync();

                bool IsDebugMode = true;
                string appUrl = IsDebugMode ? "http://localhost:2882" : $"{baseUrl}/index.html";

                if (IsDebugMode)
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c npm run dev",
                        WorkingDirectory = Path.Join(GetSolutionPath(), @"Frontend")
                    };
                    Process process = null;

                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://localhost:2882/index.html");
                    request.AllowAutoRedirect = false;
                    request.Method = "HEAD";

                    try
                    {
                        request.GetResponse();
                    }
                    catch (WebException)
                    {
                        process ??= Process.Start(startInfo);
                    }
                }

                // Get the directory containing the executable
                Log.Information("Serving React app at {AppUrl}", appUrl);

                Task.Run(() =>
                {
                    string prefix = "http://localhost:2222/";
                    ContentServer.StartServer(prefix);
                });
                // Window title declared here for visibility
                string windowTitle = "ReCaps";

                SettingsUtils.LoadSettings();
                hasLoadedInitialSettings = true;
                Settings.Instance.State.Initialize();
                SettingsUtils.SaveSettings();

                // Start WebSocket and Load Settings
                Task.Run(MessageUtils.StartWebsocket);

                // Initialize the PhotinoWindow
                window = new PhotinoWindow()
                    //.SetIconFile("C:/Users/admin/Downloads/icon.ico")
                    .SetTitle(windowTitle)
                    .SetUseOsDefaultSize(false)
                    .SetSize(new Size(1280, 720))
                    .Center()
                    .SetResizable(true)
                    .RegisterWebMessageReceivedHandler((sender, message) =>
                    {
                        window = (PhotinoWindow)sender;
                        MessageUtils.HandleMessage(message);
                    })
                    .Load(appUrl);

                // Initialize OBSUtils
                Task.Run(() =>
                {
                    try
                    {
                        OBSUtils.Initialize();
                        Log.Information("OBSUtils initialized successfully.");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to initialize OBSUtils.");
                    }
                });
                window.WaitForClose(); // Starts the application event loop
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application terminated unexpectedly.");
            }
            finally
            {
                Log.Information("Application shutting down.");
                Log.CloseAndFlush(); // Ensure all logs are written before the application exits
            }
        }

        private static string GetSolutionPath()
        {
            string currentDirectory = Directory.GetCurrentDirectory();

            string directory = currentDirectory;
            while (!string.IsNullOrEmpty(directory) && !Directory.GetFiles(directory, "*.sln").Any())
            {
                directory = Directory.GetParent(directory)?.FullName;
            }

            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("Solution path could not be found. Ensure you are running this application within a solution directory.");
            }

            return directory;
        }
    }
}
