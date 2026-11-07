import { useEffect, useState } from 'react';
import { Recording } from '../Models/types';
import { sendMessageToBackend } from '../Utils/MessageUtils';

// One frame per recording, kept across remounts so it is only generated once
const thumbnailCache = new Map<string, string>();

// The session being recorded; opening it plays a stream-copied snapshot of the file so far
export default function LiveSessionCard({
  recording,
  isFinishing = false,
}: {
  recording: Recording;
  isFinishing?: boolean;
}) {
  const startMs = new Date(recording.startTime).getTime();
  const filePath = recording.filePath;
  const [thumbnailUrl, setThumbnailUrl] = useState(() =>
    filePath ? thumbnailCache.get(filePath) : undefined,
  );
  const [thumbnailLoaded, setThumbnailLoaded] = useState(() => !!thumbnailUrl);
  const [opening, setOpening] = useState(false);

  // Retried only until the file has enough data to read
  useEffect(() => {
    if (thumbnailUrl || !filePath) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout>;
    const attempt = async () => {
      const time = Math.max(0, Math.floor((Date.now() - startMs) / 1000 - 5));
      try {
        const response = await fetch(
          `http://localhost:2222/api/thumbnail?input=${encodeURIComponent(filePath)}&time=${time}&width=960`,
        );
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const blob = await response.blob();
        if (cancelled) return;
        const objectUrl = URL.createObjectURL(blob);
        thumbnailCache.set(filePath, objectUrl);
        setThumbnailUrl(objectUrl);
      } catch {
        if (!cancelled) timer = setTimeout(attempt, 5000);
      }
    };
    timer = setTimeout(attempt, Math.max(0, 10 - (Date.now() - startMs) / 1000) * 1000);
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
      className={`card card-compact bg-base-300 text-gray-300 w-full ${opening || isFinishing ? 'cursor-default' : 'cursor-pointer'}`}
      onClick={() => {
        if (opening || isFinishing) return;
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
            alt="thumbnail"
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
            REC
          </span>
        )}
        {(opening || isFinishing) && (
          <div className="absolute inset-0 bg-black/70 flex items-center justify-center gap-2 text-sm text-white">
            <span className="loading loading-spinner loading-xs" />
            {isFinishing ? 'Finishing...' : 'Opening...'}
          </div>
        )}
      </figure>

      <div className="card-body gap-1 pt-2 border border-t-0 border-[#49515b] rounded-b-[inherit]">
        <div className="flex justify-between items-center">
          <h2 className="card-title !block truncate">{recording.game}</h2>
          {/* Same size as the menu button on other cards so the rows line up */}
          <span className="btn btn-ghost btn-sm btn-circle invisible" aria-hidden="true" />
        </div>
        <div className="text-sm text-gray-200">
          Started{' '}
          {new Date(startMs).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}{' '}
          &bull; {new Date(startMs).toLocaleDateString()}
        </div>
      </div>
    </div>
  );
}
