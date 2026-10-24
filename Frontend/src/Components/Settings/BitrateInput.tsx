import { useState, useEffect } from 'react';
import { clampInt } from '../../Utils/NumberUtils';

const MIN_BITRATE_MBPS = 1;
const MAX_BITRATE_MBPS = 100;

interface BitrateInputProps {
  value: number;
  onChange: (mbps: number) => void;
  className?: string;
}

// Whole Mbps, since recording bitrates are stored as integers
export default function BitrateInput({
  value,
  onChange,
  className = 'bg-base-200',
}: BitrateInputProps) {
  const [localValue, setLocalValue] = useState<string>(String(value));

  useEffect(() => {
    setLocalValue(String(value));
  }, [value]);

  const commit = () => {
    // Leave an untouched value alone, even one outside the range
    if (localValue === String(value)) return;

    const mbps = clampInt(localValue, MIN_BITRATE_MBPS, MAX_BITRATE_MBPS, value);
    setLocalValue(String(mbps));
    if (mbps !== value) onChange(mbps);
  };

  return (
    <div className="relative w-full">
      <input
        type="number"
        value={localValue}
        onChange={(e) => setLocalValue(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') e.currentTarget.blur();
        }}
        // A focused number field changes with the mouse wheel, which the dropdown it replaced never did
        onWheel={(e) => e.currentTarget.blur()}
        min={MIN_BITRATE_MBPS}
        max={MAX_BITRATE_MBPS}
        step={1}
        className={`input input-bordered ${className} w-full pr-14 outline-none focus:border-base-400`}
      />
      <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
        Mbps
      </span>
    </div>
  );
}
