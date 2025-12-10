import { Game } from '../Models/types';
import { MdClose } from 'react-icons/md';

interface GameCardProps {
  game: Game;
  allPaths?: string[]; // Optional: all executable paths for this game (used for pending games with multiple exes)
  type: 'allowed' | 'blocked' | 'pending';
  onAllow: (game: Game) => void;
  onBlock: (game: Game) => void;
  onRemove: (game: Game) => void;
}

export default function GameCard({
  game,
  allPaths,
  type,
  onAllow,
  onBlock,
  onRemove,
}: GameCardProps) {
  const isAllowed = type === 'allowed';
  const isBlocked = type === 'blocked';

  return (
    <div className="bg-base-300/20 bg-opacity-20 border border-base-400 border-opacity-75 rounded-lg px-3 py-3 relative">
      <button
        className="absolute top-2 right-2 btn btn-ghost btn-xs btn-circle"
        onClick={() => onRemove(game)}
      >
        <MdClose size={14} />
      </button>
      <div>
        <div
          className={`text-xs font-semibold mb-1 ${isAllowed ? 'text-green-600' : 'text-red-600'}`}
        >
          {isAllowed ? 'ALLOWED' : isBlocked ? 'BLOCKED' : '\u00A0'}
        </div>
        <div className="font-medium text-sm">{game.name}</div>
        {(() => {
          const paths = allPaths || game.paths || [];
          if (paths.length === 0) return null;

          const truncatePath = (path: string) => {
            const parts = path.replace(/\\/g, '/').split('/');
            return parts.slice(-2).join('\\');
          };

          const displayPaths = paths.map(truncatePath).join(', ');
          return <div className="text-xs text-gray-400 truncate mt-1 mb-2">{displayPaths}</div>;
        })()}
        <div className="flex gap-2 pt-2">
          <button
            className={`btn btn-sm btn-secondary bg-base-300 flex-1 text-gray-400 border-base-400 hover:text-primary hover:border-base-400 hover:bg-green-600/5 ${isAllowed ? 'opacity-50 cursor-default' : ''}`}
            disabled={isAllowed}
            onClick={() => onAllow(game)}
          >
            Allow
          </button>
          <button
            className={`btn btn-sm btn-secondary bg-base-300 flex-1 text-gray-400 border-base-400 hover:text-primary hover:border-base-400 hover:bg-red-600/5 ${isBlocked ? 'opacity-50 cursor-default' : ''}`}
            disabled={isBlocked}
            onClick={() => onBlock(game)}
          >
            Block
          </button>
        </div>
      </div>
    </div>
  );
}
