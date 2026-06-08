import { useState } from 'react';
import { Settings as SettingsType } from '../../Models/types';

interface LowlightsSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function LowlightsSection({ settings, updateSettings }: LowlightsSectionProps) {
  const [draggingBefore, setDraggingBefore] = useState<number | null>(null);
  const [draggingAfter, setDraggingAfter] = useState<number | null>(null);

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl font-semibold mb-4">Lowlights</h2>
      <div className="bg-base-200 px-4 py-3 rounded-lg space-y-3 border border-custom">
        <div className="flex items-center">
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              name="enableLowlights"
              checked={settings.enableLowlights}
              onChange={(e) => updateSettings({ enableLowlights: e.target.checked })}
              className="checkbox checkbox-primary checkbox-sm"
            />
            <span className="flex items-center gap-1 cursor-pointer">Enable Lowlights</span>
          </label>
        </div>
        <div className="flex items-center">
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              name="autoGenerateLowlights"
              checked={settings.autoGenerateLowlights}
              onChange={(e) => updateSettings({ autoGenerateLowlights: e.target.checked })}
              className="checkbox checkbox-primary checkbox-sm"
              disabled={!settings.enableLowlights}
            />
            <span className="flex items-center gap-1 cursor-pointer">
              Auto-Generate Lowlights After Recording
            </span>
          </label>
        </div>

        <div className="pt-3 border-t border-custom space-y-4">
          <div>
            <span className="text-md mb-2 block">
              Seconds Before Lowlight
              {draggingBefore !== null && ` (${draggingBefore.toFixed(1)}s)`}
            </span>
            <div className="flex items-center gap-3">
              <span className="text-xs text-base-content opacity-50 w-5 text-right shrink-0">
                1
              </span>
              <input
                type="range"
                min={1}
                max={60}
                step={0.5}
                value={draggingBefore ?? settings.lowlightPaddingBefore}
                onChange={(e) => setDraggingBefore(Number(e.target.value))}
                onMouseDown={(e) => setDraggingBefore(Number(e.currentTarget.value))}
                onMouseUp={(e) => {
                  updateSettings({ lowlightPaddingBefore: Number(e.currentTarget.value) });
                  setDraggingBefore(null);
                }}
                onTouchEnd={() => {
                  updateSettings({
                    lowlightPaddingBefore: draggingBefore ?? settings.lowlightPaddingBefore,
                  });
                  setDraggingBefore(null);
                }}
                className="range range-xs range-primary flex-1 [--range-fill:0]"
              />
              <span className="text-xs text-base-content opacity-50 w-7 shrink-0">60s</span>
            </div>
          </div>

          <div>
            <span className="text-md mb-2 block">
              Seconds After Lowlight
              {draggingAfter !== null && ` (${draggingAfter.toFixed(1)}s)`}
            </span>
            <div className="flex items-center gap-3">
              <span className="text-xs text-base-content opacity-50 w-5 text-right shrink-0">
                1
              </span>
              <input
                type="range"
                min={1}
                max={60}
                step={0.5}
                value={draggingAfter ?? settings.lowlightPaddingAfter}
                onChange={(e) => setDraggingAfter(Number(e.target.value))}
                onMouseDown={(e) => setDraggingAfter(Number(e.currentTarget.value))}
                onMouseUp={(e) => {
                  updateSettings({ lowlightPaddingAfter: Number(e.currentTarget.value) });
                  setDraggingAfter(null);
                }}
                onTouchEnd={() => {
                  updateSettings({
                    lowlightPaddingAfter: draggingAfter ?? settings.lowlightPaddingAfter,
                  });
                  setDraggingAfter(null);
                }}
                className="range range-xs range-primary flex-1 [--range-fill:0]"
              />
              <span className="text-xs text-base-content opacity-50 w-7 shrink-0">60s</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
