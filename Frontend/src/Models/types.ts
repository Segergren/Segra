export type ContentType = 'Session' | 'Buffer' | 'Clip' | 'Highlight';

export type RecordingMode = 'Session' | 'Buffer' | 'Hybrid';

export interface Content {
  type: ContentType;
  title: string;
  game: string;
  bookmarks: Bookmark[];
  fileName: string;
  filePath: string;
  fileSize: string;
  fileSizeKb: number;
  duration: string;
  createdAt: string;
  uploadId?: string;
  igdbId?: number;
}

export interface OBSVersion {
  version: string;
  isBeta: boolean;
  availableSince?: string;
  supportsFrom?: string;
  supportsTo?: string;
  url: string;
}

export interface State {
  gpuVendor: GpuVendor;
  preRecording?: PreRecording;
  recording?: Recording;
  hasLoadedObs: boolean;
  content: Content[];
  inputDevices: AudioDevice[];
  outputDevices: AudioDevice[];
  displays: Display[];
  codecs: Codec[];
  availableOBSVersions: OBSVersion[];
  isCheckingForUpdates: boolean;
  gameList: GameListEntry[];
  maxDisplayHeight: number;
  currentFolderSizeGb: number;
}

export enum GpuVendor {
  Unknown = 'Unknown',
  Nvidia = 'Nvidia',
  AMD = 'AMD',
  Intel = 'Intel',
}

export enum BookmarkType {
  Manual = 'Manual',
  Kill = 'Kill',
  Assist = 'Assist',
  Death = 'Death',
}

export enum BookmarkSubtype {
  Headshot = 'Headshot',
}

export enum KeybindAction {
  CreateBookmark = 'CreateBookmark',
  SaveReplayBuffer = 'SaveReplayBuffer',
}

export interface Keybind {
  keys: number[];
  action: KeybindAction;
  enabled: boolean;
}

export interface Bookmark {
  id: number;
  type: BookmarkType;
  subtype?: BookmarkSubtype;
  time: string;
}

export interface Recording {
  startTime: Date;
  endTime: Date;
  game: string;
  isUsingGameHook: boolean;
  gameImage?: string; // Base64 encoded image of the game executable icon
}

export interface PreRecording {
  game: string;
  status: string;
}

export interface AudioDevice {
  id: string;
  name: string;
  isDefault?: boolean;
}

export interface DeviceSetting {
  id: string;
  name: string;
  volume: number; // Volume from 0.0 to 1.0
}

export interface Display {
  deviceId: string;
  deviceName: string;
  isPrimary: boolean;
}

export interface Codec {
  friendlyName: string;
  internalEncoderId: string;
  isHardwareEncoder: boolean;
}

export interface Game {
  name: string;
  paths?: string[];
}

export interface GameListEntry {
  name: string;
  executables: string[];
}

export type ClipEncoder = 'gpu' | 'cpu';
export type ClipCodec = 'h264' | 'h265' | 'av1';
export type ClipFPS = 0 | 24 | 30 | 60 | 120 | 144;
export type ClipAudioQuality = '96k' | '128k' | '192k' | '256k' | '320k';
export type CpuClipPreset =
  | 'ultrafast'
  | 'superfast'
  | 'veryfast'
  | 'faster'
  | 'fast'
  | 'medium'
  | 'slow'
  | 'slower'
  | 'veryslow';
export type NvidiaClipPreset =
  | 'slow'
  | 'medium'
  | 'fast'
  | 'hp'
  | 'hq'
  | 'bd'
  | 'll'
  | 'llhq'
  | 'llhp'
  | 'lossless'
  | 'losslesshp';
export type Av1NvencPreset = 'p1' | 'p2' | 'p3' | 'p4' | 'p5' | 'p6' | 'p7';
export type AmdClipPreset = 'quality' | 'transcoding' | 'lowlatency' | 'ultralowlatency';
export type IntelClipPreset = 'fast' | 'medium' | 'slow';
export type ClipPreset = CpuClipPreset | NvidiaClipPreset | Av1NvencPreset | AmdClipPreset | IntelClipPreset;

export type VideoQualityPreset = 'low' | 'standard' | 'high' | 'custom';
export type ClipQualityPreset = 'low' | 'standard' | 'high' | 'custom';

export interface Settings {
  theme:
    | 'segra'
    | 'rich'
    | 'dark'
    | 'night'
    | 'dracula'
    | 'black'
    | 'luxury'
    | 'forest'
    | 'halloween'
    | 'coffee'
    | 'dim'
    | 'sunset';
  resolution: '720p' | '1080p' | '1440p' | '4K';
  frameRate: number;
  rateControl: string;
  crfValue: number;
  cqLevel: number;
  bitrate: number;
  minBitrate: number; // VBR only (Mbps)
  maxBitrate: number; // VBR only (Mbps)
  encoder: 'gpu' | 'cpu';
  codec: Codec | null;
  storageLimit: number;
  contentFolder: string;
  inputDevices: DeviceSetting[];
  outputDevices: DeviceSetting[];
  forceMonoInputSources: boolean;
  enableDisplayRecording: boolean;
  selectedDisplay: Display | null;
  recordWindowedApplications: boolean;
  selectedOBSVersion: string | null; // null means automatic (latest non-beta)
  enableAi: boolean;
  autoGenerateHighlights: boolean;
  runOnStartup: boolean;
  receiveBetaUpdates: boolean;
  recordingMode: RecordingMode;
  replayBufferDuration: number; // in seconds
  replayBufferMaxSize: number; // in MB
  clipClearSelectionsAfterCreatingClip: boolean;
  clipShowInBrowserAfterUpload: boolean; // Open browser after upload
  clipEncoder: ClipEncoder;
  clipQualityCpu: number; // CPU CRF: 17 (High) to 28 (Low)
  clipQualityGpu: number; // GPU (CQ/QP/ICQ): 0-1 (High) to 51 (Low)
  clipCodec: ClipCodec;
  clipFps: ClipFPS;
  clipAudioQuality: ClipAudioQuality;
  clipPreset: ClipPreset;
  keybindings: Keybind[];
  whitelist: Game[];
  blacklist: Game[];
  soundEffectsVolume: number; // Volume for UI sound effects (0.0 to 1.0)
  showNewBadgeOnVideos: boolean;
  showGameBackground: boolean; // Show game background while recording
  showAudioWaveformInTimeline: boolean; // Show audio waveform in video timeline
  enableSeparateAudioTracks: boolean; // Advanced: per-source audio tracks
  videoQualityPreset: VideoQualityPreset;
  clipQualityPreset: ClipQualityPreset;
  state: State;
}

export const initialState: State = {
  gpuVendor: GpuVendor.Unknown,
  recording: undefined,
  hasLoadedObs: false,
  content: [],
  inputDevices: [],
  outputDevices: [],
  displays: [],
  codecs: [],
  availableOBSVersions: [],
  isCheckingForUpdates: false,
  gameList: [],
  maxDisplayHeight: 1080,
  currentFolderSizeGb: 0,
};

export const initialSettings: Settings = {
  theme: 'segra',
  resolution: '720p',
  frameRate: 30,
  rateControl: 'CBR',
  crfValue: 23,
  cqLevel: 20,
  bitrate: 10,
  minBitrate: 10,
  maxBitrate: 15,
  encoder: 'gpu',
  codec: null,
  storageLimit: 100,
  contentFolder: '',
  inputDevices: [],
  outputDevices: [],
  forceMonoInputSources: false,
  enableDisplayRecording: true,
  selectedDisplay: null, // Default to null (auto-select)
  recordWindowedApplications: false,
  selectedOBSVersion: null, // null means automatic (latest non-beta)
  enableAi: true,
  autoGenerateHighlights: true,
  runOnStartup: false,
  receiveBetaUpdates: false,
  recordingMode: 'Hybrid',
  replayBufferDuration: 30,
  replayBufferMaxSize: 1000,
  clipClearSelectionsAfterCreatingClip: false,
  clipShowInBrowserAfterUpload: false,
  clipEncoder: 'cpu',
  clipQualityCpu: 23,
  clipQualityGpu: 23,
  clipCodec: 'h264',
  clipFps: 60,
  clipAudioQuality: '128k',
  clipPreset: 'veryfast',
  soundEffectsVolume: 1,
  showNewBadgeOnVideos: false,
  showGameBackground: true,
  showAudioWaveformInTimeline: true,
  enableSeparateAudioTracks: false,
  videoQualityPreset: 'high',
  clipQualityPreset: 'standard',
  keybindings: [
    { keys: [119], action: KeybindAction.CreateBookmark, enabled: true }, // 119 is F8
    { keys: [121], action: KeybindAction.SaveReplayBuffer, enabled: true }, // 121 is F10
  ],
  whitelist: [],
  blacklist: [],
  state: initialState,
};

export interface Selection {
  id: number;
  type: ContentType;
  startTime: number;
  endTime: number;
  thumbnailDataUrl?: string;
  isLoading: boolean;
  fileName: string;
  game?: string;
  title?: string;
  igdbId?: number;
}

export interface SelectionCardProps {
  selection: Selection;
  index: number;
  moveCard: (dragIndex: number, hoverIndex: number) => void;
  formatTime: (time: number) => string;
  isHovered: boolean;
  setHoveredSelectionId: (id: number | null) => void;
  removeSelection: (id: number) => void;
}

export interface AiProgress {
  id: string;
  progress: number;
  status: 'processing' | 'done';
  message: string;
  content: Content;
}

export interface GameResponse {
  game: {
    id: number;
    name: string;
    cover?: {
      id: number;
      image_id: string;
    };
  };
}
