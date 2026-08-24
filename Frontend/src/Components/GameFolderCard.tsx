import { useEffect, useState } from 'react';
import { Content, GameResponse } from '../Models/types';
import { Gamepad2, HardDrive, Clock } from 'lucide-react';

const coverUrlCache = new Map<string, string | null>();

function formatFileSize(totalKb: number): string {
  const mb = totalKb / 1024;
  if (mb > 1000) {
    return `${(mb / 1024).toFixed(2)} GB`;
  }
  return `${mb.toFixed(2)} MB`;
}

interface GameFolderCardProps {
  game: string;
  items: Content[];
  onClick: () => void;
}

export default function GameFolderCard({ game, items, onClick }: GameFolderCardProps) {
  const [coverUrl, setCoverUrl] = useState<string | null>(() => coverUrlCache.get(game) ?? null);

  useEffect(() => {
    if (!game || game === 'Manual Recording' || game === 'Unknown') {
      setCoverUrl(null);
      return;
    }

    if (coverUrlCache.has(game)) {
      setCoverUrl(coverUrlCache.get(game) ?? null);
      return;
    }

    let cancelled = false;
    (async () => {
      try {
        const response = await fetch(
          `https://segra.tv/api/games/search?name=${encodeURIComponent(game)}`,
        );
        if (!response.ok) throw new Error('Game not found');
        const data: GameResponse = await response.json();
        const url = data.game?.cover?.image_id
          ? `https://segra.tv/api/games/cover/${data.game.cover.image_id}`
          : null;
        coverUrlCache.set(game, url);
        if (!cancelled) setCoverUrl(url);
      } catch {
        coverUrlCache.set(game, null);
        if (!cancelled) setCoverUrl(null);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [game]);

  const totalSizeKb = items.reduce((sum, item) => sum + (item.fileSizeKb ?? 0), 0);
  const latestCreatedAt = items.reduce(
    (latest, item) => (new Date(item.createdAt) > new Date(latest) ? item.createdAt : latest),
    items[0]?.createdAt ?? '',
  );

  return (
    <div
      className="card card-compact bg-base-300 text-gray-300 w-full border border-[#49515b] cursor-pointer"
      onClick={onClick}
    >
      <figure className="relative aspect-video bg-black">
        {coverUrl ? (
          <img src={coverUrl} alt={game} className="w-full h-full object-cover" draggable={false} />
        ) : (
          <div className="w-full h-full flex items-center justify-center bg-base-200/50">
            <Gamepad2 size={48} className="text-base-content opacity-40" />
          </div>
        )}
        <span className="absolute bottom-2 right-2 bg-black/75 text-white text-xs px-2 py-1 rounded">
          {items.length} {items.length === 1 ? 'item' : 'items'}
        </span>
      </figure>
      <div className="card-body gap-1 pt-2">
        <h2 className="card-title !block truncate">{game}</h2>
        <div className="text-sm text-gray-200 flex items-center justify-between w-full">
          <span className="flex items-center gap-1">
            <HardDrive size={14} />
            {formatFileSize(totalSizeKb)}
          </span>
          <span className="flex items-center gap-1">
            <Clock size={14} />
            {latestCreatedAt ? new Date(latestCreatedAt).toLocaleDateString() : '-'}
          </span>
        </div>
      </div>
    </div>
  );
}
