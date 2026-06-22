import { useState, useEffect, useRef } from 'react';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { isSelectedGameExecutableMessage } from '../Models/WebSocketMessages';
import { FolderOpen, Plus } from 'lucide-react';
import Button from './Button';

export interface CustomGameResult {
  name: string;
  paths: string[];
  igdbId: number | null; // set when one of the selected exes matched a catalog game
  icon: string | null; // catalog CDN icon id
  customIcon: string | null; // base64 exe icon (when there's no catalog icon)
}

interface CustomGameModalProps {
  onSave: (game: CustomGameResult) => void;
  onClose: () => void;
  initialName?: string;
}

// A selected executable plus the catalog metadata the backend resolved for it.
interface SelectedExecutable {
  path: string;
  name: string;
  igdbId: number | null;
  icon: string | null;
  customIcon: string | null;
}

export default function CustomGameModal({ onSave, onClose, initialName }: CustomGameModalProps) {
  const [selectedExes, setSelectedExes] = useState<SelectedExecutable[]>([]);
  const [customGameName, setCustomGameName] = useState(initialName ?? '');
  // Treat a prefilled name (e.g. from the search box) as user-provided so browsing an exe won't replace it.
  const [nameEdited, setNameEdited] = useState(!!initialName?.trim());
  const [isSelectingFile, setIsSelectingFile] = useState(false);
  const autoOpenedRef = useRef(false);

  useEffect(() => {
    const handleWebSocketMessage = (event: CustomEvent<any>) => {
      const message = event.detail;

      if (isSelectingFile && isSelectedGameExecutableMessage(message)) {
        const content = message.content as {
          name: string;
          paths?: string[];
          igdbId?: number | null;
          icon?: string | null;
          customIcon?: string | null;
        };
        const path = content.paths?.[0] || '';
        if (path) {
          setSelectedExes((prev) =>
            prev.some((e) => e.path === path)
              ? prev
              : [
                  ...prev,
                  {
                    path,
                    name: content.name,
                    igdbId: content.igdbId ?? null,
                    icon: content.icon ?? null,
                    customIcon: content.customIcon ?? null,
                  },
                ],
          );
        }
        setIsSelectingFile(false);
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);
    return () =>
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
  }, [isSelectingFile]);

  // Suggest a name from the selected executables (a catalog match wins) until the user edits it.
  useEffect(() => {
    if (nameEdited) return;
    const catalogExe = selectedExes.find((e) => e.igdbId != null);
    setCustomGameName((catalogExe ?? selectedExes[0])?.name ?? '');
  }, [selectedExes, nameEdited]);

  // Open the file picker immediately when the modal opens (guarded so it only fires once).
  useEffect(() => {
    if (autoOpenedRef.current) return;
    autoOpenedRef.current = true;
    setIsSelectingFile(true);
    sendMessageToBackend('SelectGameExecutable');
  }, []);

  const handleBrowseExecutable = () => {
    setIsSelectingFile(true);
    sendMessageToBackend('SelectGameExecutable');
  };

  const handleRemoveCustomPath = (pathToRemove: string) => {
    setSelectedExes((prev) => prev.filter((e) => e.path !== pathToRemove));
  };

  const handleSave = () => {
    if (!customGameName.trim() || selectedExes.length === 0) return;

    // Derive the catalog link and icon from the current set of executables, so removing a path can
    // never leave stale metadata behind. Prefer a catalog CDN icon; otherwise use an extracted exe icon.
    const catalogExe = selectedExes.find((e) => e.igdbId != null);
    const icon = selectedExes.find((e) => e.icon)?.icon ?? null;
    const customIcon = icon ? null : (selectedExes.find((e) => e.customIcon)?.customIcon ?? null);

    onSave({
      name: customGameName.trim(),
      paths: selectedExes.map((e) => e.path),
      igdbId: catalogExe?.igdbId ?? null,
      icon,
      customIcon,
    });
    onClose();
  };

  return (
    <>
      <div className="bg-base-300">
        <div className="modal-header">
          <Button
            variant="ghost"
            size="sm"
            icon
            className="absolute right-4 top-1 z-10"
            onClick={onClose}
          >
            ✕
          </Button>
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
              onChange={(e) => {
                setNameEdited(true);
                setCustomGameName(e.target.value);
              }}
            />
          </div>

          <div className="form-control w-full">
            <label className="label">
              <span className="label-text text-base-content">Executables</span>
            </label>
            <Button
              variant="primary"
              className="w-full"
              onClick={handleBrowseExecutable}
              disabled={isSelectingFile}
            >
              <FolderOpen size={18} />
              {isSelectingFile ? 'Selecting...' : 'Browse Executable'}
            </Button>

            {selectedExes.length > 0 && (
              <div className="mt-3 space-y-2">
                {selectedExes.map((exe, idx) => (
                  <div
                    key={idx}
                    className="flex items-center gap-2 bg-base-200 p-2 rounded-lg border border-base-400"
                  >
                    <span className="text-xs flex-1 truncate text-gray-300">{exe.path}</span>
                    <Button
                      variant="ghost"
                      size="xs"
                      className="hover:bg-error/20 hover:text-error"
                      onClick={() => handleRemoveCustomPath(exe.path)}
                    >
                      ✕
                    </Button>
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
        <div className="modal-action mt-6">
          <Button
            variant="primary"
            className="w-full"
            onClick={handleSave}
            disabled={!customGameName.trim() || selectedExes.length === 0}
          >
            <Plus className="w-5 h-5" />
            Add Game
          </Button>
        </div>
      </div>
    </>
  );
}
