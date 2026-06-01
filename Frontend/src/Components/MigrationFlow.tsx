import { motion } from 'framer-motion';
import { Folder, HardDrive } from 'lucide-react';

interface MigrationFlowProps {
  fromPaths: string[];
  toPath: string;
  count: number;
  sizeGb: number;
}

export default function MigrationFlow({ fromPaths, toPath, count, sizeGb }: MigrationFlowProps) {
  return (
    <div>
      <p className="mb-4 text-base">
        Move {count} video{count === 1 ? '' : 's'} ({sizeGb.toFixed(2)} GB) to your recording path.
      </p>
      <div className="flex items-center gap-3">
        <div className="flex-1 min-w-0 rounded-lg border border-base-400 bg-base-200 p-3">
          <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-base-content/60">
            From
          </div>
          <div className="space-y-2">
            {fromPaths.map((path) => (
              <div key={path} className="flex items-center gap-2">
                <Folder size={16} className="shrink-0 text-base-content/70" />
                <span className="text-sm break-all">{path}</span>
              </div>
            ))}
          </div>
        </div>

        <div className="relative flex h-8 w-20 shrink-0 items-center">
          <div className="absolute inset-x-0 h-0.5 rounded-full bg-base-400" />
          {[0, 1, 2].map((i) => (
            <motion.span
              key={i}
              className="absolute h-2 w-2 rounded-full bg-primary"
              animate={{ left: ['0%', '90%'], opacity: [0, 1, 1, 0] }}
              transition={{ duration: 1.3, repeat: Infinity, delay: i * 0.43, ease: 'linear' }}
            />
          ))}
        </div>

        <div className="flex-1 min-w-0 rounded-lg border border-primary/40 bg-primary/10 p-3">
          <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-base-content/60">
            To
          </div>
          <div className="flex items-center gap-2">
            <HardDrive size={16} className="shrink-0 text-primary" />
            <span className="text-sm break-all">{toPath}</span>
          </div>
        </div>
      </div>
    </div>
  );
}
