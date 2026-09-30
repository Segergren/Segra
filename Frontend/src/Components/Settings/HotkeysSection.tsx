import { useState, useEffect } from 'react';
import { Settings as SettingsType, HotkeyAction } from '../../Models/types';
interface HotkeysSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export const getKeyName = (keyCode: number): string => {
  // Function keys F1-F24
  if (keyCode >= 112 && keyCode <= 135) return `F${keyCode - 111}`;

  // Special keys
  const keyMap: Record<number, string> = {
    8: 'Backspace',
    9: 'Tab',
    13: 'Enter',
    16: 'Shift',
    17: 'Ctrl',
    18: 'Alt',
    27: 'Esc',
    32: 'Space',
    33: 'PgUp',
    34: 'PgDn',
    35: 'End',
    36: 'Home',
    37: '←',
    38: '↑',
    39: '→',
    40: '↓',
    45: 'Insert',
    46: 'Delete',
    91: 'Win',
    144: 'Num Lock',
    186: ';',
    187: '=',
    188: ',',
    189: '-',
    190: '.',
    191: '/',
    192: '`',
    219: '[',
    220: '\\',
    221: ']',
    222: "'",
  };

  if (keyCode >= 48 && keyCode <= 57) return String.fromCharCode(keyCode); // 0-9
  if (keyCode >= 65 && keyCode <= 90) return String.fromCharCode(keyCode); // A-Z

  return keyMap[keyCode] || `Key(${keyCode})`;
};

const getActionLabel = (action: HotkeyAction): string => {
  switch (action) {
    case HotkeyAction.CreateBookmark:
      return 'Create Bookmark';
    case HotkeyAction.SaveReplayBuffer:
      return 'Save Replay Buffer';
    case HotkeyAction.ToggleRecording:
      return 'Start / Stop Recording';
    case HotkeyAction.TogglePreview:
      return 'Toggle Recording Preview';
    default:
      return action;
  }
};

const getActionHint = (action: HotkeyAction): string | null => {
  switch (action) {
    case HotkeyAction.ToggleRecording:
      return 'Records your display, or stops any active recording.';
    default:
      return null;
  }
};

// Moment-saving actions first, recording controls second
const ACTION_ORDER: HotkeyAction[] = [
  HotkeyAction.SaveReplayBuffer,
  HotkeyAction.CreateBookmark,
  HotkeyAction.ToggleRecording,
  HotkeyAction.TogglePreview,
];

const getActionRank = (action: HotkeyAction): number => {
  const rank = ACTION_ORDER.indexOf(action);
  return rank === -1 ? ACTION_ORDER.length : rank;
};

export default function HotkeysSection({ settings, updateSettings }: HotkeysSectionProps) {
  const [capturing, setCapturing] = useState<number | null>(null);
  const [pressedKeys, setPressedKeys] = useState<number[]>([]);

  useEffect(() => {
    if (capturing === null) return;

    const handleKeyDown = (e: KeyboardEvent) => {
      e.preventDefault();

      const keys: number[] = [];
      if (e.ctrlKey) keys.push(17);
      if (e.altKey) keys.push(18);
      if (e.shiftKey) keys.push(16);

      // Add the main key if it's not a modifier
      if (e.keyCode !== 16 && e.keyCode !== 17 && e.keyCode !== 18) {
        keys.push(e.keyCode);
      }

      setPressedKeys(keys);
    };

    const handleKeyUp = (e: KeyboardEvent) => {
      e.preventDefault();

      // Cancel on Escape
      if (e.keyCode === 27) {
        setCapturing(null);
        setPressedKeys([]);
        return;
      }

      // Save hotkey if we have keys and released a non-modifier key
      if (pressedKeys.length > 0 && e.keyCode !== 16 && e.keyCode !== 17 && e.keyCode !== 18) {
        const updatedHotkeys = [...settings.keybindings];
        updatedHotkeys[capturing] = {
          ...updatedHotkeys[capturing],
          keys: pressedKeys,
        };
        updateSettings({ keybindings: updatedHotkeys });
        setCapturing(null);
        setPressedKeys([]);
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    window.addEventListener('keyup', handleKeyUp);

    return () => {
      window.removeEventListener('keydown', handleKeyDown);
      window.removeEventListener('keyup', handleKeyUp);
    };
  }, [capturing, pressedKeys, settings.keybindings, updateSettings]);

  // Indices stay those of settings.keybindings so updates hit the right entry
  const orderedHotkeys = settings.keybindings
    .map((hotkey, index) => ({ hotkey, index }))
    .sort((a, b) => getActionRank(a.hotkey.action) - getActionRank(b.hotkey.action));

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
      <h2 className="text-xl font-semibold mb-4">Hotkeys</h2>
      <div className="flex flex-col gap-3">
        {orderedHotkeys.map(({ hotkey, index }) => (
          <div
            key={hotkey.action}
            className="flex items-center justify-between gap-3 bg-base-200 rounded-lg p-3 border border-base-400"
          >
            <label className="flex items-center gap-3 cursor-pointer">
              <input
                type="checkbox"
                checked={hotkey.enabled}
                onChange={(e) => {
                  const updatedHotkeys = [...settings.keybindings];
                  updatedHotkeys[index] = {
                    ...updatedHotkeys[index],
                    enabled: e.target.checked,
                  };
                  updateSettings({ keybindings: updatedHotkeys });
                }}
                className="checkbox checkbox-primary checkbox-sm"
              />
              <div>
                <div className="font-semibold">{getActionLabel(hotkey.action)}</div>
                {getActionHint(hotkey.action) && (
                  <div className="text-sm opacity-70 mt-0.5">{getActionHint(hotkey.action)}</div>
                )}
              </div>
            </label>

            <button
              className={`kbd kbd-md cursor-pointer min-w-[120px] transition-all hover:bg-base-300 outline-none h-10 ${capturing === index ? 'animate-pulse bg-base-300' : ''}`}
              onClick={() => {
                setCapturing(index);
                setPressedKeys([]);
              }}
            >
              {capturing === index
                ? 'Press Keys...'
                : hotkey.keys.map((key) => getKeyName(key)).join(' + ')}
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}
