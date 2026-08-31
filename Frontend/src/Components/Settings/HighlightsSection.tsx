import { useState } from 'react';
import { Settings as SettingsType } from '../../Models/types';

interface HighlightsSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function HighlightsSection({ settings, updateSettings }: HighlightsSectionProps) {
  const [localPaddingBefore, setLocalPaddingBefore] = useState<string>(
    String(settings.highlightPaddingBefore),
  );
  const [localPaddingAfter, setLocalPaddingAfter] = useState<string>(
    String(settings.highlightPaddingAfter),
  );

  const commitPadding = (
    local: string,
    setLocal: (value: string) => void,
    key: 'highlightPaddingBefore' | 'highlightPaddingAfter',
  ) => {
    const parsed = Number(local);
    const value = Number.isFinite(parsed) ? Math.min(60, Math.max(1, parsed)) : settings[key];
    setLocal(String(value));
    updateSettings({ [key]: value });
  };

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl font-semibold mb-4">Highlights</h2>
      <div className="space-y-3">
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

        <div className="pt-3 border-t border-custom">
          <div className="grid grid-cols-2 gap-4">
            <div className="form-control w-full">
              <label
                htmlFor="highlightPaddingBefore"
                className="label text-base-content px-0 !block mb-1"
              >
                <span className="label-text">Before Highlight</span>
              </label>
              <div className="join w-full">
                <input
                  id="highlightPaddingBefore"
                  type="number"
                  name="highlightPaddingBefore"
                  value={localPaddingBefore}
                  onChange={(e) => setLocalPaddingBefore(e.target.value)}
                  onBlur={() =>
                    commitPadding(
                      localPaddingBefore,
                      setLocalPaddingBefore,
                      'highlightPaddingBefore',
                    )
                  }
                  min={1}
                  max={60}
                  step={0.5}
                  className="input input-bordered bg-base-200 join-item flex-1 w-full outline-none focus:border-base-400"
                />
                <span className="join-item flex items-center px-3 bg-base-200 border border-base-400 text-sm opacity-70">
                  seconds
                </span>
              </div>
            </div>

            <div className="form-control w-full">
              <label
                htmlFor="highlightPaddingAfter"
                className="label text-base-content px-0 !block mb-1"
              >
                <span className="label-text">After Highlight</span>
              </label>
              <div className="join w-full">
                <input
                  id="highlightPaddingAfter"
                  type="number"
                  name="highlightPaddingAfter"
                  value={localPaddingAfter}
                  onChange={(e) => setLocalPaddingAfter(e.target.value)}
                  onBlur={() =>
                    commitPadding(localPaddingAfter, setLocalPaddingAfter, 'highlightPaddingAfter')
                  }
                  min={1}
                  max={60}
                  step={0.5}
                  className="input input-bordered bg-base-200 join-item flex-1 w-full outline-none focus:border-base-400"
                />
                <span className="join-item flex items-center px-3 bg-base-200 border border-base-400 text-sm opacity-70">
                  seconds
                </span>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
