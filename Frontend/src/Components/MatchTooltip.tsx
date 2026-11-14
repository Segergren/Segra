import { useEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { AnimatePresence, motion } from 'framer-motion';
import type { Match } from '../Models/types';
import { formatMatchKda } from '../Utils/MatchUtils';

const IMAGE_WIDTH = 208;
const SHOW_DELAY_MS = 400;

// Shows a match's details on hover. The tooltip is portaled so the timeline and card
// thumbnails, which clip their content, don't cut it off.
export default function MatchTooltip({
  match,
  mapImage,
  className,
  onClick,
  children,
}: {
  match: Match;
  mapImage?: string;
  className?: string;
  onClick?: (e: React.MouseEvent<HTMLDivElement>) => void;
  children: ReactNode;
}) {
  const [anchor, setAnchor] = useState<DOMRect | null>(null);
  const [open, setOpen] = useState(false);
  const showTimer = useRef<number | undefined>(undefined);

  useEffect(() => () => window.clearTimeout(showTimer.current), []);

  const kda = formatMatchKda(match);
  const details = [
    match.mode,
    match.character,
    kda && `${match.assists != null ? 'K/D/A' : 'K/D'} ${kda}`,
  ].filter(Boolean);
  const hasContent = !!mapImage || !!match.map || details.length > 0;

  // Kept inside the window; the text-only tooltip is narrower but this is a safe bound
  const center = anchor
    ? Math.min(
        Math.max(anchor.left + anchor.width / 2, IMAGE_WIDTH / 2 + 8),
        window.innerWidth - IMAGE_WIDTH / 2 - 8,
      )
    : 0;

  return (
    <div
      className={className}
      onClick={onClick}
      onMouseEnter={(e) => {
        if (!hasContent) return;
        const target = e.currentTarget;
        window.clearTimeout(showTimer.current);
        showTimer.current = window.setTimeout(() => {
          setAnchor(target.getBoundingClientRect());
          setOpen(true);
        }, SHOW_DELAY_MS);
      }}
      onMouseLeave={() => {
        window.clearTimeout(showTimer.current);
        setOpen(false);
      }}
    >
      {children}
      {createPortal(
        <AnimatePresence>
          {open && anchor && (
            <div
              key="match-tooltip"
              className="fixed z-[300] pointer-events-none -translate-x-1/2 -translate-y-full"
              style={{
                left: center,
                top: anchor.top - 6,
                width: mapImage ? IMAGE_WIDTH : undefined,
              }}
            >
              <motion.div
                initial={{ opacity: 0, y: 4 }}
                animate={{ opacity: 1, y: 0 }}
                exit={{ opacity: 0, y: 4 }}
                transition={{ duration: 0.12 }}
                className="p-1 border rounded-lg shadow-lg bg-base-300 border-base-400 text-center"
              >
                {mapImage && (
                  <img src={mapImage} alt="" className="w-full rounded aspect-video object-cover" />
                )}
                <div className="px-2 pt-1.5 pb-1 whitespace-nowrap">
                  {match.map && <div className="text-xs font-semibold text-white">{match.map}</div>}
                  {details.length > 0 && (
                    <div className="mt-0.5 text-[11px] text-gray-300">{details.join(' · ')}</div>
                  )}
                </div>
              </motion.div>
            </div>
          )}
        </AnimatePresence>,
        document.body,
      )}
    </div>
  );
}
