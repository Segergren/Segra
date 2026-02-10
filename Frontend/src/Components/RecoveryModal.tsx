import { useState, useMemo, useRef, useEffect } from 'react';
import { MdChevronLeft, MdChevronRight } from 'react-icons/md';
import { RecoveryFileData } from '../Models/WebSocketMessages';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { useSettings } from '../Context/SettingsContext';
import Button from './Button';

interface RecoveryModalProps {
  files: RecoveryFileData[];
  onClose: () => void;
}

export default function RecoveryModal({ files, onClose }: RecoveryModalProps) {
  const [currentIndex, setCurrentIndex] = useState(0);
  const [remainingFiles, setRemainingFiles] = useState(files);
  const [gameOverrides, setGameOverrides] = useState<Record<string, string>>({});
  const [inputValue, setInputValue] = useState('');
  const [showDropdown, setShowDropdown] = useState(false);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const settings = useSettings();

  const currentFile = remainingFiles[currentIndex];
  const totalCount = remainingFiles.length;

  const getCurrentGame = (file: RecoveryFileData) => {
    return gameOverrides[file.recoveryId] || file.detectedGame || '';
  };

  const allGames = useMemo(() => {
    return settings.state.gameList.map((game) => game.name).sort();
  }, [settings.state.gameList]);

  const filteredGames = useMemo(() => {
    const query = inputValue.trim().toLowerCase();

    // Show first 10 games if no search query or query is too short
    if (!query || query.length < 2) return allGames.slice(0, 10);

    // First try startsWith
    const startsWithMatches = allGames.filter((game) => game.toLowerCase().startsWith(query));

    // If fewer than 3 results, add includes matches (excluding duplicates)
    if (startsWithMatches.length < 3) {
      const includesMatches = allGames.filter((game) => {
        const gameLower = game.toLowerCase();
        return gameLower.includes(query) && !gameLower.startsWith(query);
      });
      return [...startsWithMatches, ...includesMatches];
    }

    return startsWithMatches;
  }, [inputValue, allGames]);

  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (dropdownRef.current && !dropdownRef.current.contains(event.target as Node)) {
        setShowDropdown(false);
        setInputValue('');
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);

  const handleGameSelect = (game: string) => {
    const newOverrides = { ...gameOverrides, [currentFile.recoveryId]: game };

    if (currentFile.detectedGame) {
      remainingFiles.forEach((file) => {
        if (
          file.detectedGame === currentFile.detectedGame &&
          file.recoveryId !== currentFile.recoveryId
        ) {
          newOverrides[file.recoveryId] = game;
        }
      });
    }

    setGameOverrides(newOverrides);
    setInputValue('');
    setShowDropdown(false);
  };

  const openInExplorer = () => {
    sendMessageToBackend('OpenFileLocation', { FilePath: currentFile.filePath });
  };

  const handleAction = (action: 'recover' | 'delete' | 'skip') => {
    const selectedGame = getCurrentGame(currentFile);

    sendMessageToBackend('RecoveryConfirm', {
      recoveryId: currentFile.recoveryId,
      action,
      gameOverride: selectedGame || undefined,
    });

    const newRemainingFiles = remainingFiles.filter((_, idx) => idx !== currentIndex);

    if (newRemainingFiles.length === 0) {
      onClose();
      return;
    }

    setRemainingFiles(newRemainingFiles);
    if (currentIndex >= newRemainingFiles.length) {
      setCurrentIndex(newRemainingFiles.length - 1);
    }
  };

  const goToPrevious = () => {
    if (currentIndex > 0) {
      setCurrentIndex(currentIndex - 1);
    }
  };

  const goToNext = () => {
    if (currentIndex < totalCount - 1) {
      setCurrentIndex(currentIndex + 1);
    }
  };

  return (
    <>
      <div className="modal-header pb-4 border-b border-gray-700">
        <div className="flex items-center justify-between w-full pr-8">
          <h2 className="font-bold text-3xl mb-0 text-warning">Recover Video Files?</h2>
          <div className="flex items-center gap-2">
            <Button variant="ghost" size="sm" onClick={goToPrevious} disabled={currentIndex === 0}>
              <MdChevronLeft size={20} />
            </Button>
            <span className="text-gray-400 text-sm">
              {currentIndex + 1} of {totalCount}
            </span>
            <Button
              variant="ghost"
              size="sm"
              onClick={goToNext}
              disabled={currentIndex === totalCount - 1}
            >
              <MdChevronRight size={20} />
            </Button>
          </div>
        </div>
        <Button
          variant="ghost"
          size="sm"
          icon
          className="absolute right-4 top-4 z-10"
          onClick={onClose}
        >
          ✕
        </Button>
      </div>

      <div className="modal-body py-2 mt-4">
        <div className="text-gray-300 text-lg mb-4">
          Found video file without metadata:
          <div className="font-semibold mt-2">{currentFile.fileName}</div>
          <div className="text-sm text-gray-400 mt-1">This may be from a crashed recording.</div>
        </div>

        <div className="bg-base-200 rounded-lg p-4 space-y-2">
          <div className="flex justify-between items-center">
            <span className="text-gray-400">Type</span>
            <span className="text-gray-200 font-medium">{currentFile.typeLabel}</span>
          </div>
          <div className="flex justify-between items-center">
            <span className="text-gray-400">Size</span>
            <span className="text-gray-200 font-medium">{currentFile.fileSize}</span>
          </div>
          <div className="flex justify-between items-center">
            <span className="text-gray-400">Game</span>
            <div className="relative" ref={dropdownRef}>
              <input
                type="text"
                className="input input-sm bg-base-300 border-base-400 text-gray-200 font-medium w-[200px]"
                placeholder={getCurrentGame(currentFile) || 'Search games...'}
                value={inputValue}
                onChange={(e) => {
                  setInputValue(e.target.value);
                  setShowDropdown(true);
                }}
                onFocus={() => {
                  setShowDropdown(true);
                }}
              />
              {showDropdown && filteredGames.length > 0 && inputValue.trim().length >= 2 && (
                <div className="absolute z-50 w-full mt-1 bg-base-200 border border-base-400 rounded-lg shadow-lg max-h-[7.5rem] overflow-y-auto">
                  {filteredGames.map((game) => {
                    const isSelected = game === getCurrentGame(currentFile);
                    return (
                      <div
                        key={game}
                        className={`p-2 hover:bg-base-300 cursor-pointer border-b border-base-300 last:border-b-0 text-sm truncate ${isSelected ? 'bg-accent/20 text-accent font-medium' : ''}`}
                        onClick={() => handleGameSelect(game)}
                        title={game}
                      >
                        {game}
                      </div>
                    );
                  })}
                </div>
              )}
            </div>
          </div>
          <div className="flex justify-between items-center">
            <span className="text-gray-400">Location</span>
            <button
              onClick={openInExplorer}
              className="text-blue-400 hover:text-blue-300 underline text-sm"
            >
              Open in File Explorer
            </button>
          </div>
        </div>
      </div>

      <div className="modal-action mt-6 flex justify-end gap-3">
        <Button variant="success" onClick={() => handleAction('recover')}>
          Recover
        </Button>
        <Button variant="danger" onClick={() => handleAction('delete')}>
          Delete
        </Button>
        <Button variant="primary" onClick={() => handleAction('skip')}>
          Skip
        </Button>
      </div>
    </>
  );
}
