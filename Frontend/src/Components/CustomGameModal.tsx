import { useState, useEffect, useRef } from 'react';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { isSelectedGameExecutableMessage } from '../Models/WebSocketMessages';
import { AppWindow, FileCog, FolderOpen, Gamepad2, Plus, RefreshCw, X } from 'lucide-react';
import Button from './Button';
import ConfirmationModal from './ConfirmationModal';
import { useSettings } from '../Context/SettingsContext';
import { useAppState } from '../Context/AppStateContext';
import { RunningApp } from '../Models/types';

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
  const settings = useSettings();
  const { platform } = useAppState();
  const [selectedExes, setSelectedExes] = useState<SelectedExecutable[]>([]);
  const [customGameName, setCustomGameName] = useState(initialName ?? '');
  // Treat a prefilled name (e.g. from the search box) as user-provided so browsing an exe won't replace it.
  const [nameEdited, setNameEdited] = useState(!!initialName?.trim());
  const [isSelectingFile, setIsSelectingFile] = useState(false);
  const [pathPendingRemoval, setPathPendingRemoval] = useState<string | null>(null);
  const [iconFailed, setIconFailed] = useState(false);
  const [runningApps, setRunningApps] = useState<RunningApp[] | null>(null);
  const [addingMore, setAddingMore] = useState(false);
  const pickedAppRef = useRef<RunningApp | null>(null);
  const showRunningApps = platform !== 'linux';

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
        // A running app keeps the name shown in the list, unless it matched a catalog game
        const picked = pickedAppRef.current;
        pickedAppRef.current = null;
        const listName =
          picked && content.igdbId == null && picked.path.toLowerCase() === path.toLowerCase()
            ? picked.name
            : null;
        if (path) {
          setSelectedExes((prev) =>
            prev.some((e) => e.path === path)
              ? prev
              : [
                  ...prev,
                  {
                    path,
                    name: listName ?? content.name,
                    igdbId: content.igdbId ?? null,
                    icon: content.icon ?? null,
                    customIcon: content.customIcon ?? null,
                  },
                ],
          );
        }
        setIsSelectingFile(false);
        if (path) setAddingMore(false);
      }
    };

    window.addEventListener('websocket-message', handleWebSocketMessage as EventListener);
    return () =>
      window.removeEventListener('websocket-message', handleWebSocketMessage as EventListener);
  }, [isSelectingFile]);

  useEffect(() => {
    if (!showRunningApps) return;
    const handleRunningApps = (event: CustomEvent<{ method: string; content: any }>) => {
      if (event.detail.method !== 'RunningApps') return;
      setRunningApps((event.detail.content as { apps: RunningApp[] }).apps);
    };
    window.addEventListener('websocket-message', handleRunningApps as EventListener);
    sendMessageToBackend('GetRunningApps');
    return () =>
      window.removeEventListener('websocket-message', handleRunningApps as EventListener);
  }, [showRunningApps]);

  // Suggest a name from the selected executables (a catalog match wins) until the user edits it.
  useEffect(() => {
    if (nameEdited) return;
    const catalogExe = selectedExes.find((e) => e.igdbId != null);
    setCustomGameName((catalogExe ?? selectedExes[0])?.name ?? '');
  }, [selectedExes, nameEdited]);

  const handleBrowseExecutable = () => {
    setIsSelectingFile(true);
    sendMessageToBackend('SelectGameExecutable');
  };

  // The backend resolves a running app the same way as a browsed exe (catalog match, exe icon)
  const handleSelectRunningApp = (app: RunningApp) => {
    pickedAppRef.current = app;
    setIsSelectingFile(true);
    sendMessageToBackend('SelectRunningGameExecutable', { Path: app.path });
  };

  const handleRemoveCustomPath = (pathToRemove: string) => {
    if (!settings.confirmBeforeDeleting) {
      setSelectedExes((prev) => prev.filter((exe) => exe.path !== pathToRemove));
      return;
    }

    setPathPendingRemoval(pathToRemove);
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

  if (pathPendingRemoval) {
    return (
      <ConfirmationModal
        title="Remove executable?"
        description={
          <>
            Remove this executable from the custom game?
            <br />
            <span className="text-sm text-gray-400 break-all">{pathPendingRemoval}</span>
          </>
        }
        confirmText="Remove"
        onConfirm={() => {
          setSelectedExes((prev) => prev.filter((e) => e.path !== pathPendingRemoval));
          setPathPendingRemoval(null);
        }}
        onCancel={() => setPathPendingRemoval(null)}
      />
    );
  }

  const catalogMatch = selectedExes.find((e) => e.igdbId != null);
  const previewIcon = selectedExes.find((e) => e.icon)?.icon;
  const previewCustomIcon = selectedExes.find((e) => e.customIcon)?.customIcon;
  const previewSrc =
    previewIcon && !settings.airplaneMode
      ? `https://segra.tv/api/games/icon/${previewIcon}`
      : previewCustomIcon
        ? `data:image/png;base64,${previewCustomIcon}`
        : null;

  const availableApps = (runningApps ?? []).filter(
    (app) => !selectedExes.some((e) => e.path.toLowerCase() === app.path.toLowerCase()),
  );

  const showPicker = selectedExes.length === 0 || addingMore;

  const picker = (
    <div className="space-y-3">
      {showRunningApps && (
        <>
          <div>
            <div className="flex items-center justify-between mb-1.5">
              <span className="text-xs opacity-70">Running apps</span>
              <Button
                variant="ghost"
                size="xs"
                onClick={() => sendMessageToBackend('GetRunningApps')}
                disabled={isSelectingFile}
              >
                <RefreshCw size={12} />
                Refresh
              </Button>
            </div>
            <div className="max-h-52 overflow-y-auto rounded-lg border border-base-400 bg-base-200 p-1">
              {availableApps.length > 0 ? (
                availableApps.map((app) => (
                  <button
                    key={app.path}
                    type="button"
                    disabled={isSelectingFile}
                    onClick={() => handleSelectRunningApp(app)}
                    className="w-full flex items-center gap-2.5 px-3 py-2 rounded-md text-sm text-left enabled:hover:bg-base-300 cursor-pointer disabled:cursor-default disabled:opacity-60"
                  >
                    {app.icon ? (
                      <img src={app.icon} alt="" className="h-5 w-5 shrink-0" />
                    ) : (
                      <AppWindow className="h-5 w-5 shrink-0 opacity-70" />
                    )}
                    <span className="truncate">{app.name}</span>
                  </button>
                ))
              ) : (
                <div className="px-3 py-2 text-sm opacity-60">
                  {runningApps
                    ? 'No apps running. Start the game, then press Refresh.'
                    : 'Loading...'}
                </div>
              )}
            </div>
          </div>
          <div className="flex items-center gap-3 text-xs opacity-60">
            <div className="flex-1 h-px bg-base-400" />
            or
            <div className="flex-1 h-px bg-base-400" />
          </div>
        </>
      )}
      <div className="flex justify-center">
        <Button size="sm" onClick={handleBrowseExecutable} disabled={isSelectingFile}>
          <FolderOpen size={16} />
          {isSelectingFile ? 'Selecting...' : 'Browse for .exe'}
        </Button>
      </div>
    </div>
  );

  return (
    <>
      <div className="modal-header">
        <Button variant="ghost" icon className="absolute right-2 top-1 z-10" onClick={onClose}>
          ✕
        </Button>
      </div>
      <div className="modal-body pt-3">
        <h3 className="font-bold text-2xl">Add Custom Game</h3>
        <p className="text-sm opacity-70 mt-1 pr-8">
          For games Segra doesn&apos;t detect on its own. It records the game whenever one of these
          executables runs.
        </p>

        <div className="mt-6 flex items-end gap-3">
          <div className="w-12 h-12 rounded-lg bg-base-200 border border-base-400 flex items-center justify-center overflow-hidden flex-shrink-0">
            {previewSrc && !iconFailed ? (
              <img
                src={previewSrc}
                alt=""
                className="w-full h-full object-cover"
                onError={() => setIconFailed(true)}
              />
            ) : (
              <Gamepad2 size={22} className="opacity-50" />
            )}
          </div>
          <div className="form-control flex-1">
            <label className="label pt-0">
              <span className="label-text text-base-content">Game Name</span>
            </label>
            <input
              type="text"
              className="input input-bordered bg-base-200 w-full focus:outline-none"
              placeholder="Enter game name..."
              value={customGameName}
              onChange={(e) => {
                setNameEdited(true);
                setCustomGameName(e.target.value);
              }}
            />
          </div>
        </div>
        {catalogMatch && (
          <p className="text-xs opacity-70 mt-2">
            Matches {catalogMatch.name} in Segra&apos;s game list, so it gets that game&apos;s icon
            and cover.
          </p>
        )}

        <div className="mt-5">
          <div className="label-text text-base-content mb-2">Executables</div>

          {selectedExes.length > 0 && (
            <div className="space-y-2">
              {selectedExes.map((exe) => {
                const parts = exe.path.split(/[\\/]/);
                const fileName = parts.pop();
                return (
                  <div
                    key={exe.path}
                    className="flex items-center gap-3 bg-base-200 px-3 py-2 rounded-lg border border-base-400"
                  >
                    <FileCog size={18} className="opacity-60 flex-shrink-0" />
                    <div className="min-w-0 flex-1">
                      <div className="text-sm font-medium truncate">{fileName}</div>
                      <div className="text-xs opacity-60 truncate">{parts.join('\\')}</div>
                    </div>
                    <Button
                      variant="ghost"
                      size="xs"
                      icon
                      aria-label={`Remove ${fileName}`}
                      className="hover:bg-error/20 hover:text-error"
                      onClick={() => handleRemoveCustomPath(exe.path)}
                    >
                      <X size={14} />
                    </Button>
                  </div>
                );
              })}
              {!addingMore && (
                <div className="flex justify-center pt-1">
                  <Button size="sm" variant="ghost" onClick={() => setAddingMore(true)}>
                    <Plus size={16} />
                    Add another executable
                  </Button>
                </div>
              )}
            </div>
          )}
          {showPicker && <div className={selectedExes.length > 0 ? 'mt-4' : ''}>{picker}</div>}
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
    </>
  );
}
