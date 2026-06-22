using Serilog;
using Segra.Backend.App;
using Segra.Backend.Core;
using System.Text.Json.Serialization;

namespace Segra.Backend.Core.Models
{
    internal class Settings
    {
        // Declared before _instance so this list exists by the time `new Settings()` runs
        // its field initializers (static fields initialize in source order).
        public static readonly List<string> KnownMenuItemIds = new List<string>
        {
            "Full Sessions",
            "Replay Buffer",
            "Clips",
            "Highlights",
            "Settings"
        };

        private static Settings _instance = new Settings();
        public static Settings Instance => _instance;
        public bool _isBulkUpdating = false;

        private string _contentFolder = Shared.PathUtils.Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Segra"));
        private string _cacheFolder = Shared.PathUtils.Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Segra"));
        private string _resolution = "1440p";
        private int _frameRate = 60;
        private bool _stretch4By3 = true;
        private bool _enableHdr = true;
        private int _bitrate = 50;
        private int _minBitrate = 35;
        private int _maxBitrate = 70;
        private string _rateControl = "VBR";
        private int _crfValue = 23;
        private int _cqLevel = 20;
        private string _encoder = "gpu";
        private Codec? _codec = null; // Set in SelectDefaultCodec()
        private string? _selectedOBSVersion = null; // null means automatic (latest non-beta)
        private bool _pendingOBSUpdate = false;
        private int _storageLimit = 100;
        private List<DeviceSetting> _inputDevices = new List<DeviceSetting>();
        private List<DeviceSetting> _outputDevices = new List<DeviceSetting>();
        private bool _forceMonoInputSources = false;
        private Display? _selectedDisplay = null;
        private DisplayCaptureMethod _displayCaptureMethod = DisplayCaptureMethod.Auto;
        private bool _enableAi = true;
        private bool _autoGenerateHighlights = true;
        private double _highlightPaddingBefore = 4;
        private double _highlightPaddingAfter = 4;
        private bool _runOnStartup = false;
        private StartupWindowMode _startupWindowMode = StartupWindowMode.Minimized;
        private CloseButtonAction _closeButtonAction = CloseButtonAction.Minimize;
        private bool _receiveBetaUpdates = false;
        private bool _airplaneMode = false;
        private RecordingMode _recordingMode = RecordingMode.Hybrid;
        private int _replayBufferDuration = 30;
        private int _replayBufferMaxSize = 1000;
        private List<Keybind> _keybindings;
        private List<Game> _whitelist = new List<Game>();
        private List<Game> _blacklist = new List<Game>();
        private Auth _auth = new Auth();
        private bool _clipClearSegmentsAfterCreatingClip = false;
        private bool _clipShowInBrowserAfterUpload = false;
        private string _clipEncoder = "cpu";
        private int _clipQualityCpu = 23; // CPU CRF: 17 (High) to 28 (Low)
        private int _clipQualityGpu = 23; // GPU (CQ/QP/ICQ): 0-1 (High) to 51 (Low)
        private string _clipCodec = "h264";
        private int _clipFps = 60; // 0 for 'Original'
        private string _clipAudioQuality = "128k";
        private string _clipPreset = "veryfast";
        private bool _clipKeepSeparateAudioTracks = false;
        private float _soundEffectsVolume = 0.5f;
        private bool _showNewBadgeOnVideos = false;
        private bool _showGameBackground = true;
        private bool _showAudioWaveformInTimeline = true;
        private bool _enableSeparateAudioTracks = false;
        private AudioOutputMode _audioOutputMode = AudioOutputMode.All;
        private bool _inputNoiseSuppression = true;
        private string _videoQualityPreset = "high";
        private string _clipQualityPreset = "standard";
        private bool _removeOriginalAfterCompression = false;
        private bool _discardSessionsWithoutBookmarks = false;
        private bool _disableWindowsGameMode = false;
        private GameIntegrations _gameIntegrations = new GameIntegrations();

        private List<MenuItemPreference> _menuItems = KnownMenuItemIds
            .Select(id => new MenuItemPreference { Id = id, Visible = true })
            .ToList();
        private string _defaultMenuItem = "Full Sessions";

        private static List<Keybind> GetDefaultKeybindings()
        {
            return new List<Keybind>
            {
                new Keybind(new List<int> { 119 }, KeybindAction.CreateBookmark, true), // 119 is F8
                new Keybind(new List<int> { 120 }, KeybindAction.ToggleRecording, true), // 120 is F9
                new Keybind(new List<int> { 121 }, KeybindAction.SaveReplayBuffer, true), // 121 is F10
                new Keybind(new List<int> { 122 }, KeybindAction.TogglePreview, true) // 122 is F11
            };
        }

        public Settings()
        {
            SetDefaultResolution();
            _keybindings = GetDefaultKeybindings();
        }

        public void BeginBulkUpdate()
        {
            _isBulkUpdating = true;
        }

        public void EndBulkUpdateAndSaveSettings(bool force = false)
        {
            _isBulkUpdating = false;
            Log.Information("End bulk update");
            SendToFrontend("End bulk update");
            SettingsService.SaveSettings(force);
        }

        private void SendToFrontend(string cause)
        {
            if (!_isBulkUpdating)
            {
                _ = MessageService.SendSettingsToFrontend(cause);
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
                string normalized = Shared.PathUtils.Normalize(value);
                bool hasChanged = Instance._contentFolder != normalized;

                _contentFolder = normalized;
                Instance._contentFolder = normalized;

                if (hasChanged || AppState.Instance.Content.Count == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        await SettingsService.LoadContentFromFolderIntoState();
                        if (Instance != null && !Instance._isBulkUpdating)
                        {
                            SettingsService.SaveSettings();
                        }
                    });
                }
            }
        }

        [JsonPropertyName("cacheFolder")]
        public string CacheFolder
        {
            get => _cacheFolder;
            set
            {
                _cacheFolder = Shared.PathUtils.Normalize(value);
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

        [JsonPropertyName("stretch4By3")]
        public bool Stretch4By3
        {
            get => _stretch4By3;
            set
            {
                _stretch4By3 = value;
            }
        }

        // When false, recordings are always SDR even if the captured display is in HDR mode
        [JsonPropertyName("enableHdr")]
        public bool EnableHdr
        {
            get => _enableHdr;
            set
            {
                _enableHdr = value;
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

        [JsonPropertyName("selectedDisplay")]
        public Display? SelectedDisplay
        {
            get => _selectedDisplay;
            set
            {
                _selectedDisplay = value;
            }
        }

        [JsonPropertyName("displayCaptureMethod")]
        public DisplayCaptureMethod DisplayCaptureMethod
        {
            get => _displayCaptureMethod;
            set
            {
                if (_displayCaptureMethod != value)
                {
                    _displayCaptureMethod = value;
                }
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

        [JsonPropertyName("highlightPaddingBefore")]
        public double HighlightPaddingBefore
        {
            get => _highlightPaddingBefore;
            set
            {
                if (_highlightPaddingBefore != value)
                {
                    _highlightPaddingBefore = value;
                }
            }
        }

        [JsonPropertyName("highlightPaddingAfter")]
        public double HighlightPaddingAfter
        {
            get => _highlightPaddingAfter;
            set
            {
                if (_highlightPaddingAfter != value)
                {
                    _highlightPaddingAfter = value;
                }
            }
        }

        [JsonPropertyName("gameIntegrations")]
        public GameIntegrations GameIntegrations
        {
            get => _gameIntegrations;
            set
            {
                _gameIntegrations = value ?? new GameIntegrations();
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
                    StartupService.SetStartupStatus(value);
                }
            }
        }

        // Whether the window opens normally or stays minimized to tray when launched from startup.
        [JsonPropertyName("startupWindowMode")]
        public StartupWindowMode StartupWindowMode
        {
            get => _startupWindowMode;
            set
            {
                if (_startupWindowMode != value)
                {
                    _startupWindowMode = value;
                }
            }
        }

        [JsonPropertyName("closeButtonAction")]
        public CloseButtonAction CloseButtonAction
        {
            get => _closeButtonAction;
            set
            {
                if (_closeButtonAction != value)
                {
                    _closeButtonAction = value;
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

        [JsonPropertyName("airplaneMode")]
        public bool AirplaneMode
        {
            get => _airplaneMode;
            set
            {
                if (_airplaneMode != value)
                {
                    _airplaneMode = value;
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
                    SettingsService.SaveSettings();
                    SendToFrontend("Whitelist changed");
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
                    SettingsService.SaveSettings();
                    SendToFrontend("Blacklist changed");
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
                }
            }
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
                }
            }
        }

        [JsonPropertyName("inputNoiseSuppression")]
        public bool InputNoiseSuppression
        {
            get => _inputNoiseSuppression;
            set
            {
                if (_inputNoiseSuppression != value)
                {
                    _inputNoiseSuppression = value;
                }
            }
        }

        [JsonPropertyName("auth")]
        public Auth Auth
        {
            get => _auth;
            set
            {
                _auth = value;
            }
        }

        [JsonPropertyName("clipClearSegmentsAfterCreatingClip")]
        public bool ClipClearSegmentsAfterCreatingClip
        {
            get => _clipClearSegmentsAfterCreatingClip;
            set
            {
                if (_clipClearSegmentsAfterCreatingClip != value)
                {
                    _clipClearSegmentsAfterCreatingClip = value;
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

        [JsonPropertyName("clipQualityCpu")]
        public int ClipQualityCpu
        {
            get => _clipQualityCpu;
            set
            {
                if (_clipQualityCpu != value)
                {
                    _clipQualityCpu = value;
                }
            }
        }

        [JsonPropertyName("clipQualityGpu")]
        public int ClipQualityGpu
        {
            get => _clipQualityGpu;
            set
            {
                if (_clipQualityGpu != value)
                {
                    _clipQualityGpu = value;
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

        [JsonPropertyName("clipKeepSeparateAudioTracks")]
        public bool ClipKeepSeparateAudioTracks
        {
            get => _clipKeepSeparateAudioTracks;
            set
            {
                if (_clipKeepSeparateAudioTracks != value)
                {
                    _clipKeepSeparateAudioTracks = value;
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

        [JsonPropertyName("audioOutputMode")]
        public AudioOutputMode AudioOutputMode
        {
            get => _audioOutputMode;
            set
            {
                if (_audioOutputMode != value)
                {
                    _audioOutputMode = value;
                }
            }
        }

        [JsonPropertyName("videoQualityPreset")]
        public string VideoQualityPreset
        {
            get => _videoQualityPreset;
            set
            {
                if (_videoQualityPreset != value)
                {
                    _videoQualityPreset = value;
                }
            }
        }

        [JsonPropertyName("clipQualityPreset")]
        public string ClipQualityPreset
        {
            get => _clipQualityPreset;
            set
            {
                if (_clipQualityPreset != value)
                {
                    _clipQualityPreset = value;
                }
            }
        }

        [JsonPropertyName("removeOriginalAfterCompression")]
        public bool RemoveOriginalAfterCompression
        {
            get => _removeOriginalAfterCompression;
            set
            {
                if (_removeOriginalAfterCompression != value)
                {
                    _removeOriginalAfterCompression = value;
                }
            }
        }

        [JsonPropertyName("discardSessionsWithoutBookmarks")]
        public bool DiscardSessionsWithoutBookmarks
        {
            get => _discardSessionsWithoutBookmarks;
            set
            {
                if (_discardSessionsWithoutBookmarks != value)
                {
                    _discardSessionsWithoutBookmarks = value;
                }
            }
        }

        // When true, Segra ensures Windows Game Mode is turned off on startup.
        // When false, Segra leaves Game Mode untouched (it never turns it back on).
        [JsonPropertyName("disableWindowsGameMode")]
        public bool DisableWindowsGameMode
        {
            get => _disableWindowsGameMode;
            set
            {
                if (_disableWindowsGameMode != value)
                {
                    _disableWindowsGameMode = value;
                }
            }
        }

        [JsonPropertyName("selectedOBSVersion")]
        public string? SelectedOBSVersion
        {
            get => _selectedOBSVersion;
            set
            {
                if (_selectedOBSVersion != value)
                {
                    _selectedOBSVersion = value;
                }
            }
        }

        [JsonPropertyName("pendingOBSUpdate")]
        public bool PendingOBSUpdate
        {
            get => _pendingOBSUpdate;
            set
            {
                if (_pendingOBSUpdate != value)
                {
                    _pendingOBSUpdate = value;
                }
            }
        }

        [JsonPropertyName("menuItems")]
        public List<MenuItemPreference> MenuItems
        {
            get => _menuItems;
            set
            {
                var incoming = value ?? new List<MenuItemPreference>();

                // Drop unknown ids and de-duplicate while preserving order
                var seen = new HashSet<string>();
                var sanitized = new List<MenuItemPreference>();
                foreach (var item in incoming)
                {
                    if (item == null || string.IsNullOrEmpty(item.Id)) continue;
                    if (!KnownMenuItemIds.Contains(item.Id)) continue;
                    if (!seen.Add(item.Id)) continue;
                    sanitized.Add(new MenuItemPreference
                    {
                        Id = item.Id,
                        Visible = item.Id == "Settings" ? true : item.Visible
                    });
                }

                // Append any known ids that were missing (e.g., new menu items added in a later version)
                foreach (var id in KnownMenuItemIds)
                {
                    if (!seen.Contains(id))
                    {
                        sanitized.Add(new MenuItemPreference
                        {
                            Id = id,
                            Visible = true
                        });
                    }
                }

                _menuItems = sanitized;
            }
        }

        [JsonPropertyName("defaultMenuItem")]
        public string DefaultMenuItem
        {
            get => _defaultMenuItem;
            set
            {
                if (string.IsNullOrEmpty(value) || !KnownMenuItemIds.Contains(value))
                {
                    _defaultMenuItem = "Full Sessions";
                    return;
                }
                _defaultMenuItem = value;
            }
        }

        [JsonPropertyName("keybindings")]
        public List<Keybind> Keybindings
        {
            get => _keybindings;
            set
            {
                _keybindings = value ?? GetDefaultKeybindings();

                // Ensure all default actions exist
                foreach (var defaultKeybind in GetDefaultKeybindings())
                {
                    if (!_keybindings.Any(k => k.Action == defaultKeybind.Action))
                    {
                        _keybindings.Add(defaultKeybind);
                    }
                }
            }
        }
    }

    public class MenuItemPreference
    {
        [JsonPropertyName("id")]
        public required string Id { get; set; }

        [JsonPropertyName("visible")]
        public bool Visible { get; set; } = true;
    }

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

    internal class PreRecording
    {
        [JsonPropertyName("game")]
        public required string Game { get; set; }

        [JsonPropertyName("status")]
        public required string Status { get; set; }

        [JsonPropertyName("coverImageId")]
        public string? CoverImageId { get; set; }

        [JsonPropertyName("pid")]
        public int? Pid { get; set; }

        [JsonPropertyName("exe")]
        public string? Exe { get; set; }
    }

    internal class Recording
    {
        private readonly object _bookmarksLock = new();

        [JsonPropertyName("startTime")]
        public DateTime StartTime { get; set; }

        [JsonPropertyName("endTime")]
        public DateTime? EndTime { get; set; } // Nullable in case recording is ongoing

        private string? _filePath;
        [JsonPropertyName("filePath")]
        public string? FilePath
        {
            get => _filePath;
            set => _filePath = Segra.Backend.Shared.PathUtils.NormalizeOrNull(value);
        } // Nullable in case recording is buffer

        [JsonPropertyName("game")]
        public required string Game { get; set; }

        [JsonPropertyName("fileName")]
        public required string FileName { get; set; }

        [JsonPropertyName("pid")]
        public int? Pid { get; set; }

        [JsonPropertyName("isUsingGameHook")]
        public bool IsUsingGameHook { get; set; }

        [JsonPropertyName("exePath")]
        public string? ExePath { get; set; }

        [JsonPropertyName("bookmarks")]
        public List<Bookmark> Bookmarks { get; set; } = new List<Bookmark>();

        [JsonPropertyName("coverImageId")]
        public string? CoverImageId { get; set; }

        [JsonPropertyName("audioTrackNames")]
        public List<string>? AudioTrackNames { get; set; }

        public void AddBookmark(Bookmark bookmark)
        {
            lock (_bookmarksLock)
            {
                Bookmarks.Add(bookmark);
            }
            AppState.Instance.NotifyRecordingUpdated();
        }

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

    public class Content
    {
        private readonly object _bookmarksLock = new();

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

        public void AddBookmark(Bookmark bookmark)
        {
            lock (_bookmarksLock)
            {
                Bookmarks.Add(bookmark);
            }
            AppState.Instance.NotifyContentUpdated();
        }

        public string FileName { get; set; } = string.Empty;

        private string _filePath = string.Empty;
        // Paths are stored with forward slashes so logs, metadata, and the WebSocket
        // payload to the frontend stay consistent regardless of how the path was built.
        public string FilePath
        {
            get => _filePath;
            set => _filePath = Segra.Backend.Shared.PathUtils.Normalize(value ?? string.Empty);
        }

        public string FileSize { get; set; } = string.Empty;

        public long FileSizeKb { get; set; } = 0;

        public TimeSpan Duration { get; set; }

        public DateTime CreatedAt { get; set; }

        public AiAnalysis? AiAnalysis { get; set; }

        public string? UploadId { get; set; }

        public int? IgdbId { get; set; }

        // Names for the audio tracks in the recording/container.
        // Track 1 is always the mixed track ("Full Mix").
        // Subsequent tracks correspond to each configured audio source
        // in the same order they are added (inputs, then outputs), up to 6 total tracks in OBS.
        public List<string>? AudioTrackNames { get; set; }

        public bool IsImported { get; set; } = false;
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
                        SettingsService.SaveSettings();
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
                        SettingsService.SaveSettings();
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

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum StartupWindowMode
    {
        Normal,
        Minimized
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CloseButtonAction
    {
        Minimize,
        Exit
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DisplayCaptureMethod
    {
        Auto,
        DXGI,
        WGC
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AudioOutputMode
    {
        All,
        GameOnly,
        GameAndDiscord
    }

    public class Game
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("paths")]
        public List<string> Paths { get; set; } = new List<string>();

        // TODO: Remove this property after migration is deployed and users have upgraded
        [JsonPropertyName("path")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string Path { get; set; } = string.Empty;
    }

    public class GameEqualityComparer : IEqualityComparer<Game>
    {
        public bool Equals(Game? x, Game? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;

            // Games are equal if they have the same name and at least one common path
            if (x.Name != y.Name) return false;
            return x.Paths.Intersect(y.Paths, StringComparer.OrdinalIgnoreCase).Any();
        }

        public int GetHashCode(Game obj)
        {
            if (obj == null) return 0;
            // Use name for hash code since paths can vary
            return obj.Name.GetHashCode();
        }
    }

    // Game integration settings - each game has its own settings object
    public class GameIntegrationSettings
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        public GameIntegrationSettings(bool enabled = true)
        {
            Enabled = enabled;
        }

        public GameIntegrationSettings() : this(true) { }
    }

    public class GameIntegrations
    {
        [JsonPropertyName("counterStrike2")]
        public GameIntegrationSettings CounterStrike2 { get; set; } = new GameIntegrationSettings(true);

        [JsonPropertyName("leagueOfLegends")]
        public GameIntegrationSettings LeagueOfLegends { get; set; } = new GameIntegrationSettings(true);

        [JsonPropertyName("pubg")]
        public GameIntegrationSettings Pubg { get; set; } = new GameIntegrationSettings(true);

        [JsonPropertyName("rocketLeague")]
        public GameIntegrationSettings RocketLeague { get; set; } = new GameIntegrationSettings(true);

        [JsonPropertyName("dota2")]
        public GameIntegrationSettings Dota2 { get; set; } = new GameIntegrationSettings(true);

        [JsonPropertyName("rust")]
        public GameIntegrationSettings Rust { get; set; } = new GameIntegrationSettings(true);

        [JsonPropertyName("minecraft")]
        public GameIntegrationSettings Minecraft { get; set; } = new GameIntegrationSettings(true);

        [JsonPropertyName("runescapeDragonwilds")]
        public GameIntegrationSettings RunescapeDragonwilds { get; set; } = new GameIntegrationSettings(true);

        [JsonPropertyName("warThunder")]
        public GameIntegrationSettings WarThunder { get; set; } = new GameIntegrationSettings(true);

        [JsonPropertyName("gta")]
        public GameIntegrationSettings Gta { get; set; } = new GameIntegrationSettings(true);
    }
}
