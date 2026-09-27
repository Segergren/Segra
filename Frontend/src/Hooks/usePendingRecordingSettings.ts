import { useAppState } from '../Context/AppStateContext';
import { useSettings } from '../Context/SettingsContext';
import { Settings } from '../Models/types';

// Settings a recording reads when it starts, grouped by the section that edits them
export const RECORDING_SETTING_GROUPS = {
  captureMode: ['recordingMode'],
  video: [
    'resolution',
    'frameRate',
    'rateControl',
    'bitrate',
    'minBitrate',
    'maxBitrate',
    'crfValue',
    'cqLevel',
    'encoder',
    'codec',
    'stretch4By3',
    'enableHdr',
    'replayBufferDuration',
    'replayBufferMaxSize',
  ],
  audio: ['inputDevices', 'outputDevices', 'enableSeparateAudioTracks', 'audioOutputMode'],
  gameIntegrations: ['gameIntegrations'],
} satisfies Record<string, (keyof Settings)[]>;

const ALL_RECORDING_SETTINGS = Object.values(RECORDING_SETTING_GROUPS).flat();

function isSameValue(a: unknown, b: unknown): boolean {
  if (a == null || b == null) return a == null && b == null;
  if (typeof a !== 'object' || typeof b !== 'object') return a === b;
  if (Array.isArray(a) !== Array.isArray(b)) return false;

  const aRecord = a as Record<string, unknown>;
  const bRecord = b as Record<string, unknown>;
  const keys = new Set([...Object.keys(aRecord), ...Object.keys(bRecord)]);
  for (const key of keys) {
    if (!isSameValue(aRecord[key], bRecord[key])) return false;
  }
  return true;
}

// True while a recording runs with values for these settings that differ from the current ones
export function usePendingRecordingSettings(
  keys: readonly (keyof Settings)[] = ALL_RECORDING_SETTINGS,
): boolean {
  const settings = useSettings();
  const startSettings = useAppState().recording?.startSettings;
  if (!startSettings) return false;

  return keys.some(
    (key) => key in startSettings && !isSameValue(settings[key], startSettings[key]),
  );
}
