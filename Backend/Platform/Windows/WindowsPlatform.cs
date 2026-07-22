using System.Diagnostics;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;
using Segra.Backend.App;
using Segra.Backend.Core;
using Segra.Backend.Core.Models;
using Segra.Backend.Shared;
using Segra.Backend.Windows.Audio;
using Segra.Backend.Windows.Display;
using Segra.Backend.Windows.Watchers;

namespace Segra.Backend.Platform.Windows
{
    internal sealed class WindowsTrayIcon : ITrayIcon
    {
        public void Initialize(Action onOpen, Action onExit)
        {
            var trayThread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using var icon = new NotifyIcon
                {
                    Icon = Properties.Resources.icon,
                    Text = "Segra",
                    Visible = true
                };

                var menu = new ContextMenuStrip();
                menu.Items.Add("Open", null, (s, e) => onOpen());
                menu.Items.Add("Exit", null, (s, e) => onExit());
                icon.ContextMenuStrip = menu;

                icon.MouseDoubleClick += (s, e) =>
                {
                    if (e.Button == MouseButtons.Left)
                        onOpen();
                };

                NotifyIconService.Initialize(icon);

                Application.Run();
            });
            trayThread.SetApartmentState(ApartmentState.STA);
            trayThread.IsBackground = true;
            trayThread.Start();
        }

        public void SetRecording(bool recording) =>
            NotifyIconService.SetNotifyIconStatus(recording ? NotifyIconState.Recording : NotifyIconState.Idle);
    }

    internal sealed class WindowsAudioWatcher : IPlatformWatcher
    {
        private readonly AudioDeviceWatcher _watcher = new();
        public event Action Changed
        {
            add => _watcher.DevicesChanged += value;
            remove => _watcher.DevicesChanged -= value;
        }
        public void Dispose() => _watcher.Dispose();
    }

    internal sealed class WindowsAudioDeviceService : IAudioDeviceService
    {
        public List<AudioDevice> GetInputDevices() => AudioDeviceService.GetInputDevices();
        public List<AudioDevice> GetOutputDevices() => AudioDeviceService.GetOutputDevices();
        public IPlatformWatcher CreateWatcher() => new WindowsAudioWatcher();
    }

    internal sealed class WindowsDisplayWatcher : IPlatformWatcher
    {
        private readonly DisplayWatcher _watcher = new();
        public event Action Changed
        {
            add => _watcher.DisplaysChanged += value;
            remove => _watcher.DisplaysChanged -= value;
        }
        public void Dispose() => _watcher.Dispose();
    }

    internal sealed class WindowsDisplayService : IDisplayService
    {
        public bool LoadAvailableMonitorsIntoState() => DisplayService.LoadAvailableMonitorsIntoState();

        public bool GetPrimaryMonitorPhysicalResolution(out uint width, out uint height)
        {
            if (DisplayService.GetPrimaryMonitorPhysicalResolution(out width, out height))
                return true;

            // Fall back to the logical (non-DPI-aware) resolution if the physical query failed.
            var primaryScreen = Screen.PrimaryScreen;
            if (primaryScreen != null)
            {
                width = (uint)primaryScreen.Bounds.Width;
                height = (uint)primaryScreen.Bounds.Height;
                return true;
            }

            width = 0;
            height = 0;
            return false;
        }

        public bool HasDisplayWithMinHeight(int minHeight) => DisplayService.HasDisplayWithMinHeight(minHeight);
        public IPlatformWatcher CreateWatcher() => new WindowsDisplayWatcher();
    }

    internal sealed class WindowsNativeDialogs : INativeDialogs
    {
        public Task<string?> PickFolderAsync(string description) => RunSta<string?>(() =>
        {
            using var fbd = new FolderBrowserDialog
            {
                Description = description,
                RootFolder = Environment.SpecialFolder.Desktop
            };
            return fbd.ShowDialog() == DialogResult.OK ? fbd.SelectedPath : null;
        });

        public Task<string?> PickFileAsync(string title, string filterDescription, string extension) => RunSta<string?>(() =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = title,
                Filter = $"{filterDescription}|*.{extension}",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = false,
                // Keep the process working directory pinned to the app directory.
                RestoreDirectory = true
            };
            return ofd.ShowDialog() == DialogResult.OK ? ofd.FileName : null;
        });

        public Task<string[]?> PickFilesAsync(string title, string filterDescription, string extension) => RunSta<string[]?>(() =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = title,
                Filter = $"{filterDescription}|*.{extension}",
                CheckFileExists = true,
                CheckPathExists = true,
                Multiselect = true,
                RestoreDirectory = true
            };
            return ofd.ShowDialog() == DialogResult.OK ? ofd.FileNames : null;
        });

        public void OpenFileLocation(string filePath)
        {
            string selectPath = filePath.Replace("/", "\\");
            Process.Start("explorer.exe", $"/select,\"{selectPath}\"");
        }

        public void OpenUrl(string url) =>
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

        public void CopyFileToClipboard(string filePath)
        {
            var thread = new Thread(() =>
            {
                var files = new System.Collections.Specialized.StringCollection { filePath };
                Clipboard.SetFileDropList(files);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        // WinForms dialogs and clipboard require an STA thread.
        private static Task<T> RunSta<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>();
            var thread = new Thread(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }
    }

    internal sealed class WindowsStartupManager : IStartupManager
    {
        public void SetStartupStatus(bool enable) => StartupService.SetStartupStatus(enable);
        public bool GetStartupStatus() => StartupService.GetStartupStatus();
    }

    internal sealed class WindowsSoundPlayer : ISoundPlayer
    {
        public void Play(byte[] wavData, float volume)
        {
            using var audioStream = new MemoryStream(wavData);
            using var audioReader = new WaveFileReader(audioStream);
            var sampleProvider = audioReader.ToSampleProvider();
            var volumeProvider = new VolumeSampleProvider(sampleProvider) { Volume = volume };

            using var waveOut = new WasapiOut(AudioClientShareMode.Shared, 100);
            waveOut.Init(volumeProvider);
            waveOut.Play();

            while (waveOut.PlaybackState == PlaybackState.Playing)
                Thread.Sleep(10);
        }
    }
}
