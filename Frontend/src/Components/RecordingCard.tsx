import { useState, useEffect, useCallback, useRef } from 'react';
import { PreRecording, Recording, GameResponse } from '../Models/types';
import { LuGamepad2 } from 'react-icons/lu';
import { BsDisplay } from 'react-icons/bs';
import { MdHdrOn } from 'react-icons/md';
import { useSettings } from '../Context/SettingsContext';

interface RecordingCardProps {
  recording?: Recording;
  preRecording?: PreRecording;
}

const RecordingCard: React.FC<RecordingCardProps> = ({ recording, preRecording }) => {
  const [elapsedTime, setElapsedTime] = useState({ hours: 0, minutes: 0, seconds: 0 });
  const { showGameBackground } = useSettings();
  const [coverUrl, setCoverUrl] = useState<string | null>(null);
  const lastFetchedGameRef = useRef<string | null>(null);
  const [showShockwave, setShowShockwave] = useState(false);

  // Listen for bookmark created events
  useEffect(() => {
    const handleBookmarkCreated = (event: CustomEvent) => {
      if (event.detail?.method === 'BookmarkCreated') {
        setShowShockwave(true);
        // Reset after animation completes
        setTimeout(() => setShowShockwave(false), 600);
      }
    };

    window.addEventListener('websocket-message', handleBookmarkCreated as EventListener);
    return () => {
      window.removeEventListener('websocket-message', handleBookmarkCreated as EventListener);
    };
  }, []);

  useEffect(() => {
    if (preRecording) {
      setElapsedTime({ hours: 0, minutes: 0, seconds: 0 });
      return;
    }

    if (!recording?.startTime) return;

    const startTime = new Date(recording.startTime).getTime(); // Get the timestamp in milliseconds

    const updateElapsedTime = () => {
      const now = Date.now(); // Current time in milliseconds
      const secondsElapsed = Math.max(0, Math.floor((now - startTime) / 1000));

      const hours = Math.floor(secondsElapsed / 3600);
      const minutes = Math.floor((secondsElapsed % 3600) / 60);
      const seconds = secondsElapsed % 60;

      setElapsedTime({ hours, minutes, seconds });
    };

    // Update the timer every second
    const intervalId = setInterval(updateElapsedTime, 1000);

    // Clean up the interval when the component unmounts
    return () => clearInterval(intervalId);
  }, [recording?.startTime, preRecording]);

  // Fetch game data from Segra.tv API
  const fetchGameData = useCallback(async () => {
    // Skip API call entirely if game background is disabled
    if (!showGameBackground) {
      setCoverUrl(null);
      return;
    }

    const gameName = preRecording ? preRecording.game : recording?.game;

    // Don't fetch for "Manual Recording"
    if (!gameName || gameName === 'Manual Recording') {
      setCoverUrl(null);
      return;
    }

    try {
      // Use coverImageId directly if available, otherwise search by name
      const coverImageId = recording?.coverImageId || preRecording?.coverImageId;
      if (coverImageId) {
        setCoverUrl(`https://segra.tv/api/games/cover/${coverImageId}`);
        lastFetchedGameRef.current = gameName;
        return;
      }

      // Skip if we already fetched for this game
      if (lastFetchedGameRef.current === gameName) {
        return;
      }

      const response = await fetch(
        `https://segra.tv/api/games/search?name=${encodeURIComponent(gameName)}`,
      );

      if (!response.ok) {
        throw new Error('Game not found');
      }

      const data: GameResponse = await response.json();

      if (data.game?.cover?.image_id) {
        setCoverUrl(`https://segra.tv/api/games/cover/${data.game.cover.image_id}`);
      }
      lastFetchedGameRef.current = gameName;
    } catch (error) {
      console.error('Error fetching game data:', error);
      setCoverUrl(null);
      lastFetchedGameRef.current = gameName;
    }
  }, [preRecording, recording, showGameBackground]);

  // Call fetchGameData when game changes
  useEffect(() => {
    fetchGameData();
  }, [fetchGameData]);

  return (
    <div className="mb-2 px-2">
      <div className="bg-base-300 border border-base-400 border-opacity-75 rounded-lg px-3 py-3.5 cursor-default relative">
        {/* Shockwave effect on bookmark creation */}
        {showShockwave && (
          <div className="absolute inset-0 z-20 pointer-events-none">
            <div className="absolute top-1/2 left-1/2 -translate-x-1/2 -translate-y-1/2 w-0 h-0 rounded-full bg-primary/40 animate-shockwave" />
          </div>
        )}
        {/* Background image with game cover */}
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
            ></div>
          </div>
        )}

        {/* Recording Indicator */}
        <div className="flex items-center justify-between mb-1 relative z-10">
          <div className="flex items-center">
            <span
              className={`w-3 h-3 rounded-full mr-2 ${preRecording ? 'bg-orange-500' : 'bg-red-500 animate-pulse'}`}
            ></span>
            <span className="text-gray-200 text-sm font-medium">
              {preRecording ? preRecording.status : 'Recording'}
            </span>
            {!preRecording && (
              <div
                className={`tooltip tooltip-right ${recording?.isUsingGameHook ? 'tooltip-success' : 'tooltip-warning'} flex items-center ml-1.5`}
                data-tip={`${recording?.isUsingGameHook ? 'Game capture (using game hook)' : 'Display capture (not using game hook)'}`}
              >
                <div className={`swap swap-flip cursor-default overflow-hidden justify-center`}>
                  <input type="checkbox" checked={recording?.isUsingGameHook} readOnly />
                  <div className={`swap-on`}>
                    <LuGamepad2 className="h-5 w-5 text-gray-300" />
                  </div>
                  <div className={`swap-off`}>
                    <BsDisplay className="h-5 w-5 text-gray-300 scale-90" />
                  </div>
                </div>
              </div>
            )}
          </div>
          {!preRecording && recording?.isHdr && (
            <div className="tooltip tooltip-right tooltip-success m-0" data-tip="Recording in HDR">
              <MdHdrOn className="h-5 w-5 text-gray-300" />
            </div>
          )}
        </div>

        {/* Recording Details */}
        <div className="flex items-center text-gray-400 text-sm relative z-10">
          <div className="flex items-center max-w-[105%]">
            <span className="countdown">
              {elapsedTime.hours > 0 && (
                <>
                  <span style={{ '--value': elapsedTime.hours, '--digits': 2 } as React.CSSProperties}></span>:
                </>
              )}
              <span style={{ '--value': elapsedTime.minutes, '--digits': 2 } as React.CSSProperties}></span>:
              <span style={{ '--value': elapsedTime.seconds, '--digits': 2 } as React.CSSProperties}></span>
            </span>
            <p className="truncate ml-2">{preRecording ? preRecording.game : recording?.game}</p>
          </div>
        </div>
      </div>
    </div>
  );
};

export default RecordingCard;
