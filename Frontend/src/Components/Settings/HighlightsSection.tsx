import { Settings as SettingsType } from '../../Models/types';

interface HighlightsSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function HighlightsSection({ settings, updateSettings }: HighlightsSectionProps) {
  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl font-semibold mb-4">Highlights</h2>
      <div className="bg-base-200 px-4 py-3 rounded-lg space-y-3 border border-custom">
        <div className="flex items-center">
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              name="enableAI"
              checked={settings.enableAi}
              onChange={(e) => updateSettings({ enableAi: e.target.checked })}
              className="checkbox checkbox-primary checkbox-sm"
            />
            <span className="flex items-center gap-1 cursor-pointer">Enable Highlights</span>
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
              disabled={!settings.enableAi}
            />
            <span className="flex items-center gap-1 cursor-pointer">
              Auto-Generate Highlights After Recording
            </span>
          </label>
        </div>
      </div>
    </div>
  );
}
