import { useState, useMemo, useRef, useEffect } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import { ChevronLeft, ChevronRight } from 'lucide-react';
import { RecoveryFileData } from '../Models/WebSocketMessages';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { useAppState } from '../Context/AppStateContext';
import Button from './Button';
import ConfirmationModal from './ConfirmationModal';
import { useSettings } from '../Context/SettingsContext';

interface RecoveryModalProps {
  files: RecoveryFileData[];
  onClose: () => void;
}

export default function RecoveryModal({ files, onClose }: RecoveryModalProps) {
  const { t } = useTranslation('modals');
  const settings = useSettings();
  const [currentIndex, setCurrentIndex] = useState(0);
  const [remainingFiles, setRemainingFiles] = useState(files);
  const [gameOverrides, setGameOverrides] = useState<Record<string, string>>({});
  const [inputValue, setInputValue] = useState('');
  const [showDropdown, setShowDropdown] = useState(false);
  const [isConfirmingDelete, setIsConfirmingDelete] = useState(false);
  const dropdownRef = useRef<HTMLDivElement>(null);
  const appState = useAppState();

  const currentFile = remainingFiles[currentIndex];
  const totalCount = remainingFiles.length;

  const getCurrentGame = (file: RecoveryFileData) => {
    return gameOverrides[file.recoveryId] || file.detectedGame || '';
  };

  const allGames = useMemo(() => {
    return appState.gameList.map((game) => game.name).sort();
  }, [appState.gameList]);

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

  if (isConfirmingDelete) {
    return (
      <ConfirmationModal
        title={t('recovery.deleteConfirm.title')}
        description={
          <>
            <Trans
              ns="modals"
              i18nKey="recovery.deleteConfirm.description"
              values={{ fileName: currentFile.fileName }}
              components={{ strong: <strong /> }}
            />
            <br />
            <span className="text-sm text-gray-400">
              {t('recovery.deleteConfirm.irreversible')}
            </span>
          </>
        }
        confirmText={t('recovery.actions.delete')}
        onConfirm={() => {
          setIsConfirmingDelete(false);
          handleAction('delete');
        }}
        onCancel={() => setIsConfirmingDelete(false)}
      />
    );
  }

  return (
    <>
      <div className="modal-header pb-4 border-b border-gray-700">
        <div className="flex items-center justify-between w-full pr-8">
          <h2 className="font-bold text-3xl mb-0 text-warning">{t('recovery.title')}</h2>
          <div className="flex items-center gap-2">
            <Button variant="ghost" size="sm" onClick={goToPrevious} disabled={currentIndex === 0}>
              <ChevronLeft size={20} />
            </Button>
            <span className="text-gray-400 text-sm">
              {t('recovery.pageOf', { current: currentIndex + 1, total: totalCount })}
            </span>
            <Button
              variant="ghost"
              size="sm"
              onClick={goToNext}
              disabled={currentIndex === totalCount - 1}
            >
              <ChevronRight size={20} />
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
          {t('recovery.foundWithoutMetadata')}
          <div className="font-semibold mt-2">{currentFile.fileName}</div>
          <div className="text-sm text-gray-400 mt-1">{t('recovery.crashedRecording')}</div>
        </div>

        <div className="bg-base-200 rounded-lg p-4 space-y-2">
          <div className="flex justify-between items-center">
            <span className="text-gray-400">{t('recovery.type')}</span>
            <span className="text-gray-200 font-medium">{currentFile.typeLabel}</span>
          </div>
          <div className="flex justify-between items-center">
            <span className="text-gray-400">{t('recovery.size')}</span>
            <span className="text-gray-200 font-medium">{currentFile.fileSize}</span>
          </div>
          <div className="flex justify-between items-center">
            <span className="text-gray-400">{t('recovery.game')}</span>
            <div className="relative" ref={dropdownRef}>
              <input
                type="text"
                className="input input-sm bg-base-300 border-base-400 text-gray-200 font-medium w-[200px]"
                placeholder={getCurrentGame(currentFile) || t('recovery.searchGames')}
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
            <span className="text-gray-400">{t('recovery.location')}</span>
            <button
              onClick={openInExplorer}
              className="text-blue-400 hover:text-blue-300 underline text-sm"
            >
              {t('recovery.openInExplorer')}
            </button>
          </div>
        </div>
      </div>

      <div className="modal-action mt-6 flex justify-end gap-3">
        <Button variant="success" onClick={() => handleAction('recover')}>
          {t('recovery.actions.recover')}
        </Button>
        <Button
          variant="danger"
          onClick={() =>
            settings.confirmBeforeDeleting ? setIsConfirmingDelete(true) : handleAction('delete')
          }
        >
          {t('recovery.actions.delete')}
        </Button>
        <Button variant="primary" onClick={() => handleAction('skip')}>
          {t('recovery.actions.skip')}
        </Button>
      </div>
    </>
  );
}
