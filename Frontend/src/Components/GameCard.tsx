import { Game } from '../Models/types';
import { MdClose } from 'react-icons/md';
import Button from './Button';

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
      <Button
        variant="ghost"
        size="xs"
        icon
        className="absolute top-2 right-2"
        onClick={() => onRemove(game)}
      >
        <MdClose size={14} />
      </Button>
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
          <Button
            variant="primary"
            size="sm"
            className={`flex-1 bg-base-300 ${isAllowed ? 'opacity-50 cursor-default' : ''}`}
            disabled={isAllowed}
            onClick={() => onAllow(game)}
          >
            Allow
          </Button>
          <Button
            variant="primary"
            size="sm"
            className={`flex-1 bg-base-300 ${isBlocked ? 'opacity-50 cursor-default' : ''}`}
            disabled={isBlocked}
            onClick={() => onBlock(game)}
          >
            Block
          </Button>
        </div>
      </div>
    </div>
  );
}
