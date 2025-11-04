using System.Reflection;
using Serilog;

namespace Segra.Backend.App
{
    internal static class StartupService
    {
        public static void SetStartupStatus(bool enable)
        {
            try
            {
                string exePath = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".exe");
                if (exePath == null)
                {
                    Log.Error("Failed to get executable path");
                    return;
                }
                string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string linkPath = Path.Combine(startupFolder, "Segra.lnk");
                if (enable && !File.Exists(linkPath))
                {
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell")!;
                    object shell = Activator.CreateInstance(shellType)!;
                    object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath })!;
                    shortcut.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
                    shortcut.GetType().InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { "--from-startup" });
                    string? workingDir = Path.GetDirectoryName(exePath);
                    if (workingDir == null)
                    {
                        Log.Error("Failed to get working directory");
                        return;
                    }
                    shortcut.GetType().InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
                    shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                    Log.Information("Added Segra to startup");
                }
                else if (!enable && File.Exists(linkPath))
                {
                    File.Delete(linkPath);
                    Log.Information("Removed Segra from startup");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
            }
        }

        public static bool GetStartupStatus()
        {
            try
            {
                string linkPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Segra.lnk");
                return File.Exists(linkPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex.Message);
                return false;
            }
        }
    }
}
