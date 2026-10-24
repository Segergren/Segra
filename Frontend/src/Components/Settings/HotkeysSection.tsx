import { useState, useEffect, useRef } from 'react';
import { Settings as SettingsType, HotkeyAction } from '../../Models/types';
import { sendMessageToBackend } from '../../Utils/MessageUtils';
import { useAppState } from '../../Context/AppStateContext';
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

const VK_SHIFT = 16;
const VK_CONTROL = 17;
const VK_ALT = 18;
const VK_ESCAPE = 27;

// Combined with one other key, in the order they are saved and shown
const MODIFIER_KEYS = [VK_CONTROL, VK_ALT, VK_SHIFT];

const isModifierKey = (keyCode: number) => MODIFIER_KEYS.includes(keyCode);

// Chromium on Windows reports the AltGr key as AltGraph instead of Ctrl and Alt, while Windows and
// so the hotkey itself see Ctrl+Alt. On Linux AltGr is just the right Alt key.
const getHeldModifiers = (e: KeyboardEvent, altGraphIsCtrlAlt: boolean): number[] => {
  const altGraph = e.getModifierState('AltGraph');
  const keys: number[] = [];
  if (e.ctrlKey || (altGraph && altGraphIsCtrlAlt)) keys.push(VK_CONTROL);
  if (e.altKey || altGraph) keys.push(VK_ALT);
  if (e.shiftKey) keys.push(VK_SHIFT);
  return keys;
};

const formatKeys = (keys: number[]) => keys.map((key) => getKeyName(key)).join(' + ');

const getCaptureLabel = (keys: number[]) => {
  if (keys.length === 0) return 'Press Keys...';
  // Modifiers alone are still waiting for the key they go with
  return keys.every(isModifierKey) ? `${formatKeys(keys)} + ...` : formatKeys(keys);
};

export default function HotkeysSection({ settings, updateSettings }: HotkeysSectionProps) {
  const { platform } = useAppState();
  const [capturing, setCapturing] = useState<number | null>(null);
  const [pressedKeys, setPressedKeys] = useState<number[]>([]);
  // The key handlers read this between renders
  const pressedKeysRef = useRef<number[]>([]);
  const isCapturing = capturing !== null;

  // Global hotkeys also see keys pressed here
  useEffect(() => {
    if (!isCapturing) return;
    sendMessageToBackend('SetHotkeysPaused', { paused: true });
    return () => sendMessageToBackend('SetHotkeysPaused', { paused: false });
  }, [isCapturing]);

  useEffect(() => {
    if (capturing === null) return;

    const altGraphIsCtrlAlt = platform === 'windows';
    const updateKeys = (keys: number[]) => {
      pressedKeysRef.current = keys;
      setPressedKeys(keys);
    };

    const handleKeyDown = (e: KeyboardEvent) => {
      e.preventDefault();
      if (e.keyCode === VK_ESCAPE) return;

      const keys = pressedKeysRef.current;
      const modifiers = getHeldModifiers(e, altGraphIsCtrlAlt);
      const mainKey = keys.find((key) => !isModifierKey(key));

      // The held key repeating, which Chromium stops flagging as a repeat after a modifier is released
      if (e.keyCode === mainKey) return;

      if (!isModifierKey(e.keyCode)) {
        updateKeys([...modifiers, e.keyCode]);
      } else if (mainKey === undefined) {
        updateKeys(modifiers);
      } else {
        // A modifier pressed while the key is held still belongs to the combination
        updateKeys([
          ...MODIFIER_KEYS.filter((key) => keys.includes(key) || modifiers.includes(key)),
          mainKey,
        ]);
      }
    };

    const handleKeyUp = (e: KeyboardEvent) => {
      e.preventDefault();

      // Cancel on Escape
      if (e.keyCode === VK_ESCAPE) {
        setCapturing(null);
        updateKeys([]);
        return;
      }

      const keys = pressedKeysRef.current;
      const mainKey = keys.find((key) => !isModifierKey(key));
      if (mainKey === undefined) {
        // Only modifiers so far, so keep showing the ones still held
        if (isModifierKey(e.keyCode)) updateKeys(getHeldModifiers(e, altGraphIsCtrlAlt));
        return;
      }

      // Saved when the key is released, so letting go of a modifier first doesn't drop it
      if (e.keyCode === mainKey) {
        const updatedHotkeys = [...settings.keybindings];
        updatedHotkeys[capturing] = {
          ...updatedHotkeys[capturing],
          keys,
        };
        updateSettings({ keybindings: updatedHotkeys });
        setCapturing(null);
        updateKeys([]);
      }
    };

    // Keys released while another window has focus never send their keyup here
    const handleBlur = () => updateKeys([]);

    window.addEventListener('keydown', handleKeyDown);
    window.addEventListener('keyup', handleKeyUp);
    window.addEventListener('blur', handleBlur);

    return () => {
      window.removeEventListener('keydown', handleKeyDown);
      window.removeEventListener('keyup', handleKeyUp);
      window.removeEventListener('blur', handleBlur);
    };
  }, [capturing, platform, settings.keybindings, updateSettings]);

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
                pressedKeysRef.current = [];
                setPressedKeys([]);
              }}
            >
              {capturing === index ? getCaptureLabel(pressedKeys) : formatKeys(hotkey.keys)}
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}
