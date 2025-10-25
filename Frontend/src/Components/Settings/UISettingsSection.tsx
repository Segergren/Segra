import { useState } from 'react';
import { MdOutlineDescription, MdLock } from 'react-icons/md';
import { SiGithub } from 'react-icons/si';
import { GrUpdate } from 'react-icons/gr';
import { Session } from '@supabase/supabase-js';
import CloudBadge from '../CloudBadge';
import AiBadge from '../AiBadge';
import DropdownSelect from '../DropdownSelect';
import { Settings as SettingsType } from '../../Models/types';
import { sendMessageToBackend } from '../../Utils/MessageUtils';

interface UISettingsSectionProps {
  session: Session | null;
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
  openReleaseNotesModal: (version: string | null) => void;
  checkForUpdates: () => void;
}

export default function UISettingsSection({
  session,
  settings,
  updateSettings,
  openReleaseNotesModal,
  checkForUpdates,
}: UISettingsSectionProps) {
  const [draggingSoundVolume, setDraggingSoundVolume] = useState<number | null>(null);
  return (
    <>
      <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
        <h2 className="text-xl font-semibold mb-4">Segra</h2>
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
              <span className="font-medium cursor-pointer">Run on Startup</span>
            </label>
          </div>
          {!session && (
            <div className="flex items-center gap-2 text-sm text-warning">
              <MdLock className="w-4 h-4" />
              <span>Sign in to access AI features</span>
            </div>
          )}
          <div className="flex items-center">
            <label className="flex items-center gap-2">
              <input
                type="checkbox"
                name="enableAI"
                checked={settings.enableAi}
                onChange={(e) => updateSettings({ enableAi: e.target.checked })}
                className="checkbox checkbox-primary checkbox-sm"
                disabled={!session}
              />
              <span className="flex items-center gap-1 cursor-pointer">
                Enable Segra AI
                <AiBadge />
                <CloudBadge />
              </span>
            </label>
          </div>
          <div className="flex items-center">
            <label className="flex items-center gap-2">
              <input
                type="checkbox"
                name="autoGenerateHighlights"
                checked={settings.autoGenerateHighlights}
                onChange={(e) => updateSettings({ autoGenerateHighlights: e.target.checked })}
                className="checkbox checkbox-primary checkbox-sm"
                disabled={!session || !settings.enableAi}
              />
              <span className="flex items-center gap-1 cursor-pointer">
                Auto-Generate Highlights
                <AiBadge />
                <CloudBadge />
              </span>
            </label>
          </div>
          <div className="flex items-center">
            <label className="label cursor-pointer justify-start gap-2 px-0">
              <input
                type="checkbox"
                name="showGameBackground"
                checked={settings.showGameBackground}
                onChange={(e) => updateSettings({ showGameBackground: e.target.checked })}
                className="checkbox checkbox-sm checkbox-primary"
              />
              <span className="flex items-center gap-1 text-base-content">
                Show Game Cover While Recording <CloudBadge />
              </span>
            </label>
          </div>
          <div className="flex items-center">
            <label className="label cursor-pointer justify-start gap-2 px-0">
              <input
                type="checkbox"
                name="showAudioWaveformInTimeline"
                checked={settings.showAudioWaveformInTimeline}
                onChange={(e) => updateSettings({ showAudioWaveformInTimeline: e.target.checked })}
                className="checkbox checkbox-sm checkbox-primary"
              />
              <span className="flex items-center gap-1 text-base-content">
                Show Audio Waveform in Video Timeline
              </span>
            </label>
          </div>
          <div className="flex items-center">
            <label className="label cursor-pointer justify-start gap-2 px-0">
              <input
                type="checkbox"
                name="showNewBadgeOnVideos"
                checked={settings.showNewBadgeOnVideos}
                onChange={(e) => updateSettings({ showNewBadgeOnVideos: e.target.checked })}
                className="checkbox checkbox-sm checkbox-primary"
              />
              <span className="flex items-center gap-1 text-base-content">
                Show<span className="badge badge-primary badge-sm text-base-300 mx-1">NEW</span>Badge on New
                Sessions and Replay Buffers
              </span>
            </label>
          </div>
          <div className="form-control">
            <label className="label px-0 text-base-content">Sound Effects Volume</label>
            <div className="flex items-center gap-2">
              <input
                type="range"
                name="soundEffectsVolume"
                min="0"
                max="1"
                step="0.01"
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
                className="range range-xs range-primary w-48"
              />
              <span className="w-12 text-center">
                {Math.round((draggingSoundVolume ?? settings.soundEffectsVolume) * 100)}%
              </span>
            </div>
          </div>
        </div>
      </div>

      {/* Advanced Settings */}
      <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
        <h2 className="text-xl font-semibold mb-4">Advanced Settings</h2>
        <div className="bg-base-200 p-4 rounded-lg space-y-4 border border-custom">
          <div className="flex flex-col gap-3">
            <div className="flex items-center justify-between">
              <div className="flex flex-col">
                <div className="mb-1">
                  <span className="text-base-content">Update Channel</span>
                </div>
                <div className="flex items-center gap-1">
                  <div className="w-28">
                    <DropdownSelect
                      size="sm"
                      items={[
                        { value: 'stable', label: 'Stable' },
                        { value: 'beta', label: 'Beta' },
                      ]}
                      value={settings.receiveBetaUpdates ? 'beta' : 'stable'}
                      onChange={(val) => updateSettings({ receiveBetaUpdates: val === 'beta' })}
                    />
                  </div>
                  <button
                    className="btn btn-sm btn-ghost btn-circle"
                    onClick={() => checkForUpdates()}
                    disabled={settings.state.isCheckingForUpdates}
                  >
                    {settings.state.isCheckingForUpdates ? (
                      <span className="loading loading-spinner loading-md text-gray-400 loading-md" ></span>
                    ) : (
                      <GrUpdate size={18} className="text-gray-400" />
                    )}
                  </button>
                </div>
              </div>
            </div>
            <div className="flex items-center">
              <button
                onClick={() => openReleaseNotesModal(null)}
                className="btn btn-sm btn-secondary border-custom hover:border-custom text-gray-400 hover:text-gray-300 flex items-center justify-center"
              >
                <SiGithub className="text-lg shrink-0" aria-hidden="true" />
                <span className="inline-block">View Release Notes</span>
              </button>
            </div>
          </div>

          {/* OBS Version Selection */}
          <div className="flex items-center justify-between">
            <div className="flex flex-col">
              <div className="mb-1">
                <span className="text-base-content">OBS Version</span>
              </div>
              <div className="w-48">
                <DropdownSelect
                  size="sm"
                  items={[
                    { value: '', label: 'Automatic' },
                    ...settings.state.availableOBSVersions
                      .sort((a, b) => {
                        return b.version.localeCompare(a.version, undefined, { numeric: true });
                      })
                      .map((v) => ({
                        value: v.version,
                        label: `${v.version}${v.isBeta ? ' (Beta)' : ''}`,
                      })),
                  ]}
                  value={settings.selectedOBSVersion || ''}
                  onChange={(val) => updateSettings({ selectedOBSVersion: val || null })}
                />
              </div>
            </div>
          </div>
        </div>
      </div>

      {/* Version */}
      <div className="text-center mt-4 text-sm text-gray-500">
        <div className="flex flex-col items-center gap-2">
          <button
            onClick={() => sendMessageToBackend('OpenLogsLocation')}
            className="btn btn-sm btn-secondary border-custom hover:border-custom text-gray-400 hover:text-gray-300 flex items-center justify-center"
          >
            <MdOutlineDescription className="w-4 h-4 shrink-0" aria-hidden="true" />
            <span className="leading-none">View Logs</span>
          </button>
          <div>
            Segra {__APP_VERSION__ === 'Developer Preview' ? __APP_VERSION__ : 'v' + __APP_VERSION__}
          </div>
        </div>
      </div>
    </>
  );
}
