import { useLayoutEffect, useRef, useState } from 'react';
import { Reorder } from 'framer-motion';
import { GripVertical, Merge } from 'lucide-react';
import { Content, ContentType } from '../Models/types';
import { useAppState } from '../Context/AppStateContext';
import { contentTypeToFolderName } from '../Utils/FileUtils';
import Button from './Button';

interface MergeModalProps {
  items: Content[];
  slow: boolean; // The videos differ in format, so the merge re-encodes them
  onMerge: (ids: string[]) => void;
  onClose: () => void;
}

const NOUNS: Record<ContentType, string> = {
  Session: 'sessions',
  Buffer: 'replay buffers',
  Clip: 'clips',
  Highlight: 'highlights',
};

// Durations arrive as "hh:mm:ss.fffffff"
const toSeconds = (duration: string) =>
  duration.split(':').reduce((total, part) => total * 60 + (parseFloat(part) || 0), 0);

const formatSeconds = (seconds: number) => {
  const total = Math.round(seconds);
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const secs = (total % 60).toString().padStart(2, '0');
  return hours > 0
    ? `${hours}:${minutes.toString().padStart(2, '0')}:${secs}`
    : `${minutes}:${secs}`;
};

export default function MergeModal({ items, slow, onMerge, onClose }: MergeModalProps) {
  const { cacheFolder } = useAppState();
  const [order, setOrder] = useState(items);
  const listRef = useRef<HTMLUListElement>(null);
  // A row dragged past the edge makes the list overflow, so a list that fits keeps its scrollbar hidden
  const [listFits, setListFits] = useState(true);

  useLayoutEffect(() => {
    const list = listRef.current;
    if (!list) return;
    const measure = () => setListFits(list.scrollHeight <= list.clientHeight);
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(list);
    return () => observer.disconnect();
  }, []);

  const type = items[0]?.type ?? 'Clip';
  const totalSeconds = order.reduce((sum, item) => sum + toSeconds(item.duration), 0);

  const thumbnailUrl = (item: Content) => {
    const thumbnailPath = `${cacheFolder}/thumbnails/${contentTypeToFolderName(item.type)}/${item.id}.jpeg`;
    return `http://localhost:2222/api/thumbnail?input=${encodeURIComponent(thumbnailPath)}`;
  };

  return (
    <>
      <div className="modal-body">
        <div className="flex items-center justify-between gap-4 mb-1">
          <h3 className="font-bold text-2xl">
            Merge {items.length} {NOUNS[type]}
          </h3>
          <Button variant="ghost" size="sm" icon onClick={onClose}>
            ✕
          </Button>
        </div>
        <p className="text-sm text-gray-400 mb-4">
          Drag to change the order.{' '}
          {type === 'Session'
            ? 'The result is saved as a new session and keeps all bookmarks.'
            : slow
              ? 'Their video settings differ, so they are re-encoded into a new clip. This takes a while.'
              : 'The merged video is saved as a new clip.'}
        </p>
        <Reorder.Group
          ref={listRef}
          axis="y"
          values={order}
          onReorder={setOrder}
          layoutScroll
          className={`max-h-[45vh] overflow-y-auto space-y-2 pr-1 ${listFits ? 'no-scrollbar' : ''}`}
        >
          {order.map((item, index) => (
            <Reorder.Item
              key={item.id}
              value={item}
              className="relative flex items-center gap-3 p-2 rounded-lg border border-base-400 bg-base-200 cursor-grab active:cursor-grabbing select-none"
            >
              <span className="w-5 shrink-0 text-center text-sm text-gray-400 tabular-nums">
                {index + 1}
              </span>
              <img
                src={thumbnailUrl(item)}
                alt=""
                className="w-24 aspect-video shrink-0 rounded bg-black object-contain"
                draggable={false}
              />
              <div className="min-w-0 flex-1">
                <div className="truncate text-gray-200">{item.title || item.game}</div>
                <div className="truncate text-xs text-gray-400">
                  {new Date(item.createdAt).toLocaleString(undefined, {
                    dateStyle: 'medium',
                    timeStyle: 'short',
                  })}
                </div>
              </div>
              <span className="shrink-0 text-sm text-gray-300 tabular-nums">
                {formatSeconds(toSeconds(item.duration))}
              </span>
              <GripVertical size={18} className="shrink-0 text-gray-500" />
            </Reorder.Item>
          ))}
        </Reorder.Group>
      </div>
      <div className="modal-action mt-6 flex items-center justify-between">
        <span className="text-sm text-gray-400 tabular-nums">
          Total length {formatSeconds(totalSeconds)}
        </span>
        <Button variant="primary" onClick={() => onMerge(order.map((item) => item.id))}>
          <Merge className="w-5 h-5" />
          {slow ? 'Merge (slow)' : 'Merge'}
        </Button>
      </div>
    </>
  );
}
