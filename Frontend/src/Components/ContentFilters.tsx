import { useEffect, useState } from 'react';
import { ArrowUpDown, Clock, HardDrive, Timer, Gamepad2, FileUp, Monitor } from 'lucide-react';
import DropdownSelect from './DropdownSelect';

export type SortOption = 'newest' | 'oldest' | 'size' | 'duration' | 'game';

export const ALL_GAMES = '__all__';
export const IMPORTED = 'Imported';

export interface GameOption {
  key: string;
  name: string;
  icon: string | null;
  count: number;
}

export interface ContentFiltersProps {
  games: GameOption[];
  totalCount: number;
  onGameFilterChange: (selectedGame: string) => void;
  onSortChange: (sortOption: SortOption) => void;
  sectionId: string;
  selectedGame: string;
  sortOption: SortOption;
}

function GameIcon({ src }: { src: string | null }) {
  const [failed, setFailed] = useState(false);
  if (!src || failed) return <Gamepad2 size={18} className="shrink-0 text-base-content/60" />;
  return (
    <img
      src={src}
      alt=""
      className="w-5 h-5 rounded object-cover shrink-0"
      onError={() => setFailed(true)}
    />
  );
}

function GameLabel({ icon, name, count }: { icon: React.ReactNode; name: string; count: number }) {
  return (
    <span className="flex items-center justify-between gap-3 w-full min-w-0">
      <span className="flex items-center gap-2.5 min-w-0">
        {icon}
        <span className="truncate">{name}</span>
      </span>
      <span className="text-xs text-base-content/50 shrink-0">{count}</span>
    </span>
  );
}

export default function ContentFilters({
  games,
  totalCount,
  onGameFilterChange,
  onSortChange,
  sectionId,
  selectedGame,
  sortOption,
}: ContentFiltersProps) {
  // Persist changes to localStorage
  useEffect(() => {
    try {
      localStorage.setItem(`${sectionId}-filters`, JSON.stringify(selectedGame));
      localStorage.setItem(`${sectionId}-sort`, JSON.stringify(sortOption));
    } catch {
      /* no-op */
    }
  }, [selectedGame, sortOption, sectionId]);

  const handleSortChange = (option: SortOption) => {
    onSortChange(option);
    // Close the dropdown by blurring the active element (DaisyUI closes on blur)
    try {
      (document.activeElement as HTMLElement)?.blur();
    } catch {
      /* no-op */
    }
  };

  const getSortLabel = (option: SortOption): string => {
    switch (option) {
      case 'newest':
        return 'Newest';
      case 'oldest':
        return 'Oldest';
      case 'size':
        return 'Size';
      case 'duration':
        return 'Duration';
      case 'game':
        return 'Game';
    }
  };

  const gameItems = [
    {
      value: ALL_GAMES,
      label: (
        <GameLabel
          icon={<Gamepad2 size={18} className="shrink-0 text-base-content/60" />}
          name="All games"
          count={totalCount}
        />
      ),
    },
    ...games.map((g) => ({
      value: g.key,
      label: (
        <GameLabel
          icon={
            g.key === IMPORTED ? (
              <FileUp size={18} className="shrink-0 text-base-content/60" />
            ) : g.key === 'Manual Recording' ? (
              <Monitor size={18} className="shrink-0 text-base-content/60" />
            ) : (
              <GameIcon src={g.icon} />
            )
          }
          name={g.name}
          count={g.count}
        />
      ),
    })),
  ];

  return (
    <div className="flex items-center gap-2">
      {/* Sort dropdown */}
      <div className="dropdown dropdown-end">
        <button
          disabled={totalCount === 0}
          className={`btn btn-sm no-animation btn-secondary border border-base-400 hover:text-primary hover:border-base-400 flex items-center gap-1 text-gray-300 h-8 ${totalCount === 0 ? 'cursor-not-allowed opacity-50' : 'cursor-pointer'}`}
        >
          <ArrowUpDown size={16} />
          {getSortLabel(sortOption)}
        </button>
        <ul
          className="dropdown-content menu bg-base-300 border border-base-400 rounded-box z-999 w-56 p-2 mt-1 shadow"
          tabIndex={0}
        >
          <li>
            <a
              className={`flex w-full items-center gap-2 px-4 py-3 ${
                sortOption === 'newest' ? 'text-primary' : 'text-white'
              } hover:bg-white/5 active:text-primary! active:bg-white/5! rounded-lg transition-all duration-200 hover:pl-5 outline-none`}
              onClick={() => handleSortChange('newest')}
            >
              <Clock size={20} />
              <span>Newest</span>
            </a>
          </li>
          <li>
            <a
              className={`flex w-full items-center gap-2 px-4 py-3 ${
                sortOption === 'oldest' ? 'text-primary' : 'text-white'
              } hover:bg-white/5 active:text-primary! active:bg-white/5! rounded-lg transition-all duration-200 hover:pl-5 outline-none`}
              onClick={() => handleSortChange('oldest')}
            >
              <Clock size={20} />
              <span>Oldest</span>
            </a>
          </li>
          <li>
            <a
              className={`flex w-full items-center gap-2 px-4 py-3 ${
                sortOption === 'size' ? 'text-primary' : 'text-white'
              } hover:bg-white/5 active:text-primary! active:bg-white/5! rounded-lg transition-all duration-200 hover:pl-5 outline-none`}
              onClick={() => handleSortChange('size')}
            >
              <HardDrive size={20} />
              <span>Size</span>
            </a>
          </li>
          <li>
            <a
              className={`flex w-full items-center gap-2 px-4 py-3 ${
                sortOption === 'duration' ? 'text-primary' : 'text-white'
              } hover:bg-white/5 active:text-primary! active:bg-white/5! rounded-lg transition-all duration-200 hover:pl-5 outline-none`}
              onClick={() => handleSortChange('duration')}
            >
              <Timer size={20} />
              <span>Duration</span>
            </a>
          </li>
          <li>
            <a
              className={`flex w-full items-center gap-2 px-4 py-3 ${
                sortOption === 'game' ? 'text-primary' : 'text-white'
              } hover:bg-white/5 active:text-primary! active:bg-white/5! rounded-lg transition-all duration-200 hover:pl-5 outline-none`}
              onClick={() => handleSortChange('game')}
            >
              <Gamepad2 size={20} />
              <span>Game A–Z</span>
            </a>
          </li>
        </ul>
      </div>
      {/* Game filter */}
      <div className="w-55">
        <DropdownSelect
          size="sm"
          items={gameItems}
          value={selectedGame}
          onChange={onGameFilterChange}
          disabled={totalCount === 0}
          buttonContent={
            <span className="flex-1 min-w-0 text-left font-medium">
              {(gameItems.find((item) => item.value === selectedGame) ?? gameItems[0]).label}
            </span>
          }
          buttonClassName={`btn btn-sm no-animation btn-secondary border border-base-400 h-8 hover:text-primary hover:*:text-primary text-gray-300 w-full justify-between ${totalCount === 0 ? 'cursor-not-allowed opacity-50' : 'cursor-pointer'}`}
          itemClassName="flex items-center justify-start gap-2 px-3 py-2.5 text-sm hover:bg-white/5 rounded-lg transition-all duration-200 hover:pl-4"
        />
      </div>
    </div>
  );
}
