import { useEffect, useState } from 'react';
import { Recording } from '../Models/types';
import { sendMessageToBackend } from '../Utils/MessageUtils';

// The session being recorded; opening it plays a stream-copied snapshot of the file so far
export default function LiveSessionCard({ recording }: { recording: Recording }) {
  // One frame a few seconds behind the live edge, taken when the card appears
  const [thumbnailTime] = useState(() =>
    Math.max(0, Math.floor((Date.now() - new Date(recording.startTime).getTime()) / 1000 - 5)),
  );
  const [thumbnailLoaded, setThumbnailLoaded] = useState(false);
  const [opening, setOpening] = useState(false);

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

  const thumbnailUrl = recording.filePath
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
          <span className="h-2 w-2 rounded-full bg-red-500" />
          REC
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
        <div className="text-sm text-gray-200">Recording now</div>
      </div>
    </div>
  );
}
