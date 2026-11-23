import type { ReactNode } from 'react';
import type { LucideIcon } from 'lucide-react';
import { Trans, useTranslation } from 'react-i18next';
import { ContentType, HotkeyAction } from '../Models/types';
import { useSettings } from '../Context/SettingsContext';
import { useSelectedMenu } from '../Context/SelectedMenuContext';
import { getKeyName } from './Settings/HotkeysSection';
import { formatBufferLength } from '../Utils/ContentFormat';
import { requestSettingsSection } from '../Utils/SettingsNavigation';
import Button from './Button';

interface EmptyLibraryStateProps {
  contentType: ContentType;
  Icon: LucideIcon;
}

interface EmptyCopy {
  title: string;
  body: ReactNode;
  action?: { label: string; section: string };
}

// Explains how recordings end up on this page, using the user's own capture mode and hotkeys
export default function EmptyLibraryState({ contentType, Icon }: EmptyLibraryStateProps) {
  const settings = useSettings();
  const { setSelectedMenu } = useSelectedMenu();
  const { t } = useTranslation('library');

  const hotkey = (action: HotkeyAction) => {
    const binding = settings.keybindings.find(
      (k) => k.action === action && k.enabled && k.keys.length > 0,
    );
    return binding ? binding.keys.map(getKeyName).join(' + ') : null;
  };

  const bufferLength = formatBufferLength(settings.replayBufferDuration);
  const recordKeys = hotkey(HotkeyAction.ToggleRecording);
  const saveKeys = hotkey(HotkeyAction.SaveReplayBuffer);
  const changeCaptureMode = { label: t('empty.changeCaptureMode'), section: 'recording' };
  const hotkeyComponents = { kbd: <kbd className="kbd kbd-sm text-gray-200" /> };
  const bufferComponents = {
    nowrap: <span className="whitespace-nowrap" />,
    kbd: <kbd className="kbd kbd-sm text-gray-200" />,
  };

  const copy = ((): EmptyCopy => {
    switch (contentType) {
      case 'Session':
        if (settings.recordingMode === 'Buffer') {
          return {
            title: t('empty.session.title'),
            body: t('empty.session.bufferBody'),
            action: changeCaptureMode,
          };
        }
        return {
          title: t('empty.session.title'),
          body: (
            <>
              {settings.autoRecordGames
                ? t('empty.session.autoRecordBody')
                : t('empty.session.manualRecordBody')}
              {recordKeys && (
                <Trans
                  ns="library"
                  i18nKey="empty.session.pressHotkey"
                  values={{ keys: recordKeys }}
                  components={hotkeyComponents}
                />
              )}
            </>
          ),
        };
      case 'Buffer':
        if (settings.recordingMode === 'Session' && !settings.alwaysOnReplayBuffer) {
          return {
            title: t('empty.buffer.title'),
            body: t('empty.buffer.offBody'),
            action: changeCaptureMode,
          };
        }
        return {
          title: t('empty.buffer.title'),
          body: saveKeys ? (
            <Trans
              ns="library"
              i18nKey="empty.buffer.keepLast"
              values={{ duration: bufferLength, keys: saveKeys }}
              components={bufferComponents}
            />
          ) : (
            <Trans
              ns="library"
              i18nKey="empty.buffer.keepLastNoHotkey"
              values={{ duration: bufferLength }}
              components={bufferComponents}
            />
          ),
          action: saveKeys
            ? undefined
            : { label: t('empty.setUpHotkeys'), section: 'hotkey-settings' },
        };
      case 'Clip':
        return {
          title: t('empty.clip.title'),
          body: t('empty.clip.body'),
        };
      case 'Highlight':
        if (!settings.enableAi) {
          return {
            title: t('empty.highlight.offTitle'),
            body: t('empty.highlight.offBody'),
            action: { label: t('empty.openHighlightSettings'), section: 'highlight-settings' },
          };
        }
        return {
          title: t('empty.highlight.title'),
          body: settings.autoGenerateHighlights
            ? t('empty.highlight.autoBody')
            : t('empty.highlight.manualBody'),
        };
    }
  })();

  return (
    <div className="flex flex-col items-center pt-16 text-center">
      <Icon size={40} strokeWidth={1.5} className="mb-4 text-gray-500" />
      <h2 className="text-lg font-semibold text-gray-200">{copy.title}</h2>
      <p className="max-w-md mt-2 text-sm leading-relaxed text-gray-400 text-balance">
        {copy.body}
      </p>
      {copy.action && (
        <Button
          variant="primary"
          size="sm"
          className="mt-5"
          onClick={() => {
            requestSettingsSection(copy.action!.section);
            setSelectedMenu('Settings');
          }}
        >
          {copy.action.label}
        </Button>
      )}
    </div>
  );
}
