using System.Diagnostics;
using Serilog;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Segra.Backend.App;
using Segra.Backend.Core.Models;
using Segra.Backend.Windows.Audio;
using Segra.Backend.Windows.Display;
using Segra.Backend.Windows.Watchers;

namespace Segra.Backend.Platform.Windows
{
    internal sealed class WindowsTrayIcon : ITrayIcon
    {
        public void Initialize(TrayActions actions)
        {
            var trayThread = new Thread(() =>
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                WindowsTrayMenu.EnableDarkMode();

                using var icon = new NotifyIcon
                {
                    Icon = Properties.Resources.icon,
                    Text = "Segra",
                    Visible = true
                };

                // Owns the native tray menu
                var menuOwner = new NativeWindow();
                menuOwner.CreateHandle(new CreateParams());

                icon.MouseUp += (s, e) =>
                {
                    if (e.Button != MouseButtons.Right)
                        return;

                    switch (WindowsTrayMenu.Show(menuOwner.Handle, Properties.Resources.icon, actions))
                    {
                        case WindowsTrayMenu.Open:
                            actions.Open();
                            break;
                        case WindowsTrayMenu.StartRecording:
                            actions.StartRecording();
                            break;
                        case WindowsTrayMenu.StopRecording:
                            actions.StopRecording();
                            break;
                        case WindowsTrayMenu.SaveReplay:
                            actions.SaveReplay();
                            break;
                        case WindowsTrayMenu.ToggleAutoRecordGames:
                            actions.ToggleAutoRecordGames();
                            break;
                        case WindowsTrayMenu.ResetWindowSize:
                            actions.ResetWindowSize();
                            break;
                        case WindowsTrayMenu.Quit:
                            icon.Visible = false;
                            actions.Exit();
                            break;
                    }
                };

                icon.MouseDoubleClick += (s, e) =>
                {
                    if (e.Button == MouseButtons.Left)
                        actions.Open();
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
                try
                {
                    var files = new System.Collections.Specialized.StringCollection { filePath };
                    Clipboard.SetFileDropList(files);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to copy file to clipboard");
                }
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

            using var waveOut = new WasapiPlayerBuilder().WithSharedMode().WithLatency(100).Build();
            waveOut.Init(volumeProvider.ToWaveProvider());
            waveOut.Play();

            while (waveOut.PlaybackState == PlaybackState.Playing)
                Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Streams IEEE float32 PCM to the default render endpoint through NAudio from inside the
    /// Segra process. Unlike webview-rendered audio (which lives in msedgewebview2.exe), the
    /// resulting WASAPI session belongs to Segra.exe so Discord/OBS "application audio" capture
    /// of the Segra window includes it.
    /// </summary>
    internal sealed class WindowsAudioStreamPlayer : IAudioStreamPlayer
    {
        private readonly object gate = new();
        private WasapiPlayer? waveOut;
        private BufferedWaveProvider? provider;

        public void Start(int sampleRate, int channels)
        {
            lock (gate)
            {
                FlushLocked();

                if (sampleRate <= 0 || channels <= 0)
                    return;

                provider = new BufferedWaveProvider(
                    WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels), TimeSpan.FromSeconds(1))
                {
                    DiscardOnBufferOverflow = true
                };

                // Silence cushion so jittery PCM delivery doesn't underrun (crackle) right after a restart.
                var cushion = new byte[sampleRate / 20 * provider.WaveFormat.BlockAlign];
                provider.AddSamples(cushion, 0, cushion.Length);

                waveOut = new WasapiPlayerBuilder().WithSharedMode().WithLatency(40).Build();
                try
                {
                    waveOut.Init(provider);
                    waveOut.Play();
                }
                catch (Exception ex)
                {
                    Log.Warning($"Failed to start native audio stream: {ex.Message}");
                    FlushLocked();
                }
            }
        }

        public void Write(byte[] pcmData)
        {
            if (pcmData.Length == 0)
                return;
            lock (gate)
            {
                if (provider == null || waveOut == null)
                    return;
                try
                {
                    provider.AddSamples(pcmData, 0, pcmData.Length);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to queue PCM samples");
                }
            }
        }

        public void Flush()
        {
            lock (gate)
            {
                FlushLocked();
            }
        }

        private void FlushLocked()
        {
            if (waveOut != null)
            {
                try
                {
                    if (waveOut.PlaybackState == PlaybackState.Playing)
                        waveOut.Stop();
                    waveOut.Dispose();
                }
                catch
                {
                    // best effort shutdown
                }
                waveOut = null;
            }
            provider = null;
        }
    }
}
