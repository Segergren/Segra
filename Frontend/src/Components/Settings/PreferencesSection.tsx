import { useState } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import { VolumeX, Volume2, SkipBack, SkipForward } from 'lucide-react';
import CloudBadge from '../CloudBadge';
import DropdownSelect from '../DropdownSelect';
import RangeSlider from '../RangeSlider';
import { CloseButtonAction, Settings as SettingsType, StartupWindowMode } from '../../Models/types';
import { useAppState } from '../../Context/AppStateContext';

interface PreferencesSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function PreferencesSection({ settings, updateSettings }: PreferencesSectionProps) {
  const { t } = useTranslation('preferences');
  const appState = useAppState();
  const [draggingSoundVolume, setDraggingSoundVolume] = useState<number | null>(null);
  const soundVolume = draggingSoundVolume ?? settings.soundEffectsVolume;

  const interfaceToggles: {
    key:
      | 'showGameBackground'
      | 'showLiveSessionCard'
      | 'showNewBadgeOnVideos'
      | 'confirmBeforeDeleting'
      | 'showAudioWaveformInTimeline'
      | 'showClipToolsOnClips'
      | 'showVideoNavigation'
      | 'showBookmarksInFullscreen';
    label: React.ReactNode;
    description: React.ReactNode;
  }[] = [
    {
      key: 'showGameBackground',
      label: (
        <>
          {t('interface.toggles.showGameBackground.label')} <CloudBadge />
        </>
      ),
      description: t('interface.toggles.showGameBackground.description'),
    },
    {
      key: 'showLiveSessionCard',
      label: t('interface.toggles.showLiveSessionCard.label'),
      description: t('interface.toggles.showLiveSessionCard.description'),
    },
    {
      key: 'showNewBadgeOnVideos',
      label: t('interface.toggles.showNewBadgeOnVideos.label'),
      description: (
        <Trans
          ns="preferences"
          i18nKey="interface.toggles.showNewBadgeOnVideos.description"
          components={{
            badge: (
              <span className="badge badge-primary badge-xs text-base-300 mx-1 align-middle relative -top-[1.5px]" />
            ),
          }}
        />
      ),
    },
    {
      key: 'confirmBeforeDeleting',
      label: t('interface.toggles.confirmBeforeDeleting.label'),
      description: t('interface.toggles.confirmBeforeDeleting.description'),
    },
    {
      key: 'showAudioWaveformInTimeline',
      label: t('interface.toggles.showAudioWaveformInTimeline.label'),
      description: t('interface.toggles.showAudioWaveformInTimeline.description'),
    },
    {
      key: 'showClipToolsOnClips',
      label: t('interface.toggles.showClipToolsOnClips.label'),
      description: t('interface.toggles.showClipToolsOnClips.description'),
    },
    {
      key: 'showVideoNavigation',
      label: t('interface.toggles.showVideoNavigation.label'),
      description: (
        <Trans
          ns="preferences"
          i18nKey="interface.toggles.showVideoNavigation.description"
          components={{
            back: <SkipBack className="inline w-3.5 h-3.5 align-[-2px]" aria-hidden="true" />,
            forward: <SkipForward className="inline w-3.5 h-3.5 align-[-2px]" aria-hidden="true" />,
          }}
        />
      ),
    },
    {
      key: 'showBookmarksInFullscreen',
      label: t('interface.toggles.showBookmarksInFullscreen.label'),
      description: t('interface.toggles.showBookmarksInFullscreen.description'),
    },
  ];

  return (
    <>
      <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
        <h2 className="text-xl font-semibold mb-4">{t('app.title')}</h2>
        <div className="grid grid-cols-2 gap-4">
          <div className="form-control">
            <label className="label">
              <span className="label-text text-base-content">{t('app.runOnStartup.label')}</span>
            </label>
            <DropdownSelect
              items={[
                { value: 'Off', label: t('app.runOnStartup.off') },
                { value: 'Minimized', label: t('app.runOnStartup.minimized') },
                { value: 'Normal', label: t('app.runOnStartup.normal') },
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
              <span className="label-text text-base-content">{t('app.closeButton.label')}</span>
            </label>
            <DropdownSelect
              items={[
                {
                  value: 'Minimize',
                  // Linux has no tray; launching Segra again brings the window back
                  label:
                    appState.platform === 'linux'
                      ? t('app.closeButton.keepInBackground')
                      : t('app.closeButton.minimizeToTray'),
                },
                { value: 'Exit', label: t('app.closeButton.exit') },
              ]}
              value={settings.closeButtonAction}
              onChange={(val) => updateSettings({ closeButtonAction: val as CloseButtonAction })}
            />
          </div>
        </div>

        <label className="label mt-4">
          <span className="label-text text-base-content">
            {t('app.soundVolume')}
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
        <h2 className="text-xl font-semibold mb-4">{t('interface.title')}</h2>
        <div className="grid grid-cols-2 gap-3">
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
