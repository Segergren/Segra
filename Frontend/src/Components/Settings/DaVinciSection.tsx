import { DaVinciMarkerColors, Settings as SettingsType } from '../../Models/types';
import DropdownSelect from '../DropdownSelect';

interface DaVinciSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

const COLORS = [
  { value: 'BLUE', label: 'Blue', hex: '#3b82f6' },
  { value: 'CYAN', label: 'Cyan', hex: '#22d3ee' },
  { value: 'GREEN', label: 'Green', hex: '#22c55e' },
  { value: 'YELLOW', label: 'Yellow', hex: '#eab308' },
  { value: 'RED', label: 'Red', hex: '#ef4444' },
  { value: 'PINK', label: 'Pink', hex: '#ec4899' },
  { value: 'PURPLE', label: 'Purple', hex: '#a855f7' },
  { value: 'WHITE', label: 'White', hex: '#f5f0e1' },
] as const;

const TYPES: { key: keyof DaVinciMarkerColors; label: string }[] = [
  { key: 'kill', label: 'Kill' },
  { key: 'goal', label: 'Goal' },
  { key: 'assist', label: 'Assist' },
  { key: 'death', label: 'Death' },
  { key: 'manual', label: 'Bookmark' },
];

const COLOR_ITEMS = COLORS.map((color) => ({
  value: color.value,
  label: (
    <span className="flex items-center gap-2">
      <span className="w-3 h-3 rounded-full shrink-0" style={{ backgroundColor: color.hex }} />
      {color.label}
    </span>
  ),
}));

export default function DaVinciSection({ settings, updateSettings }: DaVinciSectionProps) {
  const colors = settings.davinciMarkerColors;

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
      <h2 className="text-xl font-semibold mb-1">DaVinci Resolve</h2>
      <p className="text-sm opacity-70 mb-2">Marker colors used by Export to Resolve.</p>
      <div className="grid grid-cols-2 md:grid-cols-3 xl:grid-cols-5 gap-4">
        {TYPES.map(({ key, label }) => (
          <div key={key} className="form-control">
            <label className="label">
              <span className="label-text text-base-content">{label}</span>
            </label>
            <DropdownSelect
              items={COLOR_ITEMS}
              value={colors[key]}
              onChange={(val) =>
                updateSettings({
                  davinciMarkerColors: { ...colors, [key]: val as DaVinciMarkerColors[typeof key] },
                })
              }
            />
          </div>
        ))}
      </div>
    </div>
  );
}
