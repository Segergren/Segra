import { useState } from 'react';
import { VolumeX, Volume2 } from 'lucide-react';
import CloudBadge from '../CloudBadge';
import DropdownSelect from '../DropdownSelect';
import RangeSlider from '../RangeSlider';
import { CloseButtonAction, Settings as SettingsType, StartupWindowMode } from '../../Models/types';

interface PreferencesSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function PreferencesSection({ settings, updateSettings }: PreferencesSectionProps) {
  const [draggingSoundVolume, setDraggingSoundVolume] = useState<number | null>(null);
  const soundVolume = draggingSoundVolume ?? settings.soundEffectsVolume;

  const interfaceToggles: {
    key:
      | 'showGameBackground'
      | 'showAudioWaveformInTimeline'
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
                { value: 'Minimize', label: 'Minimize to Tray' },
                { value: 'Exit', label: 'Close App' },
              ]}
              value={settings.closeButtonAction}
              onChange={(val) => updateSettings({ closeButtonAction: val as CloseButtonAction })}
            />
          </div>
        </div>

        <label className="label mt-4">
          <span className="label-text text-base-content">
            Sound Effects Volume
            {draggingSoundVolume !== null && ` (${Math.round(draggingSoundVolume * 100)}%)`}
          </span>
        </label>
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
