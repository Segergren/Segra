using Serilog;
using Segra.Backend.App;
using System.Text.RegularExpressions;

namespace Segra.Backend.Shared
{
    internal static class VelopackUtils
    {
        private static readonly string VelopackLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Segra",
            "velopack.log"
        );

        /// <summary>
        /// Checks the Velopack log file for recent update errors and displays them to the user.
        /// Reads from the bottom of the log file upward until it finds the most recent "Applying package" entry,
        /// checking for any ERROR entries in between.
        /// </summary>
        public static async Task CheckForRecentUpdateErrors()
        {
            try
            {
                if (!File.Exists(VelopackLogPath))
                {
                    return;
                }

                string[] logLines = await File.ReadAllLinesAsync(VelopackLogPath);

                DateTime now = DateTime.Now;
                DateTime cutoffTime = now.AddSeconds(-10);

                var errorPattern = new Regex(@"\[update:\d+\]\s+\[(\d{2}:\d{2}:\d{2})\]\s+\[ERROR\]\s+(.+)");
                var applyingPackagePattern = new Regex(@"\[update:\d+\]\s+\[(\d{2}:\d{2}:\d{2})\]\s+\[INFO\]\s+Applying package");
                var packageAppliedSuccessPattern = new Regex(@"\[update:\d+\]\s+\[(\d{2}:\d{2}:\d{2})\]\s+\[INFO\]\s+Package applied successfully");

                foreach (string line in logLines.Reverse())
                {
                    var successMatch = packageAppliedSuccessPattern.Match(line);
                    if (successMatch.Success)
                    {
                        return;
                    }

                    var applyingMatch = applyingPackagePattern.Match(line);
                    if (applyingMatch.Success)
                    {
                        break;
                    }

                    var errorMatch = errorPattern.Match(line);
                    if (errorMatch.Success)
                    {
                        string timeStr = errorMatch.Groups[1].Value;
                        string errorMessage = errorMatch.Groups[2].Value;

                        if (TimeSpan.TryParse(timeStr, out TimeSpan logTime))
                        {
                            DateTime logDateTime = DateTime.Today.Add(logTime);

                            if (logDateTime > now)
                            {
                                logDateTime = logDateTime.AddDays(-1);
                            }

                            if (logDateTime >= cutoffTime && logDateTime <= now)
                            {
                                string displayMessage = ExtractErrorMessage(errorMessage);

                                Log.Warning("Found recent Velopack update error: {Error}", displayMessage);

                                await MessageService.ShowModal(
                                    "Update Error",
                                    displayMessage,
                                    "error",
                                    "An error occurred during the update process"
                                );

                                return;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error checking Velopack log file");
            }
        }

        /// <summary>
        /// Extracts the actual error message from the log entry.
        /// Gets the text after the last colon and trims whitespace.
        /// Example: "Apply error: Error applying package: Unable to start the update, because one or more running processes prevented it." 
        /// returns "Unable to start the update, because one or more running processes prevented it."
        /// </summary>
        private static string ExtractErrorMessage(string fullErrorMessage)
        {
            int lastColonIndex = fullErrorMessage.LastIndexOf(':');
            if (lastColonIndex >= 0 && lastColonIndex < fullErrorMessage.Length - 1)
            {
                return fullErrorMessage.Substring(lastColonIndex + 1).Trim();
            }

            return fullErrorMessage.Trim();
        }
    }
}
