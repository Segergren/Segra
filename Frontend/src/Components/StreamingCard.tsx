import { useEffect, useRef, useState } from 'react';
import { Streaming, GameResponse } from '../Models/types';
import { Gamepad2, Monitor, Radio, OctagonX } from 'lucide-react';
import { useSettings } from '../Context/SettingsContext';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import Button from './Button';

const pad = (n: number) => String(n).padStart(2, '0');

interface StreamingCardProps {
  streaming: Streaming;
}

const StreamingCard: React.FC<StreamingCardProps> = ({ streaming }) => {
  const timerRef = useRef<HTMLSpanElement>(null);
  const { showGameBackground } = useSettings();
  const [coverUrl, setCoverUrl] = useState<string | null>(null);
  const lastFetchedGameRef = useRef<string | null>(null);

  useEffect(() => {
    if (!streaming.startTime) return;

    const startTime = new Date(streaming.startTime).getTime();

    const updateElapsedTime = () => {
      if (!timerRef.current) return;
      const now = Date.now();
      const secondsElapsed = Math.max(0, Math.floor((now - startTime) / 1000));

      const hours = Math.floor(secondsElapsed / 3600);
      const minutes = Math.floor((secondsElapsed % 3600) / 60);
      const seconds = secondsElapsed % 60;

      timerRef.current.textContent =
        hours > 0
          ? `${pad(hours)}:${pad(minutes)}:${pad(seconds)}`
          : `${pad(minutes)}:${pad(seconds)}`;
    };

    updateElapsedTime();
    const intervalId = setInterval(updateElapsedTime, 1000);
    return () => clearInterval(intervalId);
  }, [streaming.startTime]);

  // Fetch IGDB cover from segra.tv API based on streaming.coverImageId / streaming.game.
  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      if (!showGameBackground) {
        setCoverUrl(null);
        return;
      }

      if (streaming.coverImageId) {
        setCoverUrl(`https://segra.tv/api/games/cover/${streaming.coverImageId}`);
        lastFetchedGameRef.current = streaming.game ?? null;
        return;
      }

      const gameName = streaming.game;
      if (!gameName) {
        setCoverUrl(null);
        return;
      }
      if (lastFetchedGameRef.current === gameName) return;

      try {
        const response = await fetch(
          `https://segra.tv/api/games/search?name=${encodeURIComponent(gameName)}`,
        );
        if (!response.ok) throw new Error('Game not found');
        const data: GameResponse = await response.json();
        if (cancelled) return;
        if (data.game?.cover?.image_id) {
          setCoverUrl(`https://segra.tv/api/games/cover/${data.game.cover.image_id}`);
        }
        lastFetchedGameRef.current = gameName;
      } catch {
        setCoverUrl(null);
        lastFetchedGameRef.current = gameName;
      }
    };
    run();
    return () => {
      cancelled = true;
    };
  }, [streaming.coverImageId, streaming.game, showGameBackground]);

  return (
    <div className="mb-2 px-2">
      <div className="bg-base-300 border border-base-400 border-opacity-75 rounded-lg px-3 py-3.5 relative overflow-hidden">
        {coverUrl && showGameBackground && (
          <div className="absolute inset-0 z-0 opacity-25">
            <div
              className="absolute inset-0 rounded-[7px]"
              style={{
                backgroundImage: `url(${coverUrl})`,
                backgroundSize: 'cover',
                backgroundPosition: 'center',
                backgroundRepeat: 'no-repeat',
              }}
            />
          </div>
        )}

        <div className="flex items-center justify-between mb-1 relative z-10">
          <div className="flex items-center">
            <span className="w-3 h-3 shrink-0 mb-0.5 rounded-full mr-1.5 bg-error animate-pulse" />
            <span className="text-gray-200 text-sm font-medium flex items-center gap-1">
              <Radio className="h-4 w-4" />
              Live
            </span>
            <div
              className={`tooltip tooltip-right ${streaming.isUsingGameHook ? 'tooltip-success' : 'tooltip-warning'} flex items-center ml-1.5 [&::before]:delay-200 [&::after]:delay-200`}
              data-tip={`${streaming.isUsingGameHook ? 'Game capture (using game hook)' : 'Display capture (no game hooked)'}`}
            >
              <div className="swap swap-flip cursor-default overflow-hidden justify-center">
                <input type="checkbox" checked={streaming.isUsingGameHook} readOnly />
                <div className="swap-on">
                  <Gamepad2 className="h-5 w-5 text-gray-300" />
                </div>
                <div className="swap-off">
                  <Monitor className="h-5 w-5 text-gray-300 scale-90" />
                </div>
              </div>
            </div>
          </div>
        </div>

        <div className="flex items-center text-gray-400 text-sm relative z-10">
          <div className="flex items-center max-w-[105%]">
            <span ref={timerRef} className="tabular-nums">
              00:00
            </span>
            {streaming.game && <p className="truncate ml-2">{streaming.game}</p>}
          </div>
        </div>

        <div className="relative z-10 mt-3">
          <Button
            variant="primary"
            className="w-full h-10"
            onClick={() => sendMessageToBackend('StopStreaming')}
          >
            <OctagonX className="w-4 h-4" />
            Stop
          </Button>
        </div>
      </div>
    </div>
  );
};

export default StreamingCard;
