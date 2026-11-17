import type { ReactNode } from 'react';
import type { LucideIcon } from 'lucide-react';
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

  const hotkey = (action: HotkeyAction) => {
    const binding = settings.keybindings.find(
      (k) => k.action === action && k.enabled && k.keys.length > 0,
    );
    return binding ? (
      <kbd className="kbd kbd-sm text-gray-200">{binding.keys.map(getKeyName).join(' + ')}</kbd>
    ) : null;
  };

  const bufferLength = formatBufferLength(settings.replayBufferDuration);
  const recordHotkey = hotkey(HotkeyAction.ToggleRecording);
  const saveHotkey = hotkey(HotkeyAction.SaveReplayBuffer);
  const changeCaptureMode = { label: 'Change capture mode', section: 'recording' };

  const copy = ((): EmptyCopy => {
    switch (contentType) {
      case 'Session':
        if (settings.recordingMode === 'Buffer') {
          return {
            title: 'No sessions yet',
            body: 'Your capture mode is Replay Buffer, so games are not saved as full sessions. Moments you save show up under Replay Buffer.',
            action: changeCaptureMode,
          };
        }
        return {
          title: 'No sessions yet',
          body: (
            <>
              {settings.autoRecordGames
                ? 'Start a game and Segra records it in the background. The whole session shows up here when you close the game.'
                : 'Auto-record is off, so a game is only recorded when you start the recording yourself.'}
              {recordHotkey && <> Press {recordHotkey} to start or stop a recording.</>}
            </>
          ),
        };
      case 'Buffer':
        if (settings.recordingMode === 'Session' && !settings.alwaysOnReplayBuffer) {
          return {
            title: 'No replay buffers yet',
            body: 'The replay buffer is off. Switch your capture mode to Replay Buffer or Hybrid to save the last moments of a game with a hotkey.',
            action: changeCaptureMode,
          };
        }
        return {
          title: 'No replay buffers yet',
          body: saveHotkey ? (
            <>
              While you play, Segra keeps the last{' '}
              <span className="whitespace-nowrap">{bufferLength}</span> in memory. Press{' '}
              {saveHotkey} to save it here.
            </>
          ) : (
            `While you play, Segra keeps the last ${bufferLength} in memory. Turn on the Save Replay Buffer hotkey to save it here.`
          ),
          action: saveHotkey ? undefined : { label: 'Set up hotkeys', section: 'hotkey-settings' },
        };
      case 'Clip':
        return {
          title: 'No clips yet',
          body: 'Open a recording, press Add Segment to mark the moment you want, then press Create Clip.',
        };
      case 'Highlight':
        if (!settings.enableAi) {
          return {
            title: 'Highlights are off',
            body: 'Turn them on to join the kills and goals from a match into one video.',
            action: { label: 'Open highlight settings', section: 'highlight-settings' },
          };
        }
        return {
          title: 'No highlights yet',
          body: settings.autoGenerateHighlights
            ? "In supported games, Segra bookmarks your kills and goals and joins them into one video when the recording ends. You can also choose Create Highlight in a session's menu."
            : "Choose Create Highlight in a session's menu to join its kills and goals into one video.",
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
