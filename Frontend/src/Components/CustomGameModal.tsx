import { useState, useEffect } from 'react';
import { Game } from '../Models/types';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { isSelectedGameExecutableMessage } from '../Models/WebSocketMessages';
import { MdFolderOpen, MdAdd } from 'react-icons/md';

interface CustomGameModalProps {
  onSave: (game: Game) => void;
  onClose: () => void;
}

export default function CustomGameModal({ onSave, onClose }: CustomGameModalProps) {
  const [customGameName, setCustomGameName] = useState('');
  const [customGamePaths, setCustomGamePaths] = useState<string[]>([]);
  const [isSelectingFile, setIsSelectingFile] = useState(false);

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<any>) => {
      const message = event.detail;

      if (isSelectingFile && isSelectedGameExecutableMessage(message)) {
        const selectedGame = message.content as Game;
        const newPath = selectedGame.paths?.[0] || '';
        if (newPath && !customGamePaths.includes(newPath)) {
          setCustomGamePaths([...customGamePaths, newPath]);
          if (!customGameName) {
            setCustomGameName(selectedGame.name);
          }
        }
        setIsSelectingFile(false);
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);
    return () =>
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
  }, [isSelectingFile, customGamePaths, customGameName]);

  const handleBrowseExecutable = () => {
    setIsSelectingFile(true);
    sendMessageToBackend('SelectGameExecutable');
  };

  const handleRemoveCustomPath = (pathToRemove: string) => {
    setCustomGamePaths(customGamePaths.filter((p) => p !== pathToRemove));
  };

  const handleSave = () => {
    if (!customGameName.trim() || customGamePaths.length === 0) return;

    const newGame: Game = {
      name: customGameName.trim(),
      paths: customGamePaths,
    };

    onSave(newGame);
    onClose();
  };

  return (
    <>
      <div className="bg-base-300">
        <div className="modal-header">
          <button
            className="btn btn-circle btn-ghost absolute right-4 top-1 z-10 text-lg hover:bg-white/10"
            onClick={onClose}
          >
            ✕
          </button>
        </div>
        <div className="modal-body pt-8">
          <h3 className="font-bold text-2xl mb-6">Add Custom Game</h3>

          <div className="form-control w-full mb-4">
            <label className="label">
              <span className="label-text text-base-content">Game Name</span>
            </label>
            <input
              type="text"
              className="input input-bordered bg-base-300 w-full focus:outline-none"
              placeholder="Enter game name..."
              value={customGameName}
              onChange={(e) => setCustomGameName(e.target.value)}
            />
          </div>

          <div className="form-control w-full">
            <label className="label">
              <span className="label-text text-base-content">Executables</span>
            </label>
            <button
              className="btn btn-secondary bg-base-300 border-base-400 hover:border-base-400 hover:bg-base-200 w-full flex items-center gap-2"
              onClick={handleBrowseExecutable}
              disabled={isSelectingFile}
            >
              <MdFolderOpen size={18} />
              {isSelectingFile ? 'Selecting...' : 'Browse Executable'}
            </button>

            {customGamePaths.length > 0 && (
              <div className="mt-3 space-y-2">
                {customGamePaths.map((path, idx) => (
                  <div
                    key={idx}
                    className="flex items-center gap-2 bg-base-200 p-2 rounded-lg border border-base-400"
                  >
                    <span className="text-xs flex-1 truncate text-gray-300">{path}</span>
                    <button
                      className="btn btn-ghost btn-xs hover:bg-error/20 hover:text-error"
                      onClick={() => handleRemoveCustomPath(path)}
                    >
                      ✕
                    </button>
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
        <div className="modal-action mt-6">
          <button
            className="btn btn-secondary bg-base-300 h-10 text-gray-400 border-base-400 hover:text-primary hover:border-base-400 hover:bg-base-300 flex items-center gap-1 w-full"
            onClick={handleSave}
            disabled={!customGameName.trim() || customGamePaths.length === 0}
          >
            <MdAdd className="w-5 h-5" />
            Add Game
          </button>
        </div>
      </div>
    </>
  );
}
