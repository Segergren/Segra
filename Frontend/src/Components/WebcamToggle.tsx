import { useSyncExternalStore } from 'react';
import { SquareUserRound } from 'lucide-react';
import { sendMessageToBackend } from '../Utils/MessageUtils';

// Hidden state per video, outside the video page so toggling only re-renders the button and webcam
const hiddenById = new Map<string, boolean>();
const listeners = new Set<() => void>();
const saveTimers = new Map<string, ReturnType<typeof setTimeout>>();

const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};

export function useWebcamHidden(videoId: string, savedHidden: boolean) {
  return useSyncExternalStore(subscribe, () => hiddenById.get(videoId) ?? savedHidden);
}

function setWebcamHidden(videoId: string, hidden: boolean) {
  hiddenById.set(videoId, hidden);
  listeners.forEach((listener) => listener());

  // Saved after the animation: the state update it triggers re-renders the page
  const timer = saveTimers.get(videoId);
  if (timer) clearTimeout(timer);
  saveTimers.set(
    videoId,
    setTimeout(() => {
      saveTimers.delete(videoId);
      sendMessageToBackend('SetWebcamLayer', { Id: videoId, Hidden: hidden });
    }, 300),
  );
}

interface WebcamToggleProps {
  videoId: string;
  savedHidden: boolean;
  className: string;
}

export default function WebcamToggle({ videoId, savedHidden, className }: WebcamToggleProps) {
  const hidden = useWebcamHidden(videoId, savedHidden);
  return (
    <button
      onClick={() => setWebcamHidden(videoId, !hidden)}
      className={className}
      aria-label={hidden ? 'Show webcam' : 'Hide webcam'}
      aria-pressed={!hidden}
    >
      <SquareUserRound className={`w-5 h-5 transition-opacity ${hidden ? 'opacity-40' : ''}`} />
    </button>
  );
}
