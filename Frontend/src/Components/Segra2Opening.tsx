import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { motion, useReducedMotion } from 'framer-motion';
import { SegraLogo } from './icons/SegraLogo';

interface Segra2OpeningProps {
  // Called when the screen is covered, so the modal can open underneath the fade out
  onReveal: () => void;
  onDone: () => void;
}

type Phase = 'intro' | 'zoom' | 'reveal';

const WORD = 'Segra';
const ZOOM_AT = 3000;
const REVEAL_AT = 3900;
const DONE_AT = 4500;
const EASE_OUT = [0.16, 1, 0.3, 1] as const;

// A point inside the S's middle stroke, so zooming in lands on dark
const ZOOM_ORIGIN = '50% 45.5%';

export default function Segra2Opening({ onReveal, onDone }: Segra2OpeningProps) {
  const reduceMotion = useReducedMotion();
  const [phase, setPhase] = useState<Phase>('intro');
  const revealed = useRef(false);
  const timers = useRef<ReturnType<typeof setTimeout>[]>([]);

  const reveal = () => {
    if (revealed.current) return;
    revealed.current = true;
    setPhase('reveal');
    onReveal();
  };

  const finish = () => {
    timers.current.forEach(clearTimeout);
    reveal();
    timers.current = [setTimeout(onDone, DONE_AT - REVEAL_AT)];
  };

  useEffect(() => {
    if (reduceMotion) {
      reveal();
      onDone();
      return;
    }
    timers.current = [
      setTimeout(() => setPhase('zoom'), ZOOM_AT),
      setTimeout(reveal, REVEAL_AT),
      setTimeout(onDone, DONE_AT),
    ];
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' || e.key === 'Enter' || e.key === ' ') finish();
    };
    window.addEventListener('keydown', onKey);
    return () => {
      timers.current.forEach(clearTimeout);
      window.removeEventListener('keydown', onKey);
    };
  }, []);

  if (reduceMotion) return null;

  const zooming = phase !== 'intro';

  return createPortal(
    <motion.div
      className="fixed inset-0 z-[2147483000] flex cursor-pointer select-none items-center justify-center overflow-hidden"
      style={{
        background: 'radial-gradient(circle at 50% 42%, #1d3a4e 0%, #132733 38%, #0a1219 100%)',
      }}
      initial={{ opacity: 0 }}
      animate={{ opacity: phase === 'reveal' ? 0 : 1 }}
      transition={{ duration: phase === 'reveal' ? 0.6 : 0.4, ease: 'easeOut' }}
      onClick={finish}
    >
      <div className="flex flex-col items-center gap-7">
        <motion.div
          className="size-36"
          style={{ transformOrigin: ZOOM_ORIGIN }}
          initial={{ opacity: 0, scale: 0.6, rotate: -120 }}
          animate={
            zooming ? { opacity: 1, scale: 90, rotate: 0 } : { opacity: 1, scale: 1, rotate: 0 }
          }
          transition={
            zooming
              ? { duration: 0.9, ease: [0.7, 0, 0.84, 0] }
              : { duration: 0.9, ease: EASE_OUT, delay: 0.2 }
          }
        >
          <SegraLogo className="size-full" />
        </motion.div>

        <motion.div
          className="flex items-baseline gap-3 text-5xl font-bold"
          animate={zooming ? { opacity: 0, filter: 'blur(10px)' } : { opacity: 1 }}
          transition={{ duration: 0.25 }}
        >
          <span className="flex text-white">
            {WORD.split('').map((letter, i) => (
              <motion.span
                key={i}
                initial={{ opacity: 0, y: 16, scale: 1.4, filter: 'blur(14px)' }}
                animate={{ opacity: 1, y: 0, scale: 1, filter: 'blur(0px)' }}
                transition={{ duration: 0.6, ease: EASE_OUT, delay: 1.0 + i * 0.07 }}
              >
                {letter}
              </motion.span>
            ))}
          </span>
          <motion.span
            className="text-primary"
            initial={{ opacity: 0, letterSpacing: '0.5em', filter: 'blur(14px)' }}
            animate={{ opacity: 1, letterSpacing: '0em', filter: 'blur(0px)' }}
            transition={{ duration: 0.8, ease: EASE_OUT, delay: 1.7 }}
          >
            2.0
          </motion.span>
        </motion.div>
      </div>

      {/* Covers whatever the zoom leaves uncovered before the fade out */}
      <motion.div
        className="pointer-events-none absolute inset-0 bg-[#1a1f29]"
        initial={{ opacity: 0 }}
        animate={{ opacity: zooming ? 1 : 0 }}
        transition={{ duration: 0.35, delay: zooming ? 0.55 : 0 }}
      />
    </motion.div>,
    document.body,
  );
}
