import { useState } from 'react';
import { ChevronDown, VolumeX, Volume2 } from 'lucide-react';
import CloudBadge from '../CloudBadge';
import DropdownSelect from '../DropdownSelect';
import RangeSlider from '../RangeSlider';
import { CloseButtonAction, Settings as SettingsType, StartupWindowMode } from '../../Models/types';
import { useAppState } from '../../Context/AppStateContext';

interface PreferencesSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

const SOUND_EFFECTS = [
  { value: 'start', label: 'Recording Started' },
  { value: 'error', label: 'Recording Error' },
  { value: 'bookmark', label: 'Bookmark / Replay Buffer Saved' },
];

export default function PreferencesSection({ settings, updateSettings }: PreferencesSectionProps) {
  const appState = useAppState();
  const [draggingSoundVolume, setDraggingSoundVolume] = useState<number | null>(null);
  const [showSoundEffects, setShowSoundEffects] = useState(false);
  const soundVolume = draggingSoundVolume ?? settings.soundEffectsVolume;

  const interfaceToggles: {
    key:
      | 'showGameBackground'
      | 'showAudioWaveformInTimeline'
      | 'showBookmarksInFullscreen'
      | 'showNewBadgeOnVideos'
      | 'confirmBeforeDeleting';
    label: React.ReactNode;
    description: string;
  }[] = [
    {
      key: 'showGameBackground',
      label: (
        <>
          Show Game Covers <CloudBadge />
        </>
      ),
      description: 'Shows cover art on the recording card and the game integration cards.',
    },
    {
      key: 'showAudioWaveformInTimeline',
      label: 'Show Audio Waveform',
      description: 'Draws the audio waveform along the video editor timeline.',
    },
    {
      key: 'showBookmarksInFullscreen',
      label: 'Show Bookmarks in Fullscreen',
      description: 'Shows bookmarks above the seek bar in the fullscreen video player.',
    },
    {
      key: 'showNewBadgeOnVideos',
      label: (
        <>
          Show<span className="badge badge-primary badge-sm text-base-300 mx-1">NEW</span>Badge
        </>
      ),
      description: "Marks sessions and replays from the last hour that you haven't opened yet.",
    },
    {
      key: 'confirmBeforeDeleting',
      label: 'Confirm Before Deleting',
      description: 'Asks before videos, recovered recordings or game paths are deleted.',
    },
  ];

  return (
    <>
      <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
        <h2 className="text-xl font-semibold mb-4">App</h2>
        <div className="grid grid-cols-2 gap-4">
          <div className="form-control">
            <label className="label">
              <span className="label-text text-base-content">Run on Startup</span>
            </label>
            <DropdownSelect
              items={[
                { value: 'Off', label: "Don't Start" },
                { value: 'Minimized', label: 'Start Minimized' },
                { value: 'Normal', label: 'Start as Normal Window' },
              ]}
              value={settings.runOnStartup ? settings.startupWindowMode : 'Off'}
              onChange={(val) =>
                val === 'Off'
                  ? updateSettings({ runOnStartup: false })
                  : updateSettings({
                      runOnStartup: true,
                      startupWindowMode: val as StartupWindowMode,
                    })
              }
            />
          </div>
          <div className="form-control">
            <label className="label">
              <span className="label-text text-base-content">Close Button</span>
            </label>
            <DropdownSelect
              items={[
                {
                  value: 'Minimize',
                  // Linux has no tray; launching Segra again brings the window back
                  label:
                    appState.platform === 'linux'
                      ? 'Keep Running in Background'
                      : 'Minimize to Tray',
                },
                { value: 'Exit', label: 'Close App' },
              ]}
              value={settings.closeButtonAction}
              onChange={(val) => updateSettings({ closeButtonAction: val as CloseButtonAction })}
            />
          </div>
        </div>

        <button
          type="button"
          className="label mt-4 gap-2 cursor-pointer"
          aria-expanded={showSoundEffects}
          onClick={() => setShowSoundEffects(!showSoundEffects)}
        >
          <span className="label-text text-base-content">
            Sound Effects Volume
            {draggingSoundVolume !== null && ` (${Math.round(draggingSoundVolume * 100)}%)`}
          </span>
          <ChevronDown
            className={`w-4 h-4 transition-transform ${showSoundEffects ? 'rotate-180' : ''}`}
          />
        </button>
        <div className="flex items-center gap-3">
          <VolumeX className="w-4 h-4 opacity-70 shrink-0" />
          <RangeSlider
            name="soundEffectsVolume"
            min="0"
            max="2"
            step="0.02"
            value={soundVolume}
            onChange={(e) => {
              setDraggingSoundVolume(parseFloat(e.target.value));
            }}
            onMouseDown={(e) => setDraggingSoundVolume(parseFloat(e.currentTarget.value))}
            onMouseUp={(e) => {
              updateSettings({ soundEffectsVolume: parseFloat(e.currentTarget.value) });
              setDraggingSoundVolume(null);
            }}
            onTouchEnd={() => {
              updateSettings({
                soundEffectsVolume: draggingSoundVolume ?? settings.soundEffectsVolume,
              });
              setDraggingSoundVolume(null);
            }}
            className="w-48"
          />
          <Volume2 className="w-4 h-4 opacity-70 shrink-0" />
        </div>
        {showSoundEffects && (
          <div className="mt-3 flex flex-col gap-2">
            {SOUND_EFFECTS.map(({ value, label }) => (
              <label key={value} className="flex items-center gap-3 cursor-pointer">
                <input
                  type="checkbox"
                  checked={!settings.disabledSoundEffects.includes(value)}
                  onChange={(e) =>
                    updateSettings({
                      disabledSoundEffects: e.target.checked
                        ? settings.disabledSoundEffects.filter((sound) => sound !== value)
                        : [...settings.disabledSoundEffects, value],
                    })
                  }
                  className="checkbox checkbox-primary checkbox-sm"
                />
                <span className="text-sm">{label}</span>
              </label>
            ))}
          </div>
        )}
      </div>

      <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
        <h2 className="text-xl font-semibold mb-4">Interface</h2>
        <div className="flex flex-col gap-3">
          {interfaceToggles.map(({ key, label, description }) => (
            <label
              key={key}
              className="flex items-center gap-3 cursor-pointer p-3 bg-base-200 rounded-lg border border-base-400"
            >
              <input
                type="checkbox"
                name={key}
                checked={settings[key]}
                onChange={(e) => updateSettings({ [key]: e.target.checked })}
                className="checkbox checkbox-primary checkbox-sm"
              />
              <div>
                <div className="flex items-center gap-1 font-semibold">{label}</div>
                <div className="text-sm opacity-70 mt-0.5">{description}</div>
              </div>
            </label>
          ))}
        </div>
      </div>
    </>
  );
}
