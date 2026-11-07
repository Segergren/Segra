import { useEffect, useState } from 'react';
import { Recording } from '../Models/types';
import { sendMessageToBackend } from '../Utils/MessageUtils';

const formatElapsed = (seconds: number) => {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = Math.floor(seconds % 60);
  const mm = String(m).padStart(2, '0');
  const ss = String(s).padStart(2, '0');
  return h > 0 ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
};

// The session being recorded; opening it plays a stream-copied snapshot of the file so far
export default function LiveSessionCard({ recording }: { recording: Recording }) {
  const startMs = new Date(recording.startTime).getTime();
  const [elapsed, setElapsed] = useState(() => (Date.now() - startMs) / 1000);
  const [thumbnailTime, setThumbnailTime] = useState<number | null>(null);
  const [thumbnailLoaded, setThumbnailLoaded] = useState(false);
  const [opening, setOpening] = useState(false);

  useEffect(() => {
    const tick = () => setElapsed((Date.now() - startMs) / 1000);
    const id = setInterval(tick, 1000);
    return () => clearInterval(id);
  }, [startMs]);

  // A fresh frame every 30 s, a few seconds behind the live edge
  useEffect(() => {
    const update = () => {
      const t = (Date.now() - startMs) / 1000 - 5;
      if (t > 0) setThumbnailTime(Math.floor(t));
    };
    update();
    const id = setInterval(update, 30000);
    return () => clearInterval(id);
  }, [startMs]);

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

  const thumbnailUrl =
    recording.filePath && thumbnailTime !== null
      ? `http://localhost:2222/api/thumbnail?input=${encodeURIComponent(recording.filePath)}&time=${thumbnailTime}`
      : undefined;

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
          className={`absolute inset-0 rounded-none bg-base-200/75 transition-opacity duration-200 ${thumbnailLoaded ? 'opacity-0' : 'skeleton opacity-100'}`}
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
            onError={() => setThumbnailLoaded(true)}
          />
        )}
        <span className="absolute top-2 left-2 flex items-center gap-1.5 bg-black/75 text-white text-xs px-2 py-1 rounded">
          <span className="h-2 w-2 rounded-full bg-red-500 animate-pulse" />
          REC
        </span>
        <span className="absolute bottom-2 right-2 bg-black/75 text-white text-xs px-2 py-1 rounded tabular-nums">
          {formatElapsed(elapsed)}
        </span>
        {opening && (
          <div className="absolute inset-0 bg-black/70 flex items-center justify-center gap-2 text-sm text-white">
            <span className="loading loading-spinner loading-xs" />
            Opening...
          </div>
        )}
      </figure>

      <div className="card-body gap-1 pt-2 border border-t-0 border-[#49515b] rounded-b-[inherit]">
        <div className="flex justify-between items-center min-h-8">
          <h2 className="card-title !block truncate">{recording.game}</h2>
        </div>
        <div className="text-sm text-gray-200">Recording now &bull; click to watch</div>
      </div>
    </div>
  );
}
