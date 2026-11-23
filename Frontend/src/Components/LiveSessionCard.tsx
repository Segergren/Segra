import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Recording } from '../Models/types';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { formatCreatedAt } from '../Utils/ContentFormat';
import { gameIconSource } from '../Utils/GameIconSource';
import { useAppState } from '../Context/AppStateContext';
import { useSettings } from '../Context/SettingsContext';
import GameIcon from './GameIcon';
import { getLiveThumbnail, setLiveThumbnail } from '../Utils/LiveThumbnails';

// True when no pixel is brighter than near-black, checked on a downscaled copy
const isFullyBlack = async (blob: Blob) => {
  const bitmap = await createImageBitmap(blob);
  const canvas = document.createElement('canvas');
  canvas.width = 64;
  canvas.height = 36;
  const ctx = canvas.getContext('2d');
  if (!ctx) return false;
  ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close();
  const { data } = ctx.getImageData(0, 0, canvas.width, canvas.height);
  for (let i = 0; i < data.length; i += 4) {
    if (0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2] > 20) return false;
  }
  return true;
};

// The session being recorded; opening it plays a stream-copied snapshot of the file so far
export default function LiveSessionCard({
  recording,
  isFinishing = false,
}: {
  recording: Recording;
  isFinishing?: boolean;
}) {
  const { t } = useTranslation('cards');
  const startMs = new Date(recording.startTime).getTime();
  const filePath = recording.filePath;
  const [thumbnailUrl, setThumbnailUrl] = useState(() =>
    filePath ? getLiveThumbnail(filePath) : undefined,
  );
  const [thumbnailLoaded, setThumbnailLoaded] = useState(() => !!thumbnailUrl);
  const [opening, setOpening] = useState(false);
  const { gameList } = useAppState();
  const { games: gameSettings, airplaneMode } = useSettings();
  const iconSrc = gameIconSource(recording.game, undefined, gameList, gameSettings, airplaneMode);
  // Opening usually takes well under a second, so the overlay only shows when it takes longer
  const [showOpening, setShowOpening] = useState(false);
  useEffect(() => {
    if (!opening) {
      setShowOpening(false);
      return;
    }
    const timer = setTimeout(() => setShowOpening(true), 300);
    return () => clearTimeout(timer);
  }, [opening]);

  // Retried only until the file has enough data to read
  useEffect(() => {
    if (thumbnailUrl || !filePath) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout>;
    let failures = 0;
    const attempt = async () => {
      const time = Math.max(0, Math.floor((Date.now() - startMs) / 1000 - 1));
      try {
        const response = await fetch(
          `http://localhost:2222/api/thumbnail?input=${encodeURIComponent(filePath)}&time=${time}&width=960`,
        );
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const blob = await response.blob();
        if (await isFullyBlack(blob)) throw new Error('Black frame');
        if (cancelled) return;
        const objectUrl = URL.createObjectURL(blob);
        setLiveThumbnail(filePath, objectUrl);
        setThumbnailUrl(objectUrl);
      } catch {
        // Each try runs ffmpeg on the recording, so a long black loading screen is retried every 15 s
        failures++;
        if (!cancelled) timer = setTimeout(attempt, failures < 10 ? 1000 : 15000);
      }
    };
    // OBS writes the first readable fragment about one keyframe interval (1 s) in
    timer = setTimeout(attempt, Math.max(0, 1 - (Date.now() - startMs) / 1000) * 1000);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [thumbnailUrl, filePath, startMs]);

  useEffect(() => {
    const handleMessage = (event: CustomEvent) => {
      const method = event.detail?.method;
      if (method === 'RecordingSnapshotReady' || method === 'RecordingSnapshotFailed') {
        setOpening(false);
      }
    };
    window.addEventListener('websocket-message', handleMessage as EventListener);
    return () => window.removeEventListener('websocket-message', handleMessage as EventListener);
  }, []);

  return (
    <div
      className={`card card-compact bg-base-300 text-gray-300 w-full ${opening ? 'cursor-default' : 'cursor-pointer'}`}
      onClick={() => {
        if (opening) return;
        setOpening(true);
        sendMessageToBackend('OpenRecordingSnapshot');
      }}
    >
      <figure className="relative aspect-video border border-b-0 border-[#49515b] [background:linear-gradient(#000,#000)_padding-box,linear-gradient(#49515b,#49515b)_border-box]">
        <div
          className={`absolute inset-0 rounded-none bg-base-200/75 transition-opacity duration-200 ${thumbnailLoaded ? 'opacity-0' : 'opacity-100'}`}
        ></div>
        {thumbnailUrl && (
          <img
            src={thumbnailUrl}
            alt={t('live.thumbnail')}
            className={`w-full h-full object-contain select-none transition-opacity duration-200 ${thumbnailLoaded ? 'opacity-100' : 'opacity-0'}`}
            width={1600}
            height={900}
            draggable={false}
            onLoad={() => setThumbnailLoaded(true)}
          />
        )}
        {!isFinishing && (
          <span className="absolute top-2 left-2 flex items-center gap-1.5 bg-black/75 text-white text-xs px-2 py-1 rounded">
            <span className="h-2 w-2 rounded-full bg-red-500" />
            {t('live.rec')}
          </span>
        )}
        {showOpening && (
          <div className="absolute inset-0 bg-black/70 flex items-center justify-center gap-2 text-sm text-white">
            <span className="loading loading-spinner loading-xs" />
            {t('live.opening')}
          </div>
        )}
      </figure>

      <div className="card-body gap-1 pt-2 border border-t-0 border-[#49515b] rounded-b-[inherit]">
        <div className="flex justify-between items-center">
          <div className="flex items-center gap-2 min-w-0 flex-1">
            <GameIcon
              src={iconSrc}
              kind={recording.game === 'Manual Recording' ? 'display' : 'game'}
            />
            <h2 className="card-title !block truncate min-w-0">{recording.game}</h2>
          </div>
          {/* Same size as the menu button on other cards so the rows line up */}
          <span className="btn btn-ghost btn-sm btn-circle invisible" aria-hidden="true" />
        </div>
        <div className="text-sm text-gray-200">
          {formatCreatedAt(new Date(startMs).toISOString())}
        </div>
      </div>
    </div>
  );
}
