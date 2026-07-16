import { useState } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { Minimize2, VolumeX, Volume2, X } from 'lucide-react';
import CloudBadge from '../CloudBadge';
import DropdownSelect from '../DropdownSelect';
import { CloseButtonAction, Settings as SettingsType, StartupWindowMode } from '../../Models/types';

interface PreferencesSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function PreferencesSection({ settings, updateSettings }: PreferencesSectionProps) {
  const [draggingSoundVolume, setDraggingSoundVolume] = useState<number | null>(null);
  // The dropdown's collapse animation needs overflow hidden, but once expanded the dropdown
  // menu must be able to overflow the row, so only reveal overflow after the animation settles.
  // Initialize from the current value: when the page loads with Run on Startup already enabled,
  // the entrance animation is skipped, so onAnimationComplete never fires to reveal overflow.
  const [startupModeOverflowVisible, setStartupModeOverflowVisible] = useState(
    settings.runOnStartup,
  );

  return (
    <div className="bg-base-300 px-4 py-3 rounded-lg space-y-3 border border-custom">
      {/* Enabled-by-default toggles */}
      <div className="flex flex-col pb-3 border-b border-custom">
        <span className="font-medium">Close Button Action</span>
        <span className="text-sm text-gray-400 mt-1">
          Choose what happens when you click the window's close (X) button.
        </span>
        <div className="inline-flex w-fit mt-3 rounded-lg border border-base-400 bg-base-300 p-1">
          <button
            type="button"
            className={`inline-flex h-8 items-center gap-2 rounded-md px-3 cursor-pointer text-sm font-semibold transition-colors ${
              settings.closeButtonAction === 'Minimize'
                ? 'bg-primary text-base-300'
                : 'text-gray-300 hover:text-primary'
            }`}
            onClick={() => updateSettings({ closeButtonAction: 'Minimize' as CloseButtonAction })}
          >
            <Minimize2 size={15} className="shrink-0" />
            <span className="leading-none">Minimize to Tray</span>
          </button>
          <button
            type="button"
            className={`inline-flex h-8 items-center gap-2 rounded-md px-3 cursor-pointer text-sm font-semibold transition-colors ${
              settings.closeButtonAction === 'Exit'
                ? 'bg-primary text-base-300'
                : 'text-gray-300 hover:text-primary'
            }`}
            onClick={() => updateSettings({ closeButtonAction: 'Exit' as CloseButtonAction })}
          >
            <X size={15} className="shrink-0" />
            <span className="leading-none">Close App</span>
          </button>
        </div>
      </div>

      <div className="flex flex-col">
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
        <AnimatePresence initial={false}>
          {settings.runOnStartup && (
            <motion.div
              key="startupWindowMode"
              initial={{ height: 0, opacity: 0 }}
              animate={{ height: 'auto', opacity: 1 }}
              exit={{ height: 0, opacity: 0 }}
              transition={{ duration: 0.2, ease: 'easeOut' }}
              onAnimationStart={() => setStartupModeOverflowVisible(false)}
              onAnimationComplete={() => setStartupModeOverflowVisible(true)}
              style={{ overflow: startupModeOverflowVisible ? 'visible' : 'hidden' }}
            >
              <div className="w-40 pt-2">
                <DropdownSelect
                  size="sm"
                  items={[
                    { value: 'Minimized', label: 'Minimized' },
                    { value: 'Normal', label: 'Normal Window' },
                  ]}
                  value={settings.startupWindowMode}
                  onChange={(val) =>
                    updateSettings({ startupWindowMode: val as StartupWindowMode })
                  }
                />
              </div>
            </motion.div>
          )}
        </AnimatePresence>
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
            name="disableWindowsGameMode"
            checked={settings.disableWindowsGameMode}
            onChange={(e) => updateSettings({ disableWindowsGameMode: e.target.checked })}
            className="checkbox checkbox-primary checkbox-sm"
          />
          <span className="cursor-pointer">Disable Windows Game Mode</span>
        </label>
      </div>

      {/* Disabled-by-default toggles */}
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
            name="discardSessionsWithoutBookmarks"
            checked={settings.discardSessionsWithoutBookmarks}
            onChange={(e) => updateSettings({ discardSessionsWithoutBookmarks: e.target.checked })}
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
          <VolumeX className="w-4 h-4 text-gray-400 shrink-0" />
          <input
            type="range"
            name="soundEffectsVolume"
            min="0"
            max="2"
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
            className="range range-xs range-primary w-48 [--range-fill:0]"
          />
          <Volume2 className="w-4 h-4 text-gray-400 shrink-0" />
        </div>
      </div>
    </div>
  );
}
