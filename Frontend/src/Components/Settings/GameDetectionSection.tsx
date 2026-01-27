import { useState, useMemo, useRef, useEffect } from 'react';
import { useSettings } from '../../Context/SettingsContext';
import { GameListEntry, Game } from '../../Models/types';
import { sendMessageToBackend } from '../../Utils/MessageUtils';
import { MdSearch } from 'react-icons/md';
import { useModal } from '../../Context/ModalContext';
import GameCard from '../GameCard';
import CustomGameModal from '../CustomGameModal';

export default function GameDetectionSection() {
  const settings = useSettings();
  const [searchQuery, setSearchQuery] = useState('');
  const [pendingGames, setPendingGames] = useState<Game[]>([]);
  const [showDropdown, setShowDropdown] = useState(false);
  const searchRef = useRef<HTMLDivElement>(null);
  const { openModal, closeModal } = useModal();

  // Close dropdown when clicking outside
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (searchRef.current && !searchRef.current.contains(event.target as Node)) {
        setShowDropdown(false);
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);

  // Filter games based on search query
  const filteredGames = useMemo(() => {
    if (!searchQuery.trim()) return [];

    const query = searchQuery.toLowerCase();
    return settings.state.gameList
      .filter((game) => {
        const matchesQuery = game.name.toLowerCase().includes(query);
        const hasExeInWhitelist = settings.whitelist.some((g) =>
          g.paths?.some((path) => game.executables.includes(path)),
        );
        const hasExeInBlacklist = settings.blacklist.some((g) =>
          g.paths?.some((path) => game.executables.includes(path)),
        );
        const hasExeInPending = pendingGames.some((g) =>
          g.paths?.some((path) => game.executables.includes(path)),
        );
        return matchesQuery && !hasExeInWhitelist && !hasExeInBlacklist && !hasExeInPending;
      })
      .slice(0, 100);
  }, [searchQuery, settings.state.gameList, settings.whitelist, settings.blacklist, pendingGames]);

  const handleGameSelect = (game: GameListEntry) => {
    const newGame: Game = {
      name: game.name,
      paths: game.executables,
    };

    const isInWhitelist = settings.whitelist.some((g) => g.name === newGame.name);
    const isInBlacklist = settings.blacklist.some((g) => g.name === newGame.name);
    const isInPending = pendingGames.some((g) => g.name === newGame.name);

    if (!isInWhitelist && !isInBlacklist && !isInPending) {
      setPendingGames([...pendingGames, newGame]);
    }

    setSearchQuery('');
    setShowDropdown(false);
  };

  const handleAllowPending = (game: Game) => {
    sendMessageToBackend('AddToWhitelist', { game });
    setPendingGames(pendingGames.filter((g) => g.name !== game.name));
  };

  const handleBlockPending = (game: Game) => {
    sendMessageToBackend('AddToBlacklist', { game });
    setPendingGames(pendingGames.filter((g) => g.name !== game.name));
  };

  const handleRemovePending = (game: Game) => {
    setPendingGames(pendingGames.filter((g) => g.name !== game.name));
  };

  const handleRemoveFromWhitelist = (game: Game) => {
    sendMessageToBackend('RemoveFromWhitelist', { game });
  };

  const handleRemoveFromBlacklist = (game: Game) => {
    sendMessageToBackend('RemoveFromBlacklist', { game });
  };

  const handleMoveToWhitelist = (game: Game) => {
    sendMessageToBackend('MoveGame', { game, targetList: 'whitelist' });
  };

  const handleMoveToBlacklist = (game: Game) => {
    sendMessageToBackend('MoveGame', { game, targetList: 'blacklist' });
  };

  const handleAddCustomGame = () => {
    openModal(
      <CustomGameModal
        onSave={(game) => setPendingGames([...pendingGames, game])}
        onClose={closeModal}
      />,
    );
  };

  const allGames = useMemo(() => {
    const combined = [
      ...settings.whitelist.map((game) => ({ ...game, type: 'allowed' as const })),
      ...settings.blacklist.map((game) => ({ ...game, type: 'blocked' as const })),
    ];
    return combined.sort((a, b) => a.name.localeCompare(b.name));
  }, [settings.whitelist, settings.blacklist]);

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl font-semibold mb-2">Game Detection</h2>
      <p className="text-sm opacity-80 mb-4">
        Search for a game and add it to your Allow List or Block List. Segra auto-detects most
        games, but you can manually add games if needed.
      </p>

      {/* Search Bar with Add Custom Game Button */}
      <div className="mb-6 relative" ref={searchRef}>
        <label className="label pb-1">
          <span className="label-text text-base-content font-semibold">Search Games</span>
        </label>
        <div className="join w-full">
          <div className="relative flex-1 join-item">
            <div className="absolute inset-y-0 left-0 flex items-center pl-3 pointer-events-none">
              <MdSearch className="text-base-content opacity-50 z-10" size={20} />
            </div>
            <input
              type="text"
              className="input input-bordered w-full pl-10 bg-base-200 join-item"
              placeholder="Search for a game..."
              value={searchQuery}
              onChange={(e) => {
                setSearchQuery(e.target.value);
                setShowDropdown(true);
              }}
              onFocus={() => setShowDropdown(true)}
            />
          </div>
          <button
            className="btn btn-secondary bg-base-200 hover:bg-base-300 border-base-400 hover:border-base-400 border-l-0 font-semibold join-item"
            onClick={handleAddCustomGame}
          >
            Add Custom
          </button>
        </div>

        {/* Dropdown Results */}
        {showDropdown && filteredGames.length > 0 && (
          <div className="absolute z-10 w-full mt-1 bg-base-200 border border-base-400 rounded-lg shadow-lg max-h-64 overflow-y-auto">
            {filteredGames.map((game, index) => (
              <div
                key={index}
                className="p-3 hover:bg-base-300 cursor-pointer border-b border-base-300 last:border-b-0"
                onClick={() => handleGameSelect(game)}
              >
                <div className="font-medium">{game.name}</div>
                <div className="text-xs text-gray-400 mt-1">{game.executables[0]}</div>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Combined Allow List and Block List */}
      <div className="bg-base-200 p-4 rounded-lg border border-custom">
        <h3 className="text-lg font-semibold mb-2">Allowed & Blocked Games</h3>
        <p className="text-xs opacity-70 mb-3">
          Games in your allow list are forced to be detected. Games in your block list are prevented
          from being recorded.
        </p>

        {allGames.length === 0 && pendingGames.length === 0 ? (
          <div className="text-center text-gray-500 py-8">No games in allow or block list</div>
        ) : (
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3 max-h-[400px] overflow-y-auto pr-2">
            {/* Pending Games */}
            {pendingGames.map((game, index) => (
              <GameCard
                key={`pending-${index}`}
                game={game}
                allPaths={game.paths}
                type="pending"
                onAllow={handleAllowPending}
                onBlock={handleBlockPending}
                onRemove={handleRemovePending}
              />
            ))}

            {/* Combined Allow & Block List (sorted by name) */}
            {allGames.map((game, index) => (
              <GameCard
                key={`${game.type}-${index}`}
                game={game}
                type={game.type}
                onAllow={handleMoveToWhitelist}
                onBlock={handleMoveToBlacklist}
                onRemove={
                  game.type === 'allowed' ? handleRemoveFromWhitelist : handleRemoveFromBlacklist
                }
              />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
