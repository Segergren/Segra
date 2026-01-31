import { useState } from 'react';
import { MdVolumeOff, MdVolumeUp } from 'react-icons/md';
import CloudBadge from '../CloudBadge';
import { Settings as SettingsType } from '../../Models/types';

interface PreferencesSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function PreferencesSection({ settings, updateSettings }: PreferencesSectionProps) {
  const [draggingSoundVolume, setDraggingSoundVolume] = useState<number | null>(null);

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl font-semibold mb-4">Preferences</h2>
      <div className="bg-base-200 px-4 py-3 rounded-lg space-y-3 border border-custom">
        <div className="flex items-center">
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              name="runOnStartup"
              checked={settings.runOnStartup}
              onChange={(e) => updateSettings({ runOnStartup: e.target.checked })}
              className="checkbox checkbox-primary checkbox-sm"
            />
            <span className="cursor-pointer">Run on Startup</span>
          </label>
        </div>

        <div className="flex items-center">
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              name="showGameBackground"
              checked={settings.showGameBackground}
              onChange={(e) => updateSettings({ showGameBackground: e.target.checked })}
              className="checkbox checkbox-primary checkbox-sm"
            />
            <span className="flex items-center gap-1 cursor-pointer">
              Show Game Covers <CloudBadge />
            </span>
          </label>
        </div>

        <div className="flex items-center">
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              name="removeOriginalAfterCompression"
              checked={settings.removeOriginalAfterCompression}
              onChange={(e) => updateSettings({ removeOriginalAfterCompression: e.target.checked })}
              className="checkbox checkbox-primary checkbox-sm"
            />
            <span className="cursor-pointer">Delete Original File After Compression</span>
          </label>
        </div>

        <div className="flex items-center">
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              name="showAudioWaveformInTimeline"
              checked={settings.showAudioWaveformInTimeline}
              onChange={(e) => updateSettings({ showAudioWaveformInTimeline: e.target.checked })}
              className="checkbox checkbox-primary checkbox-sm"
            />
            <span className="cursor-pointer">Show Audio Waveform in Video Timeline</span>
          </label>
        </div>

        <div className="flex items-center">
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              name="discardSessionsWithoutBookmarks"
              checked={settings.discardSessionsWithoutBookmarks}
              onChange={(e) =>
                updateSettings({ discardSessionsWithoutBookmarks: e.target.checked })
              }
              className="checkbox checkbox-primary checkbox-sm"
            />
            <span className="cursor-pointer">
              Discard Session Recordings Without Manual Bookmarks
            </span>
          </label>
        </div>

        <div className="flex items-center">
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              name="showNewBadgeOnVideos"
              checked={settings.showNewBadgeOnVideos}
              onChange={(e) => updateSettings({ showNewBadgeOnVideos: e.target.checked })}
              className="checkbox checkbox-primary checkbox-sm"
            />
            <span className="flex items-center gap-1 cursor-pointer">
              Show<span className="badge badge-primary badge-sm text-base-300 mx-1">NEW</span>
              Badge on New Sessions and Replay Buffers
            </span>
          </label>
        </div>

        <div className="pt-3 border-t border-custom">
          <span className="text-md mb-2 block">
            Sound Effects Volume
            {draggingSoundVolume !== null && ` (${Math.round(draggingSoundVolume * 100)}%)`}
          </span>
          <div className="flex items-center gap-3">
            <MdVolumeOff className="w-4 h-4 text-gray-400 shrink-0" />
            <input
              type="range"
              name="soundEffectsVolume"
              min="0"
              max="1"
              step="0.02"
              value={draggingSoundVolume ?? settings.soundEffectsVolume}
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
              className="range range-xs range-primary w-26 [--range-fill:0]"
            />
            <MdVolumeUp className="w-4 h-4 text-gray-400 shrink-0" />
          </div>
        </div>
      </div>
    </div>
  );
}
