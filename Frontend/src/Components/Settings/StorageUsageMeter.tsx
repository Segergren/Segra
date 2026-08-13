import { useMemo } from 'react';
import { AlertTriangle } from 'lucide-react';
import { Content, ContentType, State } from '../../Models/types';

const TYPE_COLORS: Record<ContentType, string> = {
  Session: '#3987e5',
  Buffer: '#d95926',
  Clip: '#199e70',
  Highlight: '#c98500',
};

const OTHER_COLOR = '#49515b';

const TYPE_LABELS: Record<ContentType, string> = {
  Session: 'Sessions',
  Buffer: 'Buffers',
  Clip: 'Clips',
  Highlight: 'Highlights',
};

const TYPE_ORDER: ContentType[] = ['Session', 'Buffer', 'Clip', 'Highlight'];

const normalizePath = (path: string) => path.replace(/\\/g, '/').replace(/\/+$/, '');

const formatGb = (value: number) => {
  if (value >= 100) return value.toFixed(0);
  if (value >= 10) return value.toFixed(1);
  return value.toFixed(2);
};

// "E:/Segra" -> "Drive E:". Linux paths have no letter, so they fall back to "Disk".
const driveLabel = (contentFolder: string) => {
  const match = /^([a-zA-Z]):/.exec(contentFolder.trim());
  return match ? `Drive ${match[1].toUpperCase()}:` : 'Disk';
};

interface Segment {
  key: string;
  label: string;
  color: string;
  gb: number;
}

interface StorageUsageMeterProps {
  content: Content[];
  contentFolder: string;
  storageLimitGb: number;
  usedGb: number;
  driveUsedGb: State['recordingDriveUsedGb'];
  driveFreeGb: State['recordingDriveFreeGb'];
}

export default function StorageUsageMeter({
  content,
  contentFolder,
  storageLimitGb,
  usedGb,
  driveUsedGb,
  driveFreeGb,
}: StorageUsageMeterProps) {
  const segments = useMemo<Segment[]>(() => {
    // Imported content can live outside the recording path and isn't counted against
    // the limit, so the breakdown has to match what the limit actually measures.
    const root = normalizePath(contentFolder.trim()).toLowerCase();
    const inside = root
      ? content.filter((c) =>
          normalizePath(c.filePath || '')
            .toLowerCase()
            .startsWith(root + '/'),
        )
      : [];

    const byType = TYPE_ORDER.map((type) => ({
      key: type,
      label: TYPE_LABELS[type],
      color: TYPE_COLORS[type],
      gb:
        inside.filter((c) => c.type === type).reduce((sum, c) => sum + (c.fileSizeKb || 0), 0) /
        (1024 * 1024),
    }));

    // Whatever the folder holds beyond tracked content: orphaned files, stray videos.
    const tracked = byType.reduce((sum, s) => sum + s.gb, 0);
    const other = Math.max(0, usedGb - tracked);

    return [...byType, { key: 'Other', label: 'Other', color: OTHER_COLOR, gb: other }].filter(
      (s) => s.gb >= 0.01,
    );
  }, [content, contentFolder, usedGb]);

  const isOverLimit = storageLimitGb > 0 && usedGb > storageLimitGb;
  const usedPercent = storageLimitGb > 0 ? (usedGb / storageLimitGb) * 100 : 0;
  const freeInLimitGb = Math.max(0, storageLimitGb - usedGb);

  // Over the limit the bar is full, so segments are scaled against usage instead.
  const scaleGb = isOverLimit ? usedGb : storageLimitGb;

  const hasDriveSpace = driveUsedGb !== null && driveFreeGb !== null;
  const driveTotalGb = hasDriveSpace ? driveUsedGb + driveFreeGb : null;

  return (
    <div className="rounded-lg border border-base-400 bg-base-200 p-4">
      <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
        <span className="font-medium text-base-content">Storage Usage</span>
        <span className="flex items-center gap-2 text-sm tabular-nums text-base-content/60">
          <span>
            {formatGb(usedGb)} GB of {formatGb(storageLimitGb)} GB limit
          </span>
          <span aria-hidden="true">·</span>
          <span className={`font-semibold ${isOverLimit ? 'text-error' : 'text-base-content/80'}`}>
            {isOverLimit && <AlertTriangle size={13} className="mr-1 inline-block align-[-2px]" />}
            {Math.round(usedPercent)}%{isOverLimit ? ' over limit' : ''}
          </span>
        </span>
      </div>

      <div
        className="mt-3 flex h-2.5 w-full gap-[2px] overflow-hidden rounded-full bg-base-400/35"
        role="img"
        aria-label={`${formatGb(usedGb)} GB used of a ${formatGb(storageLimitGb)} GB limit`}
      >
        {segments.map((s) => (
          <div
            key={s.key}
            className="h-full"
            style={{
              width: `${scaleGb > 0 ? (s.gb / scaleGb) * 100 : 0}%`,
              backgroundColor: s.color,
            }}
          />
        ))}
      </div>

      <div className="mt-3 flex flex-wrap items-center justify-between gap-x-5 gap-y-2 text-sm">
        <div className="flex flex-wrap items-center gap-x-5 gap-y-2">
          {segments.map((s) => (
            <span key={s.key} className="flex items-center gap-2">
              <span
                className="size-2.5 shrink-0 rounded-[2px]"
                style={{ backgroundColor: s.color }}
              />
              <span className="text-base-content/80">{s.label}</span>
              <span className="tabular-nums text-base-content/50">{formatGb(s.gb)} GB</span>
            </span>
          ))}
        </div>
        {!isOverLimit && (
          <span className="flex items-center gap-2">
            <span className="size-2.5 shrink-0 rounded-[2px] border border-base-400 bg-base-400/35" />
            <span className="text-base-content/80">Free in limit</span>
            <span className="tabular-nums text-base-content/50">{formatGb(freeInLimitGb)} GB</span>
          </span>
        )}
      </div>

      <div className="mt-3 flex flex-wrap items-center justify-between gap-x-4 gap-y-1 border-t border-base-400/50 pt-3 text-xs tabular-nums text-base-content/50">
        <span>
          {hasDriveSpace && driveTotalGb !== null
            ? `${driveLabel(contentFolder)} ${formatGb(driveUsedGb)} GB used of ${formatGb(driveTotalGb)} GB`
            : 'Checking drive space...'}
        </span>
        {hasDriveSpace && <span>{formatGb(driveFreeGb)} GB free</span>}
      </div>
    </div>
  );
}
