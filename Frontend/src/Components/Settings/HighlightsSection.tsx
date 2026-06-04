import { useState } from 'react';
import { Settings as SettingsType } from '../../Models/types';

interface HighlightsSectionProps {
  settings: SettingsType;
  updateSettings: (updates: Partial<SettingsType>) => void;
}

export default function HighlightsSection({ settings, updateSettings }: HighlightsSectionProps) {
  const [draggingBefore, setDraggingBefore] = useState<number | null>(null);
  const [draggingAfter, setDraggingAfter] = useState<number | null>(null);

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

        <div className="pt-3 border-t border-custom space-y-4">
          <div>
            <span className="text-md mb-2 block">
              Seconds Before Highlight
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
                value={draggingBefore ?? settings.highlightPaddingBefore}
                onChange={(e) => setDraggingBefore(Number(e.target.value))}
                onMouseDown={(e) => setDraggingBefore(Number(e.currentTarget.value))}
                onMouseUp={(e) => {
                  updateSettings({ highlightPaddingBefore: Number(e.currentTarget.value) });
                  setDraggingBefore(null);
                }}
                onTouchEnd={() => {
                  updateSettings({
                    highlightPaddingBefore: draggingBefore ?? settings.highlightPaddingBefore,
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
              Seconds After Highlight
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
                value={draggingAfter ?? settings.highlightPaddingAfter}
                onChange={(e) => setDraggingAfter(Number(e.target.value))}
                onMouseDown={(e) => setDraggingAfter(Number(e.currentTarget.value))}
                onMouseUp={(e) => {
                  updateSettings({ highlightPaddingAfter: Number(e.currentTarget.value) });
                  setDraggingAfter(null);
                }}
                onTouchEnd={() => {
                  updateSettings({
                    highlightPaddingAfter: draggingAfter ?? settings.highlightPaddingAfter,
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
