import { useEffect, useRef, type ReactNode } from 'react';
import confetti from 'canvas-confetti';
import { AudioLines, Gamepad2, MonitorPlay, Split, Trophy, Webcam, X } from 'lucide-react';
import Button from './Button';

interface Segra2IntroModalProps {
  previousVersion: string;
  onClose: () => void;
  onShowReleaseNotes: () => void;
}

const FEATURES: { icon: ReactNode; title: string; text: string }[] = [
  {
    icon: <MonitorPlay size={20} />,
    title: 'Watch while you record',
    text: 'Open the session mid-game and clip from it.',
  },
  {
    icon: <Trophy size={20} />,
    title: 'Match results',
    text: 'Score, map or champion in CS2, Dota 2, League of Legends and Siege.',
  },
  {
    icon: <Split size={20} />,
    title: 'One video per match',
    text: 'Optional. Each match is saved as soon as it ends.',
  },
  {
    icon: <Webcam size={20} />,
    title: 'Webcam (beta)',
    text: 'Recorded as its own layer you can move, resize or hide in the editor.',
  },
  {
    icon: <AudioLines size={20} />,
    title: 'Per-app audio',
    text: 'Put any app on its own audio track.',
  },
  {
    icon: <Gamepad2 size={20} />,
    title: 'Easier per-game settings',
    text: "Games you've played are already listed. Add others by picking a running app.",
  },
];

// Two columns of equal height; the long hotkey line wraps, so the right side has one item fewer
const ALSO_NEW = [
  [
    'Clip from clips and highlights',
    'Previous and next buttons in the player',
    'Game icons, kills and deaths on library cards',
    'Save recordings without game folders',
  ],
  [
    'Auto-delete old videos',
    'Keep the replay buffer after saving',
    "The record hotkey records the game you're in instead of the display",
  ],
];

const CONFETTI_SECONDS = 7;

// Cannons from both bottom corners for a while. The canvas goes inside the modal's <dialog>,
// which renders in the browser's top layer above anything outside it.
function startConfetti(host: Element): () => void {
  const canvas = document.createElement('canvas');
  canvas.style.cssText =
    'position:fixed;inset:0;width:100vw;height:100vh;pointer-events:none;z-index:2147483647';
  host.appendChild(canvas);
  const fire = confetti.create(canvas, { resize: true });

  const burst = { particleCount: 160, spread: 80, startVelocity: 70, ticks: 350 };
  fire({ ...burst, angle: 60, origin: { x: 0, y: 1 } });
  fire({ ...burst, angle: 120, origin: { x: 1, y: 1 } });

  const stream = { particleCount: 30, spread: 65, startVelocity: 62, ticks: 350 };
  const end = Date.now() + CONFETTI_SECONDS * 1000;
  const interval = setInterval(() => {
    if (Date.now() > end) {
      clearInterval(interval);
      return;
    }
    fire({ ...stream, angle: 60, origin: { x: 0, y: 1 } });
    fire({ ...stream, angle: 120, origin: { x: 1, y: 1 } });
  }, 220);

  // Let the last pieces fall before removing the canvas
  const cleanup = setTimeout(() => canvas.remove(), (CONFETTI_SECONDS + 6) * 1000);
  return () => {
    clearInterval(interval);
    clearTimeout(cleanup);
    fire.reset();
    canvas.remove();
  };
}

export default function Segra2IntroModal({
  previousVersion,
  onClose,
  onShowReleaseNotes,
}: Segra2IntroModalProps) {
  const rootRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const host = rootRef.current?.closest('dialog') ?? document.body;
    return startConfetti(host);
  }, []);

  return (
    <div ref={rootRef}>
      <div className="flex items-start justify-between gap-4">
        <div className="min-w-0">
          <h2 className="text-[1.75rem] font-bold leading-tight text-white">
            Introducing Segra 2.0
          </h2>
          <p className="mt-1 text-sm text-gray-400">
            Here's what changed since v{previousVersion}.
          </p>
        </div>
        <Button variant="ghost" icon size="sm" onClick={onClose} aria-label="Close">
          <X size={18} />
        </Button>
      </div>

      <div className="mt-5 grid grid-cols-1 gap-3 sm:grid-cols-2">
        {FEATURES.map((feature) => (
          <div key={feature.title} className="rounded-lg border border-base-400 bg-base-200 p-3">
            <h3 className="flex items-center gap-2 font-semibold text-white">
              <span className="shrink-0 text-gray-400">{feature.icon}</span>
              {feature.title}
            </h3>
            <p className="mt-1 text-sm leading-snug text-gray-400">{feature.text}</p>
          </div>
        ))}
      </div>

      <h3 className="mt-5 text-sm font-semibold text-white">Also new</h3>
      <div className="mt-2 grid grid-cols-1 gap-x-6 gap-y-1.5 text-sm text-gray-300 sm:grid-cols-2">
        {ALSO_NEW.map((column, i) => (
          <ul key={i} className="flex flex-col gap-1.5">
            {column.map((item) => (
              <li key={item} className="flex items-baseline gap-2">
                <span className="size-1.5 shrink-0 -translate-y-0.5 rounded-full bg-gray-500" />
                {item}
              </li>
            ))}
          </ul>
        ))}
      </div>

      <div className="mt-5 flex items-center justify-between gap-3 border-t border-base-400 pt-4">
        <Button variant="ghost" size="sm" onClick={onShowReleaseNotes}>
          Full release notes
        </Button>
        <Button variant="primary" size="sm" onClick={onClose}>
          Let's go
        </Button>
      </div>
    </div>
  );
}
