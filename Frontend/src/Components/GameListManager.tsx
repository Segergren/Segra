import React, { useState, useEffect } from 'react';
import { Game } from '../Models/types';
import { useSettings } from '../Context/SettingsContext';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { isSelectedGameExecutableMessage } from '../Models/WebSocketMessages';

interface GameListManagerProps {
  listType: 'whitelist' | 'blacklist';
}

export const GameListManager: React.FC<GameListManagerProps> = ({ listType }) => {
  const settings = useSettings();
  
  const [newGameName, setNewGameName] = useState('');
  const [newGamePath, setNewGamePath] = useState('');
  const [isAdding, setIsAdding] = useState(false);
  const [isSelectingFile, setIsSelectingFile] = useState(false);

  const gameList = listType === 'whitelist' ? settings.whitelist : settings.blacklist;
  const listTitle = listType === 'whitelist' ? 'Whitelist' : 'Blacklist';

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<any>) => {
      const message = event.detail;
      
      if (isSelectingFile && isSelectedGameExecutableMessage(message)) {
        const selectedGame = message.content as Game;
        setNewGameName(selectedGame.name);
        setNewGamePath(selectedGame.path);
        setIsSelectingFile(false);
        setIsAdding(true); // Show the form after file is selected
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);

    return () => {
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
    };
  }, [isSelectingFile]);

  const handleSelectExecutable = () => {
    setIsSelectingFile(true);
    sendMessageToBackend('SelectGameExecutable');
  };

  const handleAddGame = () => {
    if (!newGameName.trim() || !newGamePath.trim()) return;

    const newGame: Game = {
      name: newGameName.trim(),
      path: newGamePath.trim()
    };

    // Send to backend
    sendMessageToBackend(
      listType === 'whitelist' ? 'AddToWhitelist' : 'AddToBlacklist', 
      { game: newGame }
    );

    // Reset form
    setNewGameName('');
    setNewGamePath('');
    setIsAdding(false);
  };

  const handleRemoveGame = (game: Game) => {
    sendMessageToBackend(
      listType === 'whitelist' ? 'RemoveFromWhitelist' : 'RemoveFromBlacklist', 
      { game }
    );
  };

  return (
    <div className="bg-base-100 rounded-lg p-5 mb-6 shadow-md border border-custom">
      <div className="mb-5">
        <h2 className="text-xl font-bold">{listTitle}</h2>
      </div>

      {isAdding && (
        <div className="bg-base-300 rounded-lg p-4 mb-4">
          <h3 className="text-lg font-semibold mb-2">Add New Game</h3>
          <div className="flex flex-col gap-3">
            <div>
              <label className="label">
                <span className="label-text">Game Name</span>
              </label>
              <input 
                type="text" 
                className="input input-bordered w-full" 
                value={newGameName}
                onChange={(e) => setNewGameName(e.target.value)}
                placeholder="Enter game name"
              />
            </div>
            <div>
              <label className="label">
                <span className="label-text">Game Path</span>
              </label>
              <div className="flex gap-2">
                <input 
                  type="text" 
                  className="input input-bordered w-full" 
                  value={newGamePath}
                  onChange={(e) => setNewGamePath(e.target.value)}
                  placeholder="Enter game executable path"
                />
                <button 
                  className="btn btn-neutral"
                  onClick={handleSelectExecutable}
                  disabled={isSelectingFile}
                >
                  {isSelectingFile ? (
                    <span className="loading loading-spinner loading-xs"></span>
                  ) : (
                    'Browse'
                  )}
                </button>
              </div>
            </div>
            <div className="flex justify-end gap-2 mt-2">
              <button 
                className="btn btn-sm btn-ghost" 
                onClick={() => setIsAdding(false)}
              >
                Cancel
              </button>
              <button 
                className="btn btn-sm btn-primary" 
                onClick={handleAddGame}
                disabled={!newGameName.trim() || !newGamePath.trim()}
              >
                Add
              </button>
            </div>
          </div>
        </div>
      )}

      <div className="mb-4">
        <div className="overflow-x-auto rounded-lg border border-base-300 bg-base-100 shadow-sm">
          <table className="table w-full">
            <thead className="bg-base-300">
              <tr>
                <th className="font-bold text-base-content w-1/3">Game Name</th>
                <th className="font-bold text-base-content w-1/2">Path</th>
                <th className="font-bold text-base-content w-1/6">Action</th>
              </tr>
            </thead>
            <tbody>
              {gameList.length === 0 ? (
                <tr>
                  <td colSpan={3} className="text-center text-gray-500 py-4 border-b border-base-200">
                    No games in {listTitle.toLowerCase()}
                  </td>
                </tr>
              ) : (
                gameList.map((game, index) => (
                  <tr key={index} className="hover:bg-base-200 border-b border-base-200">
                    <td className="font-medium">{game.name}</td>
                    <td className="text-xs text-gray-400 truncate max-w-xs">{game.path}</td>
                    <td>
                      <button 
                        className="btn btn-sm btn-error" 
                        onClick={() => handleRemoveGame(game)}
                      >
                        Remove
                      </button>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>
      
      {!isAdding && (
        <div className="flex justify-start mt-5">
          <button 
            className="btn btn-sm btn-primary" 
            onClick={handleSelectExecutable}
          >
            <svg xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" strokeWidth={1.5} stroke="currentColor" className="w-5 h-5 mr-1">
              <path strokeLinecap="round" strokeLinejoin="round" d="M12 4.5v15m7.5-7.5h-15" />
            </svg>
            Add Game
          </button>
        </div>
      )}
    </div>
  );
};

export default GameListManager;
