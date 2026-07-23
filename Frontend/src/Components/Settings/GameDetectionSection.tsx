import { useState, useMemo, useRef, useEffect } from 'react';
import { useSettings, useSettingsUpdater } from '../../Context/SettingsContext';
import { useAppState } from '../../Context/AppStateContext';
import {
  GameListEntry,
  GameSetting,
  GameQualityOverride,
  VideoQualityPreset,
  RecordingMode,
} from '../../Models/types';
import { motion, AnimatePresence } from 'framer-motion';
import {
  Search,
  Gamepad2,
  X,
  ChevronLeft,
  ChevronRight,
  Plus,
  VolumeX,
  Volume2,
} from 'lucide-react';
import { useModal } from '../../Context/ModalContext';
import CustomGameModal from '../CustomGameModal';
import DropdownSelect from '../DropdownSelect';
import { useDeleteConfirmation } from '../../Hooks/useDeleteConfirmation';

const BITRATE_OPTIONS = Array.from({ length: 19 }, (_, i) => (i + 2) * 5); // 10..100 Mbps

// Square game icon from the CDN, with a graceful fallback when there's no icon (custom games)
// or the image fails to load.
function GameIcon({
  iconId,
  customIcon,
  name,
  size = 40,
}: {
  iconId?: string | null;
  customIcon?: string | null;
  name: string;
  size?: number;
}) {
  const [errored, setErrored] = useState(false);
  // Prefer the catalog CDN icon; fall back to the exe-extracted base64 icon for custom games.
  const src = iconId
    ? `https://segra.tv/api/games/icon/${iconId}`
    : customIcon
      ? `data:image/png;base64,${customIcon}`
      : null;
  const showImage = !!src && !errored;
  return (
    <div
      className="rounded-md bg-base-200 flex items-center justify-center overflow-hidden flex-shrink-0"
      style={{ width: size, height: size }}
    >
      {showImage ? (
        <img
          src={src}
          alt={name}
          className="w-full h-full object-cover"
          onError={() => setErrored(true)}
        />
      ) : (
        <Gamepad2 size={Math.round(size * 0.5)} className="text-base-content opacity-50" />
      )}
    </div>
  );
}

// A reusable "Use custom … for this game" header row with an on/off toggle.
function OverrideSection({
  title,
  description,
  enabled,
  onToggle,
  children,
}: {
  title: string;
  description: string;
  enabled: boolean;
  onToggle: (enabled: boolean) => void;
  children?: React.ReactNode;
}) {
  return (
    <div className="bg-base-200 rounded-lg border border-base-400 p-4">
      <label className="flex items-start justify-between gap-4 cursor-pointer">
        <div>
          <div className="font-semibold">{title}</div>
          <div className="text-xs opacity-70 mt-0.5">{description}</div>
        </div>
        <input
          type="checkbox"
          className="toggle toggle-primary flex-shrink-0"
          checked={enabled}
          onChange={(e) => onToggle(e.target.checked)}
        />
      </label>
      <AnimatePresence initial={false}>
        {enabled && children && (
          <motion.div
            initial={{ opacity: 0, height: 0 }}
            animate={{
              opacity: 1,
              height: 'fit-content',
              transition: {
                duration: 0.3,
                height: { type: 'spring', stiffness: 300, damping: 30 },
              },
            }}
            exit={{ opacity: 0, height: 0, transition: { duration: 0.2 } }}
            style={{ overflow: 'visible' }}
          >
            <div className="mt-4">{children}</div>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}

export default function GameDetectionSection() {
  const settings = useSettings();
  const updateSettings = useSettingsUpdater();
  const appState = useAppState();
  const { openModal, closeModal } = useModal();
  const confirmDelete = useDeleteConfirmation();

  const [searchQuery, setSearchQuery] = useState('');
  const [showDropdown, setShowDropdown] = useState(false);
  const [selectedName, setSelectedName] = useState<string | null>(null);
  const searchRef = useRef<HTMLDivElement>(null);
  const tabsRef = useRef<HTMLDivElement>(null);
  const [canScrollLeft, setCanScrollLeft] = useState(false);
  const [canScrollRight, setCanScrollRight] = useState(false);

  // Close the search dropdown when clicking outside of it.
  useEffect(() => {
    const handleClickOutside = (event: MouseEvent) => {
      if (searchRef.current && !searchRef.current.contains(event.target as Node)) {
        setShowDropdown(false);
      }
    };
    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);

  const games = settings.games;

  // Track whether the tab strip overflows so we can show scroll arrows instead of a scrollbar.
  useEffect(() => {
    const el = tabsRef.current;
    if (!el) return;
    const update = () => {
      setCanScrollLeft(el.scrollLeft > 1);
      setCanScrollRight(el.scrollLeft + el.clientWidth < el.scrollWidth - 1);
    };
    update();
    el.addEventListener('scroll', update, { passive: true });
    const resizeObserver = new ResizeObserver(update);
    resizeObserver.observe(el);
    window.addEventListener('resize', update);
    return () => {
      el.removeEventListener('scroll', update);
      resizeObserver.disconnect();
      window.removeEventListener('resize', update);
    };
  }, [games.length]);

  const scrollTabs = (direction: -1 | 1) => {
    const el = tabsRef.current;
    if (!el) return;
    el.scrollBy({ left: direction * Math.max(160, el.clientWidth * 0.6), behavior: 'smooth' });
  };
  // No game is expanded by default; clicking a tab toggles its panel open/closed.
  const selectedGame = useMemo(
    () => games.find((g) => g.name === selectedName) ?? null,
    [games, selectedName],
  );

  // Games matching the search that aren't already in the list.
  const filteredGames = useMemo(() => {
    if (!searchQuery.trim()) return [];
    const query = searchQuery.toLowerCase();
    return appState.gameList
      .filter((g) => g.name.toLowerCase().includes(query) && !games.some((x) => x.name === g.name))
      .slice(0, 100);
  }, [searchQuery, appState.gameList, games]);

  // For migrated games that don't carry an icon yet, look one up from the catalog by igdb id or name.
  const resolveIcon = (g: GameSetting): string | undefined =>
    g.icon ??
    appState.gameList.find((e) => (g.igdbId != null && e.igdbId === g.igdbId) || e.name === g.name)
      ?.icon;

  const addGame = (game: GameSetting) => {
    // Match an existing entry by name or by catalog id, so the same game can't be added twice (even
    // under a slightly different name). Merge any new executable paths into it rather than dropping them.
    const existing = games.find(
      (g) => g.name === game.name || (game.igdbId != null && g.igdbId === game.igdbId),
    );
    if (existing) {
      const mergedPaths = Array.from(new Set([...existing.paths, ...game.paths]));
      if (mergedPaths.length !== existing.paths.length) {
        updateGame(existing.name, { paths: mergedPaths });
      }
      setSelectedName(existing.name);
      return;
    }
    updateSettings({ games: [...games, game] });
    setSelectedName(game.name);
  };

  const handleGameSelect = (entry: GameListEntry) => {
    addGame({
      name: entry.name,
      paths: entry.executables,
      igdbId: entry.igdbId ?? null,
      icon: entry.icon,
      customIcon: null,
      record: true,
      qualityOverride: null,
      recordingModeOverride: null,
      discardSessionsWithoutBookmarksOverride: null,
      enableHdrOverride: null,
      volumeOverride: null,
    });
    setSearchQuery('');
    setShowDropdown(false);
  };

  const handleAddCustomGame = (initialName?: string) => {
    openModal(
      <CustomGameModal
        initialName={initialName}
        onSave={(game) =>
          addGame({
            name: game.name,
            paths: game.paths,
            igdbId: game.igdbId,
            icon: game.icon ?? undefined,
            customIcon: game.customIcon,
            record: true,
            qualityOverride: null,
            recordingModeOverride: null,
            discardSessionsWithoutBookmarksOverride: null,
            enableHdrOverride: null,
            volumeOverride: null,
          })
        }
        onClose={closeModal}
      />,
    );
  };

  const updateGame = (name: string, patch: Partial<GameSetting>) => {
    updateSettings({ games: games.map((g) => (g.name === name ? { ...g, ...patch } : g)) });
  };

  const removeGame = (name: string) => {
    confirmDelete({
      title: 'Remove game settings?',
      description: `Remove ${name} and all of its recording overrides? Existing recordings will not be deleted.`,
      confirmText: 'Remove',
      onConfirm: () => {
        updateSettings({ games: games.filter((g) => g.name !== name) });
        if (selectedName === name) setSelectedName(null);
      },
    });
  };

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl font-semibold mb-2">Game Recording &amp; Overrides</h2>
      <p className="text-sm opacity-80 mb-4">
        Add a game here to force Segra to record it (or stop it from recording), and optionally
        override your recording settings for that game. Most games are detected automatically, so
        add one only if it isn&apos;t being recorded, or when you want different settings for it.
      </p>

      {/* Add game search */}
      <div className="mb-5 relative" ref={searchRef}>
        <label className="label pb-1">
          <span className="label-text text-base-content font-semibold">Add a game</span>
        </label>
        <div className="relative">
          <div className="absolute inset-y-0 left-0 flex items-center pl-3 pointer-events-none">
            <Search className="text-base-content opacity-50 z-10" size={20} />
          </div>
          <input
            type="text"
            className="input input-bordered w-full pl-10 bg-base-200"
            placeholder="Search for a game to record..."
            value={searchQuery}
            onChange={(e) => {
              setSearchQuery(e.target.value);
              setShowDropdown(true);
            }}
            onFocus={() => setShowDropdown(true)}
          />
        </div>

        {showDropdown && searchQuery.trim().length > 0 && (
          <div className="absolute z-50 w-full mt-1 bg-base-200 border border-base-400 rounded-lg shadow-lg max-h-72 overflow-y-auto">
            {filteredGames.map((game, index) => (
              <div
                key={index}
                className="p-2.5 hover:bg-base-300 cursor-pointer border-b border-base-300 flex items-center gap-3"
                onClick={() => handleGameSelect(game)}
              >
                <GameIcon iconId={game.icon} name={game.name} size={32} />
                <div className="min-w-0">
                  <div className="font-medium truncate">{game.name}</div>
                  <div className="text-xs text-gray-400 truncate">{game.executables[0]}</div>
                </div>
              </div>
            ))}

            {filteredGames.length === 0 && (
              <div className="px-3 pt-3 pb-1 text-xs text-gray-400">
                No matching game in the catalog.
              </div>
            )}

            {/* Fallback for games not in the catalog (browse to the executable). */}
            <div
              className="p-2.5 hover:bg-base-300 cursor-pointer flex items-center gap-3 text-primary"
              onClick={() => {
                setShowDropdown(false);
                handleAddCustomGame(searchQuery.trim());
              }}
            >
              <div className="w-8 h-8 rounded-md bg-base-300 flex items-center justify-center flex-shrink-0">
                <Plus size={18} />
              </div>
              <div className="font-medium truncate">Add a custom game</div>
            </div>
          </div>
        )}
      </div>

      {games.length === 0 ? (
        <div className="bg-base-200 rounded-lg border border-base-400 text-center text-gray-500 py-10">
          No custom games yet. Search for a game above to customize how Segra records it.
        </div>
      ) : (
        <>
          {/* Game tabs */}
          <div className={`relative border-b border-base-400 ${selectedGame ? 'mb-4' : ''}`}>
            {canScrollLeft && (
              <button
                type="button"
                aria-label="Scroll left"
                onClick={() => scrollTabs(-1)}
                className="absolute left-0 top-0 bottom-0 z-10 flex items-center justify-center px-1 bg-gradient-to-r from-base-300 via-base-300 to-transparent text-base-content cursor-pointer"
              >
                <ChevronLeft size={18} />
              </button>
            )}
            {canScrollRight && (
              <button
                type="button"
                aria-label="Scroll right"
                onClick={() => scrollTabs(1)}
                className="absolute right-0 top-0 bottom-0 z-10 flex items-center justify-center px-1 bg-gradient-to-l from-base-300 via-base-300 to-transparent text-base-content cursor-pointer"
              >
                <ChevronRight size={18} />
              </button>
            )}
            <div
              ref={tabsRef}
              className="no-scrollbar flex gap-1 overflow-x-auto overflow-y-hidden"
            >
              {games.map((g) => {
                const isActive = selectedGame?.name === g.name;
                return (
                  <div
                    key={g.name}
                    onClick={() => setSelectedName((prev) => (prev === g.name ? null : g.name))}
                    className={`flex items-center gap-2 pl-3 pr-2 py-2 -mb-px border-b-2 whitespace-nowrap transition-colors cursor-pointer ${
                      isActive
                        ? 'border-primary text-primary'
                        : 'border-transparent text-gray-400 hover:text-base-content'
                    }`}
                  >
                    <GameIcon
                      iconId={resolveIcon(g)}
                      customIcon={g.customIcon}
                      name={g.name}
                      size={20}
                    />
                    <span className="text-sm font-medium">{g.name}</span>
                    <button
                      type="button"
                      aria-label={`Remove ${g.name}`}
                      onClick={(e) => {
                        e.stopPropagation();
                        removeGame(g.name);
                      }}
                      className="p-0.5 rounded opacity-50 hover:opacity-100 hover:text-error hover:bg-base-100 cursor-pointer"
                    >
                      <X size={14} />
                    </button>
                  </div>
                );
              })}
            </div>
          </div>

          {selectedGame && (
            <GamePanel
              key={selectedGame.name}
              game={selectedGame}
              iconId={resolveIcon(selectedGame)}
              settings={settings}
              codecs={appState.codecs}
              maxDisplayHeight={appState.maxDisplayHeight}
              onUpdate={(patch) => updateGame(selectedGame.name, patch)}
            />
          )}
        </>
      )}
    </div>
  );
}

function GamePanel({
  game,
  iconId,
  settings,
  codecs,
  maxDisplayHeight,
  onUpdate,
}: {
  game: GameSetting;
  iconId?: string;
  settings: ReturnType<typeof useSettings>;
  codecs: ReturnType<typeof useAppState>['codecs'];
  maxDisplayHeight: number;
  onUpdate: (patch: Partial<GameSetting>) => void;
}) {
  const q = game.qualityOverride;
  const mode = game.recordingModeOverride;
  const [draggingVolume, setDraggingVolume] = useState<number | null>(null);

  // Quality override helpers ------------------------------------------------
  const enableQuality = () =>
    onUpdate({
      qualityOverride: {
        preset: settings.videoQualityPreset,
        resolution: settings.resolution,
        frameRate: settings.frameRate,
        rateControl: settings.rateControl,
        crfValue: settings.crfValue,
        cqLevel: settings.cqLevel,
        bitrate: settings.bitrate,
        minBitrate: settings.minBitrate,
        maxBitrate: settings.maxBitrate,
        encoder: settings.encoder,
        codec: settings.codec,
      },
    });

  const setQuality = (patch: Partial<GameQualityOverride>) => {
    if (!q) return;
    onUpdate({ qualityOverride: { ...q, ...patch } });
  };

  // Recording mode override helpers ----------------------------------------
  const enableMode = () =>
    onUpdate({
      recordingModeOverride: {
        recordingMode: settings.recordingMode,
        replayBufferDuration: settings.replayBufferDuration,
        replayBufferMaxSize: settings.replayBufferMaxSize,
      },
    });

  return (
    <div className="space-y-4">
      {/* Header: icon, name, record toggle */}
      <div className="bg-base-200 rounded-lg border border-base-400 p-4 flex items-center gap-4">
        <GameIcon iconId={iconId} customIcon={game.customIcon} name={game.name} size={48} />
        <div className="min-w-0 flex-1">
          <div className="font-semibold text-lg truncate">{game.name}</div>
          <div className="text-xs text-gray-400 truncate">
            {game.paths.length === 1 ? game.paths[0] : `${game.paths.length} executables`}
          </div>
        </div>
        <label className="flex items-center gap-2 cursor-pointer flex-shrink-0">
          <span
            className={`text-sm font-semibold ${game.record ? 'text-primary' : 'text-gray-400'}`}
          >
            {game.record ? 'Recording on' : 'Recording off'}
          </span>
          <input
            type="checkbox"
            className="toggle toggle-primary"
            checked={game.record}
            onChange={(e) => onUpdate({ record: e.target.checked })}
          />
        </label>
      </div>

      {/* Recording quality override */}
      <OverrideSection
        title="Recording Quality"
        description="Override resolution, frame rate and bitrate for this game."
        enabled={q != null}
        onToggle={(enabled) => (enabled ? enableQuality() : onUpdate({ qualityOverride: null }))}
      >
        {q && (
          <QualityOverrideEditor
            value={q}
            codecs={codecs}
            maxDisplayHeight={maxDisplayHeight}
            onChange={setQuality}
          />
        )}
      </OverrideSection>

      {/* Recording mode override */}
      <OverrideSection
        title="Recording Mode"
        description="Override whether this game records a full session, a replay buffer, or both."
        enabled={mode != null}
        onToggle={(enabled) => (enabled ? enableMode() : onUpdate({ recordingModeOverride: null }))}
      >
        {mode && (
          <div className="space-y-4">
            <div className="grid grid-cols-3 gap-3">
              {(['Hybrid', 'Session', 'Buffer'] as RecordingMode[]).map((m) => (
                <button
                  key={m}
                  onClick={() => onUpdate({ recordingModeOverride: { ...mode, recordingMode: m } })}
                  className={`bg-base-300 p-3 rounded-lg border text-sm font-semibold transition-all cursor-pointer hover:bg-base-100 ${
                    mode.recordingMode === m ? 'border-primary' : 'border-base-400'
                  }`}
                >
                  {m === 'Hybrid' ? 'Hybrid' : m === 'Session' ? 'Session' : 'Replay Buffer'}
                </button>
              ))}
            </div>

            <AnimatePresence initial={false}>
              {(mode.recordingMode === 'Buffer' || mode.recordingMode === 'Hybrid') && (
                <motion.div
                  initial={{ opacity: 0, height: 0 }}
                  animate={{
                    opacity: 1,
                    height: 'fit-content',
                    transition: {
                      duration: 0.3,
                      height: { type: 'spring', stiffness: 300, damping: 30 },
                    },
                  }}
                  exit={{ opacity: 0, height: 0, transition: { duration: 0.2 } }}
                  style={{ overflow: 'visible' }}
                >
                  <div className="grid grid-cols-2 gap-4">
                    <div className="form-control">
                      <label className="label text-base-content px-0 !block mb-1">
                        <span className="label-text">Buffer Duration (seconds)</span>
                      </label>
                      <input
                        type="number"
                        min={5}
                        max={600}
                        defaultValue={mode.replayBufferDuration}
                        onBlur={(e) =>
                          onUpdate({
                            recordingModeOverride: {
                              ...mode,
                              replayBufferDuration: Math.min(
                                600,
                                Math.max(5, Number(e.target.value) || 30),
                              ),
                            },
                          })
                        }
                        className="input input-bordered bg-base-300 w-full outline-none focus:border-base-400"
                      />
                    </div>
                    <div className="form-control">
                      <label className="label text-base-content px-0 !block mb-1">
                        <span className="label-text">Buffer Maximum Size (MB)</span>
                      </label>
                      <input
                        type="number"
                        min={100}
                        max={5000}
                        defaultValue={mode.replayBufferMaxSize}
                        onBlur={(e) =>
                          onUpdate({
                            recordingModeOverride: {
                              ...mode,
                              replayBufferMaxSize: Math.min(
                                5000,
                                Math.max(100, Number(e.target.value) || 1000),
                              ),
                            },
                          })
                        }
                        className="input input-bordered bg-base-300 w-full outline-none focus:border-base-400"
                      />
                    </div>
                  </div>
                </motion.div>
              )}
            </AnimatePresence>
          </div>
        )}
      </OverrideSection>

      {/* Discard sessions without bookmarks override */}
      <OverrideSection
        title="Discard Sessions Without Bookmarks"
        description="Override whether sessions of this game are discarded when they have no manual bookmarks."
        enabled={game.discardSessionsWithoutBookmarksOverride != null}
        onToggle={(enabled) =>
          onUpdate({
            discardSessionsWithoutBookmarksOverride: enabled
              ? settings.discardSessionsWithoutBookmarks
              : null,
          })
        }
      >
        <label className="flex items-center gap-2 cursor-pointer">
          <input
            type="checkbox"
            className="checkbox checkbox-primary checkbox-sm"
            checked={game.discardSessionsWithoutBookmarksOverride ?? false}
            onChange={(e) =>
              onUpdate({ discardSessionsWithoutBookmarksOverride: e.target.checked })
            }
          />
          <span>Discard Session Recordings Without Manual Bookmarks</span>
        </label>
      </OverrideSection>

      {/* HDR recording override */}
      <OverrideSection
        title="HDR Recording"
        description="Override whether HDR recording is enabled for this game (e.g. disable it for games where HDR injection tools break capture)."
        enabled={game.enableHdrOverride != null}
        onToggle={(enabled) => onUpdate({ enableHdrOverride: enabled ? settings.enableHdr : null })}
      >
        <label className="flex items-center gap-2 cursor-pointer">
          <input
            type="checkbox"
            className="checkbox checkbox-primary checkbox-sm"
            checked={game.enableHdrOverride ?? false}
            onChange={(e) => onUpdate({ enableHdrOverride: e.target.checked })}
          />
          <span>Record in HDR when the display supports it</span>
        </label>
      </OverrideSection>

      {/* Recording volume override */}
      <OverrideSection
        title="Recording Volume"
        description="Override the captured game/system audio volume for this game, without changing your own in-game or Windows volume."
        enabled={game.volumeOverride != null}
        onToggle={(enabled) => onUpdate({ volumeOverride: enabled ? 1.0 : null })}
      >
        <div className="flex items-center gap-3">
          <VolumeX className="w-4 h-4 text-gray-400 shrink-0" />
          <input
            type="range"
            min="0"
            max="2"
            step="0.02"
            value={draggingVolume ?? game.volumeOverride ?? 1.0}
            onChange={(e) => setDraggingVolume(parseFloat(e.target.value))}
            onMouseDown={(e) => setDraggingVolume(parseFloat(e.currentTarget.value))}
            onMouseUp={(e) => {
              onUpdate({ volumeOverride: parseFloat(e.currentTarget.value) });
              setDraggingVolume(null);
            }}
            onTouchEnd={() => {
              onUpdate({ volumeOverride: draggingVolume ?? game.volumeOverride ?? 1.0 });
              setDraggingVolume(null);
            }}
            className="range range-xs range-primary w-48 [--range-fill:0]"
          />
          <Volume2 className="w-4 h-4 text-gray-400 shrink-0" />
          <span className="text-xs w-10 text-right">
            {Math.round((draggingVolume ?? game.volumeOverride ?? 1.0) * 100)}%
          </span>
        </div>
      </OverrideSection>
    </div>
  );
}

function QualityOverrideEditor({
  value,
  codecs,
  maxDisplayHeight,
  onChange,
}: {
  value: GameQualityOverride;
  codecs: ReturnType<typeof useAppState>['codecs'];
  maxDisplayHeight: number;
  onChange: (patch: Partial<GameQualityOverride>) => void;
}) {
  const presets: { id: VideoQualityPreset; label: string; sub: string }[] = [
    { id: 'low', label: 'Low Quality', sub: '720p • 30fps' },
    { id: 'standard', label: 'Standard', sub: '1080p • 60fps' },
    {
      id: 'high',
      label: 'High Quality',
      sub: `${maxDisplayHeight >= 1440 ? '1440p' : '1080p'} • 60fps`,
    },
    { id: 'custom', label: 'Custom', sub: 'Manual config' },
  ];

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-4 gap-3">
        {presets.map((p) => (
          <div
            key={p.id}
            onClick={() => onChange({ preset: p.id })}
            className={`bg-base-300 p-3 rounded-lg flex flex-col items-center justify-center border transition-all cursor-pointer hover:bg-base-100 ${
              value.preset === p.id ? 'border-primary' : 'border-base-400'
            }`}
          >
            <div className="text-sm font-semibold">{p.label}</div>
            <div className="text-xs text-base-content text-opacity-70 mt-1">{p.sub}</div>
          </div>
        ))}
      </div>

      <AnimatePresence initial={false}>
        {value.preset === 'custom' && (
          <motion.div
            initial={{ opacity: 0, height: 0 }}
            animate={{
              opacity: 1,
              height: 'fit-content',
              transition: {
                duration: 0.3,
                height: { type: 'spring', stiffness: 300, damping: 30 },
              },
            }}
            exit={{ opacity: 0, height: 0, transition: { duration: 0.2 } }}
            style={{ overflow: 'visible' }}
          >
            <div className="grid grid-cols-2 gap-4">
              {/* Resolution */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Resolution</span>
                </label>
                <DropdownSelect
                  items={[
                    { value: '720p', label: '720p' },
                    { value: '1080p', label: '1080p' },
                    ...(maxDisplayHeight >= 1440 ? [{ value: '1440p', label: '1440p' }] : []),
                    ...(maxDisplayHeight >= 2160 ? [{ value: '4K', label: '4K' }] : []),
                  ]}
                  value={value.resolution}
                  onChange={(val) =>
                    onChange({ resolution: val as '720p' | '1080p' | '1440p' | '4K' })
                  }
                />
              </div>

              {/* Frame rate */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Frame Rate (FPS)</span>
                </label>
                <DropdownSelect
                  items={[24, 30, 60, 120, 144].map((v) => ({
                    value: String(v),
                    label: String(v),
                  }))}
                  value={String(value.frameRate)}
                  onChange={(val) => onChange({ frameRate: Number(val) })}
                />
              </div>

              {/* Rate control */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Rate Control</span>
                </label>
                <DropdownSelect
                  items={[
                    { value: 'CBR', label: 'CBR (Constant Bitrate)' },
                    { value: 'VBR', label: 'VBR (Variable Bitrate)' },
                    ...(value.encoder === 'cpu'
                      ? [{ value: 'CRF', label: 'CRF (Constant Rate Factor)' }]
                      : [{ value: 'CQP', label: 'CQP (Constant Quantization Parameter)' }]),
                  ]}
                  value={value.rateControl}
                  onChange={(val) => onChange({ rateControl: val })}
                />
              </div>

              {/* Bitrate (CBR) */}
              {value.rateControl === 'CBR' && (
                <div className="form-control">
                  <label className="label">
                    <span className="label-text text-base-content">Bitrate</span>
                  </label>
                  <DropdownSelect
                    items={BITRATE_OPTIONS.map((v) => ({ value: String(v), label: `${v} Mbps` }))}
                    value={String(value.bitrate)}
                    onChange={(val) => onChange({ bitrate: Number(val) })}
                  />
                </div>
              )}

              {/* Min/Max bitrate (VBR) */}
              {value.rateControl === 'VBR' && (
                <>
                  <div className="form-control">
                    <label className="label">
                      <span className="label-text text-base-content">Minimum Bitrate</span>
                    </label>
                    <DropdownSelect
                      items={BITRATE_OPTIONS.map((v) => ({ value: String(v), label: `${v} Mbps` }))}
                      value={String(value.minBitrate)}
                      onChange={(val) => {
                        const min = Number(val);
                        onChange({ minBitrate: min, maxBitrate: Math.max(min, value.maxBitrate) });
                      }}
                    />
                  </div>
                  <div className="form-control">
                    <label className="label">
                      <span className="label-text text-base-content">Maximum Bitrate</span>
                    </label>
                    <DropdownSelect
                      items={BITRATE_OPTIONS.map((v) => ({ value: String(v), label: `${v} Mbps` }))}
                      value={String(value.maxBitrate)}
                      onChange={(val) => {
                        const max = Number(val);
                        onChange({ maxBitrate: max, minBitrate: Math.min(max, value.minBitrate) });
                      }}
                    />
                  </div>
                </>
              )}

              {/* CRF */}
              {value.rateControl === 'CRF' && (
                <div className="form-control">
                  <label className="label">
                    <span className="label-text text-base-content">CRF Value (0-51)</span>
                  </label>
                  <input
                    type="number"
                    min={0}
                    max={51}
                    defaultValue={value.crfValue}
                    onBlur={(e) =>
                      onChange({
                        crfValue: Math.min(51, Math.max(0, Number(e.target.value) || 23)),
                      })
                    }
                    className="input input-bordered bg-base-300 w-full outline-none focus:border-base-400"
                  />
                </div>
              )}

              {/* CQ */}
              {value.rateControl === 'CQP' && (
                <div className="form-control">
                  <label className="label">
                    <span className="label-text text-base-content">CQ Level (0-30)</span>
                  </label>
                  <input
                    type="number"
                    min={0}
                    max={30}
                    defaultValue={value.cqLevel}
                    onBlur={(e) =>
                      onChange({ cqLevel: Math.min(30, Math.max(0, Number(e.target.value) || 20)) })
                    }
                    className="input input-bordered bg-base-300 w-full outline-none focus:border-base-400"
                  />
                </div>
              )}

              {/* Encoder */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Video Encoder</span>
                </label>
                <DropdownSelect
                  items={[
                    { value: 'gpu', label: 'GPU' },
                    { value: 'cpu', label: 'CPU' },
                  ]}
                  value={value.encoder}
                  onChange={(val) => {
                    const encoder = val as 'gpu' | 'cpu';
                    // Keep rate control valid for the chosen encoder (CRF is CPU-only, CQP is GPU-only).
                    let rateControl = value.rateControl;
                    if (encoder === 'cpu' && rateControl === 'CQP') rateControl = 'CRF';
                    if (encoder === 'gpu' && rateControl === 'CRF') rateControl = 'CQP';
                    // Reset the codec to one valid for the new encoder; the backend records with the
                    // override's codec directly, so a stale codec would otherwise win over the encoder choice.
                    const codec =
                      codecs.find((c) =>
                        encoder === 'gpu' ? c.isHardwareEncoder : !c.isHardwareEncoder,
                      ) ?? null;
                    onChange({ encoder, rateControl, codec });
                  }}
                />
              </div>

              {/* Codec */}
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Codec</span>
                </label>
                <DropdownSelect
                  items={codecs
                    .filter((codec) =>
                      value.encoder === 'gpu' ? codec.isHardwareEncoder : !codec.isHardwareEncoder,
                    )
                    .map((codec) => ({
                      value: codec.internalEncoderId,
                      label: codec.friendlyName,
                    }))}
                  value={
                    codecs.find((c) => c.internalEncoderId === value.codec?.internalEncoderId)
                      ?.internalEncoderId
                  }
                  onChange={(val) =>
                    onChange({ codec: codecs.find((c) => c.internalEncoderId === val) ?? null })
                  }
                  disabled={codecs.length === 0}
                />
              </div>
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
