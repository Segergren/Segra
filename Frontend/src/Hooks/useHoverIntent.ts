import { useEffect, useLayoutEffect, useMemo, useRef } from 'react';
import type React from 'react';

const SETTLE_MS = 50;
const SLOW_PX_PER_MS = 0.3;
// Speed is measured over this window, since single mousemove deltas are too noisy
const SPEED_WINDOW_MS = 80;
const MIN_SPEED_SPAN_MS = 40;

export interface HoverIntentHandlers<T> {
  enter: (value: T, e: React.MouseEvent) => void;
  move: (e: React.MouseEvent) => void;
  leave: () => void;
  // Report the hover right away, e.g. on click
  commit: () => void;
}

// Reports a hover only once the pointer slows down or rests, so sweeping across items does not flash hover UI
export function useHoverIntent<T>(onHover: (value: T | null) => void): HoverIntentHandlers<T> {
  const onHoverRef = useRef(onHover);
  useLayoutEffect(() => {
    onHoverRef.current = onHover;
  });

  const timerRef = useRef<number | null>(null);
  const samplesRef = useRef<{ x: number; y: number; t: number }[]>([]);
  const pendingRef = useRef<{ value: T } | null>(null);
  const activeRef = useRef(false);

  const handlers = useMemo<HoverIntentHandlers<T>>(() => {
    const clearTimer = () => {
      if (timerRef.current) {
        clearTimeout(timerRef.current);
        timerRef.current = null;
      }
    };
    const commit = () => {
      clearTimer();
      if (!pendingRef.current || activeRef.current) return;
      activeRef.current = true;
      onHoverRef.current(pendingRef.current.value);
    };
    const restartTimer = () => {
      clearTimer();
      timerRef.current = window.setTimeout(commit, SETTLE_MS);
    };
    return {
      enter: (value, e) => {
        pendingRef.current = { value };
        activeRef.current = false;
        samplesRef.current = [{ x: e.clientX, y: e.clientY, t: performance.now() }];
        restartTimer();
      },
      move: (e) => {
        if (!pendingRef.current || activeRef.current) return;
        const now = performance.now();
        const samples = samplesRef.current;
        samples.push({ x: e.clientX, y: e.clientY, t: now });
        while (samples.length > 2 && now - samples[1].t >= SPEED_WINDOW_MS) samples.shift();
        const first = samples[0];
        const span = now - first.t;
        if (span >= MIN_SPEED_SPAN_MS) {
          const speed = Math.hypot(e.clientX - first.x, e.clientY - first.y) / span;
          if (speed < SLOW_PX_PER_MS) {
            commit();
            return;
          }
        }
        restartTimer();
      },
      leave: () => {
        clearTimer();
        pendingRef.current = null;
        if (activeRef.current) {
          activeRef.current = false;
          onHoverRef.current(null);
        }
      },
      commit,
    };
  }, []);

  useEffect(
    () => () => {
      if (timerRef.current) clearTimeout(timerRef.current);
    },
    [],
  );

  return handlers;
}
