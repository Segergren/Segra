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
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
      <h2 className="text-xl font-semibold mb-4">Highlights</h2>
      <div className="space-y-3">
        <label className="flex items-center gap-3 cursor-pointer p-3 bg-base-200 rounded-lg border border-base-400">
          <input
            type="checkbox"
            name="enableAI"
            checked={settings.enableAi}
            onChange={(e) => updateSettings({ enableAi: e.target.checked })}
            className="checkbox checkbox-primary checkbox-sm"
          />
          <div>
            <div className="font-semibold">Enable Highlights</div>
            <div className="text-sm opacity-70 mt-0.5">
              Adds Create Highlight to full sessions, which joins your kills and goals into one
              video.
            </div>
          </div>
        </label>
        <label
          className={`flex items-center gap-3 p-3 bg-base-200 rounded-lg border border-base-400 ${settings.enableAi ? 'cursor-pointer' : 'opacity-50 cursor-not-allowed'}`}
        >
          <input
            type="checkbox"
            name="autoGenerateHighlights"
            checked={settings.autoGenerateHighlights}
            onChange={(e) => updateSettings({ autoGenerateHighlights: e.target.checked })}
            className="checkbox checkbox-primary checkbox-sm"
            disabled={!settings.enableAi}
          />
          <div>
            <div className="font-semibold">Auto-Generate Highlights After Recording</div>
            <div className="text-sm opacity-70 mt-0.5">
              Creates the highlight automatically when a recording with kills or goals ends.
            </div>
          </div>
        </label>

        <div>
          <div className="grid grid-cols-2 gap-4">
            <div className="form-control w-full">
              <label
                htmlFor="highlightPaddingBefore"
                className="label text-base-content px-0 !block"
              >
                <span className="label-text">Before Highlight</span>
              </label>
              <div className="relative w-full">
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
                  className="input input-bordered bg-base-200 w-full pr-12 outline-none focus:border-base-400"
                />
                <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
                  sec
                </span>
              </div>
            </div>

            <div className="form-control w-full">
              <label
                htmlFor="highlightPaddingAfter"
                className="label text-base-content px-0 !block"
              >
                <span className="label-text">After Highlight</span>
              </label>
              <div className="relative w-full">
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
                  className="input input-bordered bg-base-200 w-full pr-12 outline-none focus:border-base-400"
                />
                <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
                  sec
                </span>
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
