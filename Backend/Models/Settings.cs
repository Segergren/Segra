using Segra.Backend.Audio;
using Segra.Backend.Utils;
using Serilog;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using static Segra.Backend.Utils.GeneralUtils;

namespace Segra.Backend.Models
{
    internal class Settings
    {
        private static Settings _instance = new Settings();
        public static Settings Instance => _instance;
        public bool _isBulkUpdating = false;

        private string _contentFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Segra").Replace("\\", "/");
        private string _theme = "segra";
        private string _resolution = "1080p";
        private int _frameRate = 60;
        private int _bitrate = 40;
        private int _minBitrate = 40;
        private int _maxBitrate = 60;
        private string _rateControl = "VBR";
        private int _crfValue = 23;
        private int _cqLevel = 20;
        private string _encoder = "gpu";
        private Codec? _codec = null; // Set in SelectDefaultCodec()
        private int _storageLimit = 100;
        private List<DeviceSetting> _inputDevices = new List<DeviceSetting>();
        private List<DeviceSetting> _outputDevices = new List<DeviceSetting>();
        private bool _forceMonoInputSources = false;
        private bool _enableDisplayRecording = true;
        private Display? _selectedDisplay = null;
        private bool _enableAi = true;
        private bool _autoGenerateHighlights = true;
        private bool _runOnStartup = false;
        private bool _receiveBetaUpdates = false;
        private RecordingMode _recordingMode = RecordingMode.Session;
        private int _replayBufferDuration = 30;
        private int _replayBufferMaxSize = 500;
        private List<Keybind> _keybindings;
        private List<Game> _whitelist = new List<Game>();
        private List<Game> _blacklist = new List<Game>();
        private State _state = new State();
        private Auth _auth = new Auth();
        private bool _clipClearSelectionsAfterCreatingClip = false;
        private bool _clipShowInBrowserAfterUpload = false;
        private string _clipEncoder = "cpu";
        private int _clipQualityCrf = 23;
        private string _clipCodec = "h264";
        private int _clipFps = 0; // 0 for 'Original'
        private string _clipAudioQuality = "128k";
        private string _clipPreset = "veryfast";
        private float _soundEffectsVolume = 0.5f;
        private bool _showNewBadgeOnVideos = true;
        private bool _showGameBackground = true;
        private bool _showAudioWaveformInTimeline = true;
        private bool _enableSeparateAudioTracks = false;

        // Returns the default keybindings
        private static List<Keybind> GetDefaultKeybindings()
        {
            return new List<Keybind>
            {
                new Keybind(new List<int> { 119 }, KeybindAction.CreateBookmark, true), // 119 is F8
                new Keybind(new List<int> { 121 }, KeybindAction.SaveReplayBuffer, true) // 121 is F10
            };
        }

        public Settings()
        {
            SetDefaultResolution();
            _keybindings = GetDefaultKeybindings();
        }

        // Begin bulk update suppression
        public void BeginBulkUpdate()
        {
            _isBulkUpdating = true;
        }

        public void EndBulkUpdateAndSaveSettings()
        {
            _isBulkUpdating = false;
            Log.Information("End bulk update");
            SendToFrontend("End bulk update");
            SettingsUtils.SaveSettings();
        }

        private void SendToFrontend(string cause)
        {
            if (!_isBulkUpdating)
            {
                _ = MessageUtils.SendSettingsToFrontend(cause);
            }
        }

        private void SetDefaultResolution()
        {
            int screenHeight = 1080; // Fallback value
            var primaryScreen = Screen.PrimaryScreen;

            if (primaryScreen != null)
            {
                screenHeight = primaryScreen.Bounds.Height;
            }

            // Determine resolution based on height
            if (screenHeight >= 2160)
            {
                _resolution = "4K";
            }
            else if (screenHeight >= 1440)
            {
                _resolution = "1440p";
            }
            else
            {
                _resolution = "1080p";
            }
        }

        [JsonPropertyName("contentFolder")]
        public string ContentFolder
        {
            get => _contentFolder;
            set
            {
                bool hasChanged = Instance._contentFolder != value.Replace("\\", "/");

                _contentFolder = value.Replace("\\", "/");
                Instance._contentFolder = value.Replace("\\", "/");

                if (hasChanged || Instance.State.Content.Count == 0)
                {
                    SendToFrontend("Content folder changed");
                    SettingsUtils.LoadContentFromFolderIntoState();
                    if (Instance != null && !Instance._isBulkUpdating)
                    {
                        SettingsUtils.SaveSettings();
                    }
                }
            }
        }

        [JsonPropertyName("theme")]
        public string Theme
        {
            get => _theme;
            set
            {
                _theme = value;
            }
        }

        [JsonPropertyName("resolution")]
        public string Resolution
        {
            get => _resolution;
            set
            {
                _resolution = value;
            }
        }

        [JsonPropertyName("frameRate")]
        public int FrameRate
        {
            get => _frameRate;
            set
            {
                _frameRate = value;
            }
        }

        [JsonPropertyName("rateControl")]
        public string RateControl
        {
            get => _rateControl;
            set
            {
                _rateControl = value;
            }
        }

        [JsonPropertyName("crfValue")]
        public int CrfValue
        {
            get => _crfValue;
            set
            {
                _crfValue = value;
            }
        }

        [JsonPropertyName("cqLevel")]
        public int CqLevel
        {
            get => _cqLevel;
            set
            {
                _cqLevel = value;
            }
        }

        [JsonPropertyName("bitrate")]
        public int Bitrate
        {
            get => _bitrate;
            set
            {
                _bitrate = value;
            }
        }

        // Minimum bitrate in Mbps (used for VBR only)
        [JsonPropertyName("minBitrate")]
        public int MinBitrate
        {
            get => _minBitrate;
            set
            {
                _minBitrate = value;
            }
        }

        // Maximum bitrate in Mbps (used for VBR only)
        [JsonPropertyName("maxBitrate")]
        public int MaxBitrate
        {
            get => _maxBitrate;
            set
            {
                _maxBitrate = value;
            }
        }

        [JsonPropertyName("encoder")]
        public string Encoder
        {
            get => _encoder;
            set
            {
                _encoder = value;
            }
        }

        [JsonPropertyName("codec")]
        public Codec? Codec
        {
            get => _codec;
            set
            {
                _codec = value;
            }
        }

        [JsonPropertyName("storageLimit")]
        public int StorageLimit
        {
            get => _storageLimit;
            set
            {
                _storageLimit = value;
            }
        }

        [JsonPropertyName("inputDevices")]
        public List<DeviceSetting> InputDevices
        {
            get => _inputDevices;
            set
            {
                _inputDevices = value;
            }
        }

        [JsonPropertyName("outputDevices")]
        public List<DeviceSetting> OutputDevices
        {
            get => _outputDevices;
            set
            {
                _outputDevices = value;
            }
        }

        [JsonPropertyName("enableDisplayRecording")]
        public bool EnableDisplayRecording
        {
            get => _enableDisplayRecording;
            set
            {
                if (_enableDisplayRecording != value)
                {
                    _enableDisplayRecording = value;
                }
            }
        }

        [JsonPropertyName("selectedDisplay")]
        public Display? SelectedDisplay
        {
            get => _selectedDisplay;
            set
            {
                _selectedDisplay = value;
            }
        }

        [JsonPropertyName("enableAi")]
        public bool EnableAi
        {
            get => _enableAi;
            set
            {
                if (_enableAi != value)
                {
                    _enableAi = value;
                    SendToFrontend("Enable AI changed");
                }
            }
        }

        [JsonPropertyName("autoGenerateHighlights")]
        public bool AutoGenerateHighlights
        {
            get => _autoGenerateHighlights;
            set
            {
                if (_autoGenerateHighlights != value)
                {
                    _autoGenerateHighlights = value;
                }
            }
        }

        [JsonPropertyName("runOnStartup")]
        public bool RunOnStartup
        {
            get => _runOnStartup;
            set
            {
                if (_runOnStartup != value)
                {
                    _runOnStartup = value;
                    StartupUtils.SetStartupStatus(value);
                }
            }
        }

        [JsonPropertyName("receiveBetaUpdates")]
        public bool ReceiveBetaUpdates
        {
            get => _receiveBetaUpdates;
            set
            {
                if (_receiveBetaUpdates != value)
                {
                    _receiveBetaUpdates = value;
                }
            }
        }

        [JsonPropertyName("recordingMode")]
        public RecordingMode RecordingMode
        {
            get => _recordingMode;
            set
            {
                if (_recordingMode != value)
                {
                    _recordingMode = value;
                    SendToFrontend("Recording mode changed");
                }
            }
        }

        [JsonPropertyName("whitelist")]
        public List<Game> Whitelist
        {
            get => _whitelist;
            set
            {
                bool hasChanged = !_whitelist.SequenceEqual(value, new GameEqualityComparer());
                _whitelist = value;
                if (hasChanged && !_isBulkUpdating)
                {
                    SendToFrontend("Whitelist changed");
                    SettingsUtils.SaveSettings();
                }
            }
        }

        [JsonPropertyName("blacklist")]
        public List<Game> Blacklist
        {
            get => _blacklist;
            set
            {
                bool hasChanged = !_blacklist.SequenceEqual(value, new GameEqualityComparer());
                _blacklist = value;
                if (hasChanged && !_isBulkUpdating)
                {
                    SendToFrontend("Blacklist changed");
                    SettingsUtils.SaveSettings();
                }
            }
        }

        [JsonPropertyName("replayBufferDuration")]
        public int ReplayBufferDuration
        {
            get => _replayBufferDuration;
            set
            {
                if (_replayBufferDuration != value)
                {
                    _replayBufferDuration = value;
                    SendToFrontend("Replay buffer duration changed");
                }
            }
        }

        [JsonPropertyName("replayBufferMaxSize")]
        public int ReplayBufferMaxSize
        {
            get => _replayBufferMaxSize;
            set
            {
                if (_replayBufferMaxSize != value)
                {
                    _replayBufferMaxSize = value;
                    SendToFrontend("Replay buffer max size changed");
                }
            }
        }

        [JsonPropertyName("state")]
        public State State
        {
            get => _state;
        }

        [JsonPropertyName("forceMonoInputSources")]
        public bool ForceMonoInputSources
        {
            get => _forceMonoInputSources;
            set
            {
                if (_forceMonoInputSources != value)
                {
                    _forceMonoInputSources = value;
                    SendToFrontend("Force mono input sources changed");
                }
            }
        }

        [JsonPropertyName("auth")]
        public Auth Auth
        {
            get => _auth;
            set
            {
                bool hasChanged = Instance._auth.Jwt != value.Jwt || Instance._auth.RefreshToken != value.RefreshToken;
                _auth = value;
                if (hasChanged)
                {
                    SendToFrontend("Auth changed");
                }
            }
        }

        [JsonPropertyName("clipClearSelectionsAfterCreatingClip")]
        public bool ClipClearSelectionsAfterCreatingClip
        {
            get => _clipClearSelectionsAfterCreatingClip;
            set
            {
                if (_clipClearSelectionsAfterCreatingClip != value)
                {
                    _clipClearSelectionsAfterCreatingClip = value;
                }
            }
        }

        [JsonPropertyName("clipShowInBrowserAfterUpload")]
        public bool ClipShowInBrowserAfterUpload
        {
            get => _clipShowInBrowserAfterUpload;
            set
            {
                if (_clipShowInBrowserAfterUpload != value)
                {
                    _clipShowInBrowserAfterUpload = value;
                }
            }
        }

        [JsonPropertyName("clipEncoder")]
        public string ClipEncoder
        {
            get => _clipEncoder;
            set
            {
                if (_clipEncoder != value)
                {
                    _clipEncoder = value;
                }
            }
        }

        [JsonPropertyName("clipQualityCrf")]
        public int ClipQualityCrf
        {
            get => _clipQualityCrf;
            set
            {
                if (_clipQualityCrf != value)
                {
                    _clipQualityCrf = value;
                }
            }
        }

        [JsonPropertyName("clipCodec")]
        public string ClipCodec
        {
            get => _clipCodec;
            set
            {
                if (_clipCodec != value)
                {
                    _clipCodec = value;
                }
            }
        }

        [JsonPropertyName("clipFps")]
        public int ClipFps
        {
            get => _clipFps;
            set
            {
                if (_clipFps != value)
                {
                    _clipFps = value;
                }
            }
        }

        [JsonPropertyName("clipAudioQuality")]
        public string ClipAudioQuality
        {
            get => _clipAudioQuality;
            set
            {
                if (_clipAudioQuality != value)
                {
                    _clipAudioQuality = value;
                }
            }
        }

        [JsonPropertyName("clipPreset")]
        public string ClipPreset
        {
            get => _clipPreset;
            set
            {
                if (_clipPreset != value)
                {
                    _clipPreset = value;
                }
            }
        }

        [JsonPropertyName("soundEffectsVolume")]
        public float SoundEffectsVolume
        {
            get => _soundEffectsVolume;
            set
            {
                if (_soundEffectsVolume != value)
                {
                    _soundEffectsVolume = value;
                    SendToFrontend("Sound effects volume changed");
                }
            }
        }

        [JsonPropertyName("showNewBadgeOnVideos")]
        public bool ShowNewBadgeOnVideos
        {
            get => _showNewBadgeOnVideos;
            set
            {
                if (_showNewBadgeOnVideos != value)
                {
                    _showNewBadgeOnVideos = value;
                }
            }
        }

        [JsonPropertyName("showGameBackground")]
        public bool ShowGameBackground
        {
            get => _showGameBackground;
            set
            {
                if (_showGameBackground != value)
                {
                    _showGameBackground = value;
                    SendToFrontend("Show game background setting changed");
                }
            }
        }

        [JsonPropertyName("showAudioWaveformInTimeline")]
        public bool ShowAudioWaveformInTimeline
        {
            get => _showAudioWaveformInTimeline;
            set
            {
                if (_showAudioWaveformInTimeline != value)
                {
                    _showAudioWaveformInTimeline = value;
                    SendToFrontend("Show audio waveform setting changed");
                }
            }
        }

        [JsonPropertyName("enableSeparateAudioTracks")]
        public bool EnableSeparateAudioTracks
        {
            get => _enableSeparateAudioTracks;
            set
            {
                if (_enableSeparateAudioTracks != value)
                {
                    _enableSeparateAudioTracks = value;
                }
            }
        }

        [JsonPropertyName("keybindings")]
        public List<Keybind> Keybindings
        {
            get => _keybindings;
            set
            {
                if (value == null || !_keybindings.SequenceEqual(value))
                {
                    _keybindings = value ?? new List<Keybind>();
                    // Check for each default keybind action and add it if missing
                    foreach (var defaultKeybind in GetDefaultKeybindings())
                    {
                        if (!_keybindings.Any(k => k.Action == defaultKeybind.Action))
                        {
                            _keybindings.Add(defaultKeybind);
                            Log.Information($"Added missing keybind for action {defaultKeybind.Action}");
                        }
                    }
                }
            }
        }
    }

    // Class definition for device settings
    public class DeviceSetting
    {
        [JsonPropertyName("id")]
        public required string Id { get; set; }
        [JsonPropertyName("name")]
        public required string Name { get; set; }
        [JsonPropertyName("volume")]
        public float Volume { get; set; } = 1.0f; // Default volume for all devices initially
    }

    // Equality comparer for DeviceSetting based on Id and Name
    public class DeviceSettingEqualityComparer : IEqualityComparer<DeviceSetting>
    {
        public bool Equals(DeviceSetting? x, DeviceSetting? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (ReferenceEquals(x, null) || ReferenceEquals(y, null))
                return false;
            return x.Id == y.Id && x.Name == y.Name && x.Volume == y.Volume;
        }

        public int GetHashCode(DeviceSetting obj)
        {
            if (ReferenceEquals(obj, null)) return 0;
            int hashId = obj.Id == null ? 0 : obj.Id.GetHashCode();
            int hashName = obj.Name == null ? 0 : obj.Name.GetHashCode();
            return hashId ^ hashName;
        }
    }

    // Enum definitions with JSON converters
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ThemeType
    {
        [EnumMember(Value = "night")]
        Night,
        [EnumMember(Value = "dark")]
        Dark
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EncoderType
    {
        [EnumMember(Value = "gpu")]
        GPU,
        [EnumMember(Value = "cpu")]
        CPU
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CodecType
    {
        [EnumMember(Value = "h264")]
        H264,
        [EnumMember(Value = "h265")]
        H265
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PresetType
    {
        [EnumMember(Value = "fast")]
        Fast,
        [EnumMember(Value = "medium")]
        Medium,
        [EnumMember(Value = "slow")]
        Slow
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ProfileType
    {
        [EnumMember(Value = "baseline")]
        Baseline,
        [EnumMember(Value = "main")]
        Main,
        [EnumMember(Value = "high")]
        High
    }

    // State class
    internal class State : IDisposable
    {
        private GpuVendor _gpuVendor = GpuVendor.Unknown;
        private PreRecording? _preRecording = null;
        private Recording? _recording = null;
        private bool _hasLoadedObs = false;
        private List<Content> _content = [];

        private List<AudioDevice> _inputDevices = [];
        private List<AudioDevice> _outputDevices = [];
        private List<Display> _displays = [];
        private List<Codec> _codecs = [];
        private bool _isCheckingForUpdates = false;

        private AudioDeviceWatcher? _deviceWatcher;

        public void Initialize()
        {
            _deviceWatcher = new();
            _deviceWatcher.DevicesChanged += UpdateAudioDevices;

            UpdateAudioDevices();
            DisplayUtils.LoadAvailableMonitorsIntoState();

            bool hasNoSelectedAudioDevices = Settings.Instance.InputDevices.Count == 0 && Settings.Instance.OutputDevices.Count == 0;
            Log.Information($"Has no selected audio devices: {hasNoSelectedAudioDevices}");
            if (hasNoSelectedAudioDevices)
            {
                SelectDefaultDevices();
            }
        }

        private static void SendToFrontend(string cause)
        {
            if (Settings.Instance != null && !Settings.Instance._isBulkUpdating)
            {
                _ = MessageUtils.SendSettingsToFrontend(cause);
            }
        }
        [JsonPropertyName("gpuVendor")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public GpuVendor GpuVendor
        {
            get => _gpuVendor;
            set
            {
                if (_gpuVendor != value)
                {
                    _gpuVendor = value;
                }
            }
        }

        [JsonPropertyName("preRecording")]
        public PreRecording? PreRecording
        {
            get => _preRecording;
            set
            {
                if (_preRecording != value)
                {
                    _preRecording = value;
                    SendToFrontend("State update: PreRecording");
                }
            }
        }

        [JsonPropertyName("recording")]
        public Recording? Recording
        {
            get => _recording;
            set
            {
                if (_recording != value)
                {
                    _recording = value;
                    SendToFrontend("State update: Recording");
                }
            }
        }

        [JsonPropertyName("hasLoadedObs")]
        public bool HasLoadedObs
        {
            get => _hasLoadedObs;
            set
            {
                if (_hasLoadedObs != value)
                {
                    _hasLoadedObs = value;
                    SendToFrontend("State update: HasLoadedObs");
                }
            }
        }

        [JsonPropertyName("content")]
        public List<Content> Content
        {
            get => _content;
            private set
            {
                if (_content != value)
                {
                    _content = value;
                    SendToFrontend("State update: Content");
                }
            }
        }

        [JsonPropertyName("inputDevices")]
        public List<AudioDevice> InputDevices
        {
            get => _inputDevices;
            set
            {
                if (_inputDevices != value)
                {
                    _inputDevices = value;
                    SendToFrontend("State update: InputDevices");
                }
            }
        }

        [JsonPropertyName("outputDevices")]
        public List<AudioDevice> OutputDevices
        {
            get => _outputDevices;
            set
            {
                if (_outputDevices != value)
                {
                    _outputDevices = value;
                    SendToFrontend("State update: OutputDevices");
                }
            }
        }

        [JsonPropertyName("displays")]
        public List<Display> Displays
        {
            get => _displays;
            set
            {
                if (_displays != value)
                {
                    _displays = value;
                    SendToFrontend("State update: Displays");
                }
            }
        }

        [JsonPropertyName("codecs")]
        public List<Codec> Codecs
        {
            get => _codecs;
            set
            {
                if (_codecs != value)
                {
                    _codecs = value;
                    SendToFrontend("State update: Codecs");
                }
            }
        }

        [JsonPropertyName("isCheckingForUpdates")]
        public bool IsCheckingForUpdates
        {
            get => _isCheckingForUpdates;
            set
            {
                if (_isCheckingForUpdates != value)
                {
                    _isCheckingForUpdates = value;
                    SendToFrontend("State update: IsCheckingForUpdates");
                }
            }
        }

        public void UpdateAudioDevices()
        {
            // Get the list of input devices
            var inputDevices = AudioDeviceUtils.GetInputDevices();
            if (!Enumerable.SequenceEqual(_inputDevices, inputDevices))
            {
                _inputDevices = inputDevices;
            }

            // Get the list of output devices
            var outputDevices = AudioDeviceUtils.GetOutputDevices();
            if (!Enumerable.SequenceEqual(_outputDevices, outputDevices))
            {
                _outputDevices = outputDevices;
            }

            Log.Information("Audio devices");
            Log.Information("-------------");
            foreach (var device in InputDevices)
            {
                Log.Information($"Input device: {device.Name} {device.Id}");
            }

            foreach (var device in OutputDevices)
            {
                Log.Information($"Output device: {device.Name} {device.Id}");
            }
            Log.Information("-------------");
            _ = MessageUtils.SendSettingsToFrontend("Updated audio devices");
        }

        private void SelectDefaultDevices()
        {
            var defaultInputDevice = _inputDevices.FirstOrDefault(d => d.IsDefault);
            if (defaultInputDevice != null)
            {
                Settings.Instance.BeginBulkUpdate();
                Settings.Instance.InputDevices.Add(new DeviceSetting
                {
                    Id = defaultInputDevice.Id,
                    Name = defaultInputDevice.Name,
                    Volume = 1.0f
                });
                Settings.Instance.EndBulkUpdateAndSaveSettings();
                Log.Information($"Auto-selected default input device: {defaultInputDevice.Name}");
            }

            var defaultOutputDevice = _outputDevices.FirstOrDefault(d => d.IsDefault);
            if (defaultOutputDevice != null)
            {
                Settings.Instance.BeginBulkUpdate();
                Settings.Instance.OutputDevices.Add(new DeviceSetting
                {
                    Id = defaultOutputDevice.Id,
                    Name = defaultOutputDevice.Name,
                    Volume = 1.0f
                });
                Settings.Instance.EndBulkUpdateAndSaveSettings();
                Log.Information($"Auto-selected default output device: {defaultOutputDevice.Name}");
            }
        }

        public void UpdateRecordingEndTime(DateTime endTime)
        {
            if (_recording != null)
            {
                _recording.EndTime = endTime;
                SendToFrontend("State update: Recording end time");
            }
        }

        public void SetContent(List<Content> contents, bool sendToFrontend)
        {
            _content = contents;
            if (sendToFrontend)
            {
                SendToFrontend("State update: Content");
            }

        }

        public void Dispose()
        {
            if (_deviceWatcher != null)
            {
                _deviceWatcher.DevicesChanged -= UpdateAudioDevices;
                _deviceWatcher.Dispose();
                _deviceWatcher = null;
            }
        }
    }

    internal class PreRecording
    {
        [JsonPropertyName("game")]
        public required string Game { get; set; }

        [JsonPropertyName("status")]
        public required string Status { get; set; }
    }

    // Recording class
    internal class Recording
    {
        [JsonPropertyName("startTime")]
        public DateTime StartTime { get; set; }

        [JsonPropertyName("endTime")]
        public DateTime? EndTime { get; set; } // Nullable in case recording is ongoing

        [JsonPropertyName("filePath")]
        public string? FilePath { get; set; } // Nullable in case recording is buffer

        [JsonPropertyName("game")]
        public required string Game { get; set; }

        [JsonPropertyName("fileName")]
        public required string FileName { get; set; }

        [JsonPropertyName("isUsingGameHook")]
        public bool IsUsingGameHook { get; set; }

        [JsonPropertyName("gameImage")]
        public string? GameImage { get; set; }

        [JsonPropertyName("bookmarks")]
        public List<Bookmark> Bookmarks { get; set; } = new List<Bookmark>();

        [JsonPropertyName("duration")]
        public TimeSpan? Duration
        {
            get
            {
                if (EndTime.HasValue)
                {
                    return EndTime.Value - StartTime;
                }
                else
                {
                    return null;
                }
            }
        }
    }

    // Content class
    public class Content
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public enum ContentType
        {
            Session,
            Buffer,
            Clip,
            Highlight
        }

        public ContentType Type { get; set; } = ContentType.Session;

        public string Title { get; set; } = string.Empty;

        public string Game { get; set; } = string.Empty;
        public List<Bookmark> Bookmarks { get; set; } = new List<Bookmark>();

        public string FileName { get; set; } = string.Empty;

        public string FilePath { get; set; } = string.Empty;

        public string FileSize { get; set; } = string.Empty;

        public long FileSizeKb { get; set; } = 0;

        public TimeSpan Duration { get; set; }

        public DateTime CreatedAt { get; set; }

        public AiAnalysis? AiAnalysis { get; set; }

        public string? UploadId { get; set; }

        // Names for the audio tracks in the recording/container.
        // Track 1 is always the mixed track ("Full Mix").
        // Subsequent tracks correspond to each configured audio source
        // in the same order they are added (inputs, then outputs), up to 6 total tracks in OBS.
        public List<string>? AudioTrackNames { get; set; }
    }

    public class AiAnalysis
    {
        public string? Id { get; set; }
    }

    internal class AudioDevice : IEquatable<AudioDevice>
    {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public bool IsDefault { get; set; }

        public bool Equals(AudioDevice? other)
        {
            if (other == null)
                return false;
            return Id == other.Id && Name == other.Name;
        }

        public override int GetHashCode()
        {
            return (Id + Name).GetHashCode();
        }
    }

    // Auth class for storing authentication tokens
    internal class Auth
    {
        private string _jwt = string.Empty;
        private string _refreshToken = string.Empty;

        [JsonPropertyName("jwt")]
        public string Jwt
        {
            get => _jwt;
            set
            {
                if (_jwt != value)
                {
                    bool hasChanged = !Settings.Instance.Auth.Jwt.Equals(value);
                    _jwt = value;

                    if (Settings.Instance != null && hasChanged && !Settings.Instance._isBulkUpdating)
                    {
                        SettingsUtils.SaveSettings();
                    }
                }
            }
        }

        [JsonPropertyName("refreshToken")]
        public string RefreshToken
        {
            get => _refreshToken;
            set
            {
                if (_refreshToken != value)
                {
                    bool hasChanged = !Settings.Instance.Auth.RefreshToken.Equals(value);
                    _refreshToken = value;
                    if (Settings.Instance != null && hasChanged && !Settings.Instance._isBulkUpdating)
                    {
                        SettingsUtils.SaveSettings();
                    }
                }
            }
        }

        public bool HasCredentials()
        {
            return !string.IsNullOrEmpty(_jwt) && !string.IsNullOrEmpty(_refreshToken);
        }
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum RecordingMode
    {
        Session,
        Buffer,
        Hybrid
    }

    public class Game
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;
    }

    public class GameEqualityComparer : IEqualityComparer<Game>
    {
        public bool Equals(Game? x, Game? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            return x.Name == y.Name && x.Path == y.Path;
        }

        public int GetHashCode(Game obj)
        {
            if (obj == null) return 0;
            return (obj.Name + obj.Path).GetHashCode();
        }
    }
}
