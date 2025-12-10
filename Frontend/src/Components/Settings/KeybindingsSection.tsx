import { useState, useEffect } from 'react';
import { Settings as SettingsType, KeybindAction } from '../../Models/types';
interface KeybindingsSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

const getKeyName = (keyCode: number): string => {
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

const getActionLabel = (action: KeybindAction): string => {
  return action === KeybindAction.CreateBookmark ? 'Create Bookmark' : 'Save Replay Buffer';
};

export default function KeybindingsSection({ settings, updateSettings }: KeybindingsSectionProps) {
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

      // Save keybind if we have keys and released a non-modifier key
      if (pressedKeys.length > 0 && e.keyCode !== 16 && e.keyCode !== 17 && e.keyCode !== 18) {
        const updatedKeybindings = [...settings.keybindings];
        updatedKeybindings[capturing] = {
          ...updatedKeybindings[capturing],
          keys: pressedKeys,
        };
        updateSettings({ keybindings: updatedKeybindings });
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

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl font-semibold mb-4">Keybindings</h2>
      <div className="space-y-2">
        {settings.keybindings.map((keybind, index) => (
          <div
            key={index}
            className="flex items-center justify-between bg-base-200 rounded-lg py-2 px-3 border border-base-400 w-1/2"
          >
            <label className="flex items-center gap-3 cursor-pointer">
              <input
                type="checkbox"
                checked={keybind.enabled}
                onChange={(e) => {
                  const updatedKeybindings = [...settings.keybindings];
                  updatedKeybindings[index] = {
                    ...updatedKeybindings[index],
                    enabled: e.target.checked,
                  };
                  updateSettings({ keybindings: updatedKeybindings });
                }}
                className="checkbox checkbox-primary"
              />
              <span className="font-medium">{getActionLabel(keybind.action)}</span>
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
                : keybind.keys.map((key) => getKeyName(key)).join(' + ')}
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}
