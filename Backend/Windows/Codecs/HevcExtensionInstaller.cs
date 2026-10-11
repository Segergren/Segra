using Serilog;
using Segra.Backend.App;
using global::Windows.Management.Deployment;

namespace Segra.Backend.Windows.Codecs
{
    internal static class HevcExtensionInstaller
    {
        private const string BundleUrl = "https://cdn.segra.tv/hevc/HEVCVideoExtension-2.4.149.0.AppxBundle";
        private const int ErrorInstallPackageDowngrade = unchecked((int)0x80073D06);
        private static int _busy;

        public static async Task InstallAsync()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1)
                return;

            string bundlePath = Path.Combine(Path.GetTempPath(), "Segra-HEVCVideoExtension.AppxBundle");
            bool success = false;
            try
            {
                Log.Information("Installing HEVC Video Extension from {Url}", BundleUrl);
                using (var httpClient = new HttpClient())
                using (var download = await httpClient.GetStreamAsync(BundleUrl))
                using (var file = File.Create(bundlePath))
                {
                    await download.CopyToAsync(file);
                }

                await new PackageManager().AddPackageAsync(new Uri(bundlePath), null, DeploymentOptions.None);
                Log.Information("HEVC Video Extension installed");
                success = true;
            }
            catch (Exception ex) when (ex.HResult == ErrorInstallPackageDowngrade)
            {
                Log.Information("A newer HEVC Video Extension is already installed");
                success = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "HEVC Video Extension install failed");
                await MessageService.ShowModal("HEVC extension install failed", ex.Message, "error");
            }
            finally
            {
                try { File.Delete(bundlePath); } catch { }
                Interlocked.Exchange(ref _busy, 0);
            }

            await MessageService.SendFrontendMessage("HevcExtensionInstallFinished", new { success });
        }
    }
}
