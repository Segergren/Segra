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
import { Search, Gamepad2, X, Plus, VolumeX, Volume2 } from 'lucide-react';
import { useModal } from '../../Context/ModalContext';
import { clampInt } from '../../Utils/NumberUtils';
import CustomGameModal from '../CustomGameModal';
import Button from '../Button';
import DropdownSelect from '../DropdownSelect';
import RangeSlider from '../RangeSlider';
import BitrateInput from './BitrateInput';
import { useDeleteConfirmation } from '../../Hooks/useDeleteConfirmation';

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
  const { airplaneMode } = useSettings();
  // Prefer the catalog CDN icon; fall back to the exe-extracted base64 icon for custom games.
  const src =
    iconId && !airplaneMode
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

const presetSummary = (preset: VideoQualityPreset, maxDisplayHeight: number) =>
  ({
    low: '720p • 30fps',
    standard: '1080p • 60fps',
    high: `${maxDisplayHeight >= 1440 ? '1440p' : '1080p'} • 60fps`,
    custom: 'Manual config',
  })[preset];

// Darkens like the inputs and dropdowns on hover (globals.css)
const OPTION_HOVER = 'hover:bg-[color-mix(in_oklab,var(--color-base-300),#000_7%)]';

const RECORD_OPTIONS: { label: string; description: string; value: boolean | null }[] = [
  { label: 'Auto', description: 'Follows Auto-Record Games', value: null },
  { label: 'Always', description: 'Even when Auto-Record is off', value: true },
  { label: 'Never', description: 'Only when started manually', value: false },
];

const RECORDING_MODE_LABEL: Record<RecordingMode, string> = {
  Hybrid: 'Hybrid',
  Session: 'Session',
  Buffer: 'Replay Buffer',
};

const DEFAULT_SUMMARY = 'Default settings';

// Short line listing what a game overrides, shown on its card
function overridesSummary(g: GameSetting, maxDisplayHeight: number): string {
  if (g.record === false) return 'Not recorded';
  const parts: string[] = g.record === true ? ['Always'] : [];
  const q = g.qualityOverride;
  if (q) {
    parts.push(
      q.preset === 'custom'
        ? `${q.resolution} • ${q.frameRate}fps`
        : presetSummary(q.preset, maxDisplayHeight),
    );
  }
  if (g.recordingModeOverride)
    parts.push(RECORDING_MODE_LABEL[g.recordingModeOverride.recordingMode]);
  const others = [
    g.discardSessionsWithoutBookmarksOverride,
    g.enableHdrOverride,
    g.volumeOverride,
    g.highlightPaddingOverride,
  ].filter((o) => o != null).length;
  if (others > 0) parts.push(parts.length > 0 ? `+${others} more` : `${others} overrides`);
  return parts.length > 0 ? parts.join(' • ') : DEFAULT_SUMMARY;
}

function GameCard({
  game,
  iconId,
  coverUrl,
  summary,
  selected,
  onSelect,
  onRemove,
}: {
  game: GameSetting;
  iconId?: string;
  coverUrl: string | null;
  summary: string;
  selected: boolean;
  onSelect: () => void;
  onRemove: () => void;
}) {
  return (
    <div
      role="button"
      tabIndex={0}
      aria-pressed={selected}
      onClick={onSelect}
      onKeyDown={(e) => {
        if (e.target === e.currentTarget && (e.key === 'Enter' || e.key === ' ')) {
          e.preventDefault();
          onSelect();
        }
      }}
      className={`group relative bg-base-200 px-4 py-3 rounded-lg border cursor-pointer transition-colors ${selected ? 'border-primary' : 'border-base-400 hover:border-base-content/30'}`}
    >
      {coverUrl && (
        <div
          className={`absolute inset-0 rounded-[inherit] bg-cover bg-center pointer-events-none ${game.record === false ? 'opacity-15 grayscale' : 'opacity-35'}`}
          style={{ backgroundImage: `url(${coverUrl})` }}
        />
      )}
      <button
        type="button"
        aria-label={`Remove ${game.name}`}
        onClick={(e) => {
          e.stopPropagation();
          onRemove();
        }}
        className="absolute top-1.5 right-1.5 z-20 p-0.5 rounded opacity-0 group-hover:opacity-70 focus-visible:opacity-70 hover:!opacity-100 hover:text-error hover:bg-base-100 cursor-pointer"
      >
        <X size={14} />
      </button>
      <div className="relative z-10 flex items-center gap-3 h-14">
        <GameIcon iconId={iconId} customIcon={game.customIcon} name={game.name} size={40} />
        <div className="min-w-0">
          <div className="font-semibold truncate drop-shadow-md">{game.name}</div>
          <div
            className={`text-xs truncate drop-shadow-md ${summary === DEFAULT_SUMMARY ? 'opacity-80' : 'text-warning'}`}
          >
            {summary}
          </div>
        </div>
      </div>
    </div>
  );
}

// Expands and collapses its children. Padding instead of a margin and overflow hidden while
// animating, so closing runs all the way down without a jump at the end.
function Collapse({ open, children }: { open: boolean; children?: React.ReactNode }) {
  // Once open, nothing is clipped and the content sits above the sections below it, so dropdown
  // menus can extend past it
  const [settled, setSettled] = useState(true);
  return (
    <AnimatePresence initial={false}>
      {open && (
        <motion.div
          initial={{ opacity: 0, height: 0 }}
          animate={{
            opacity: 1,
            height: 'auto',
            transition: {
              duration: 0.3,
              height: { type: 'spring', stiffness: 300, damping: 30 },
            },
          }}
          exit={{
            opacity: 0,
            height: 0,
            transition: { duration: 0.25, ease: 'easeInOut' },
          }}
          onAnimationStart={() => setSettled(false)}
          onAnimationComplete={() => setSettled(true)}
          style={{
            overflow: settled ? 'visible' : 'hidden',
            position: 'relative',
            zIndex: settled ? 10 : undefined,
          }}
        >
          <div className="pt-4">{children}</div>
        </motion.div>
      )}
    </AnimatePresence>
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
      <Collapse open={enabled && !!children}>{children}</Collapse>
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

  // No game is expanded by default; clicking a card toggles its panel open/closed.
  const selectedGame = useMemo(
    () => games.find((g) => g.name === selectedName) ?? null,
    [games, selectedName],
  );

  // Games matching the search that aren't already in the list.
  const filteredGames = useMemo(() => {
    if (!searchQuery.trim()) return [];
    const query = searchQuery.toLowerCase();
    return appState.gameList
      .filter(
        (g) =>
          g.name.toLowerCase().includes(query) &&
          !games.some((x) => x.name === g.name || (g.igdbId != null && x.igdbId === g.igdbId)),
      )
      .slice(0, 100);
  }, [searchQuery, appState.gameList, games]);

  const catalog = useMemo(() => {
    const byIgdbId = new Map<number, GameListEntry>();
    const byName = new Map<string, GameListEntry>();
    for (const e of appState.gameList) {
      if (e.igdbId != null && !byIgdbId.has(e.igdbId)) byIgdbId.set(e.igdbId, e);
      if (!byName.has(e.name)) byName.set(e.name, e);
    }
    return { byIgdbId, byName };
  }, [appState.gameList]);

  const findCatalogEntry = (g: GameSetting) =>
    (g.igdbId != null ? catalog.byIgdbId.get(g.igdbId) : undefined) ?? catalog.byName.get(g.name);

  const sortedGames = useMemo(
    () => [...games].sort((a, b) => a.name.localeCompare(b.name)),
    [games],
  );

  // For migrated games that don't carry an icon yet, look one up from the catalog by igdb id or name.
  const resolveIcon = (g: GameSetting): string | undefined => g.icon ?? findCatalogEntry(g)?.icon;

  const showCovers = settings.showGameBackground && !settings.airplaneMode;
  const resolveCover = (g: GameSetting): string | null => {
    const coverId = showCovers ? findCatalogEntry(g)?.coverImageId : undefined;
    return coverId ? `https://segra.tv/api/games/cover/${coverId}` : null;
  };

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
      paths: [],
      igdbId: entry.igdbId ?? null,
      icon: entry.icon,
      customIcon: null,
      record: true,
      qualityOverride: null,
      recordingModeOverride: null,
      discardSessionsWithoutBookmarksOverride: null,
      enableHdrOverride: null,
      volumeOverride: null,
      highlightPaddingOverride: null,
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
            highlightPaddingOverride: null,
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
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
      <h2 className="text-xl font-semibold mb-2">Game Recording &amp; Overrides</h2>
      <p className="text-sm opacity-70 mb-4">
        Games you record show up here automatically. Pick one to change its recording settings or
        whether it gets recorded. Add a game below only if Segra doesn&apos;t record it on its own.
      </p>

      {/* Global auto-record toggle */}
      <label className="flex items-center gap-3 cursor-pointer p-3 bg-base-200 rounded-lg border border-base-400 mb-5">
        <input
          type="checkbox"
          className="checkbox checkbox-primary checkbox-sm"
          checked={settings.autoRecordGames}
          onChange={(e) => updateSettings({ autoRecordGames: e.target.checked })}
        />
        <div>
          <div className="font-semibold">Auto-Record Games</div>
          <div className="text-sm opacity-70 mt-0.5">
            Automatically start recording when a game launches. Games set to Always and manual
            recordings are unaffected.
          </div>
        </div>
      </label>

      {/* Add game search */}
      <div className="mb-5 relative" ref={searchRef}>
        <label className="label">
          <span className="label-text text-base-content font-semibold">Add a Game</span>
        </label>
        <div className="flex gap-2">
          <div className="relative flex-1">
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
          <Button
            className="flex-shrink-0"
            onClick={() => {
              setShowDropdown(false);
              handleAddCustomGame(searchQuery.trim() || undefined);
            }}
          >
            <Plus size={18} />
            Add Custom Game
          </Button>
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
                <div className="font-medium truncate min-w-0">{game.name}</div>
              </div>
            ))}

            {filteredGames.length === 0 && (
              <div className="px-3 pt-3 pb-1 text-xs opacity-70">
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
        <div className="bg-base-200 rounded-lg border border-base-400 text-center text-base-content/60 py-10">
          No games yet. Games you record show up here, or search for one above.
        </div>
      ) : (
        <>
          {/* Up to 4 per row, based on the section's own width rather than the window */}
          <div
            className={`grid grid-cols-[repeat(auto-fill,minmax(max(13rem,calc((100%_-_2.25rem)/4)),1fr))] gap-3 ${selectedGame ? 'mb-4' : ''}`}
          >
            {sortedGames.map((g) => (
              <GameCard
                key={g.name}
                game={g}
                iconId={resolveIcon(g)}
                coverUrl={resolveCover(g)}
                summary={overridesSummary(g, appState.maxDisplayHeight)}
                selected={selectedGame?.name === g.name}
                onSelect={() => setSelectedName((prev) => (prev === g.name ? null : g.name))}
                onRemove={() => removeGame(g.name)}
              />
            ))}
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
  const padding = game.highlightPaddingOverride;
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
      {/* Header: icon and name */}
      <div className="bg-base-200 rounded-lg border border-base-400 p-4 flex items-center gap-4">
        <GameIcon iconId={iconId} customIcon={game.customIcon} name={game.name} size={48} />
        <div className="min-w-0 flex-1">
          <div className="font-semibold text-lg truncate">{game.name}</div>
          {game.paths.length > 0 && (
            <div className="text-xs opacity-70 truncate">
              {game.paths.length === 1 ? game.paths[0] : `${game.paths.length} executables`}
            </div>
          )}
        </div>
      </div>

      {/* When this game is recorded automatically */}
      <div className="bg-base-200 rounded-lg border border-base-400 p-4">
        <div className="font-semibold">Automatic Recording</div>
        <div className="text-xs opacity-70 mt-0.5">
          Whether Segra starts recording when this game launches. Auto-Record Games is currently{' '}
          {settings.autoRecordGames ? 'on' : 'off'}.
        </div>
        <div
          role="radiogroup"
          aria-label="Automatic Recording"
          className="grid grid-cols-3 gap-3 mt-4"
        >
          {RECORD_OPTIONS.map((o) => (
            <button
              key={o.label}
              type="button"
              role="radio"
              aria-checked={game.record === o.value}
              onClick={() => onUpdate({ record: o.value })}
              className={`bg-base-300 p-3 rounded-lg border flex flex-col items-center transition-all cursor-pointer ${OPTION_HOVER} ${
                game.record === o.value ? 'border-primary' : 'border-base-400'
              }`}
            >
              <span className="text-sm font-semibold">{o.label}</span>
              <span className="text-xs opacity-70 mt-1">{o.description}</span>
            </button>
          ))}
        </div>
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
          <div>
            <div className="grid grid-cols-3 gap-3">
              {(['Hybrid', 'Session', 'Buffer'] as RecordingMode[]).map((m) => (
                <button
                  key={m}
                  onClick={() => onUpdate({ recordingModeOverride: { ...mode, recordingMode: m } })}
                  className={`bg-base-300 p-3 rounded-lg border text-sm font-semibold transition-all cursor-pointer ${OPTION_HOVER} ${
                    mode.recordingMode === m ? 'border-primary' : 'border-base-400'
                  }`}
                >
                  {RECORDING_MODE_LABEL[m]}
                </button>
              ))}
            </div>

            <Collapse open={mode.recordingMode === 'Buffer' || mode.recordingMode === 'Hybrid'}>
              <div className="grid grid-cols-2 gap-4">
                <div className="form-control">
                  <label className="label text-base-content px-0 !block">
                    <span className="label-text">Buffer Duration</span>
                  </label>
                  <div className="relative w-full">
                    <input
                      type="number"
                      min={5}
                      max={600}
                      defaultValue={mode.replayBufferDuration}
                      onBlur={(e) =>
                        onUpdate({
                          recordingModeOverride: {
                            ...mode,
                            replayBufferDuration: clampInt(e.target.value, 5, 600, 30),
                          },
                        })
                      }
                      className="input input-bordered bg-base-300 w-full pr-12 outline-none focus:border-base-400"
                    />
                    <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
                      sec
                    </span>
                  </div>
                </div>
                <div className="form-control">
                  <label className="label text-base-content px-0 !block">
                    <span className="label-text">Buffer Maximum Size</span>
                  </label>
                  <div className="relative w-full">
                    <input
                      type="number"
                      min={100}
                      max={5000}
                      defaultValue={mode.replayBufferMaxSize}
                      onBlur={(e) =>
                        onUpdate({
                          recordingModeOverride: {
                            ...mode,
                            replayBufferMaxSize: clampInt(e.target.value, 100, 5000, 1000),
                          },
                        })
                      }
                      className="input input-bordered bg-base-300 w-full pr-12 outline-none focus:border-base-400"
                    />
                    <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
                      MB
                    </span>
                  </div>
                </div>
              </div>
            </Collapse>
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
          <VolumeX className="w-4 h-4 opacity-70 shrink-0" />
          <RangeSlider
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
            className="w-48"
          />
          <Volume2 className="w-4 h-4 opacity-70 shrink-0" />
          <span className="text-xs w-10 text-right">
            {Math.round((draggingVolume ?? game.volumeOverride ?? 1.0) * 100)}%
          </span>
        </div>
      </OverrideSection>

      {/* Highlight padding override */}
      <OverrideSection
        title="Highlight Padding"
        description="Override how many seconds are kept before and after each highlight moment in this game."
        enabled={padding != null}
        onToggle={(enabled) =>
          onUpdate({
            highlightPaddingOverride: enabled
              ? {
                  before: settings.highlightPaddingBefore,
                  after: settings.highlightPaddingAfter,
                }
              : null,
          })
        }
      >
        {padding && (
          <div className="grid grid-cols-2 gap-4">
            <div className="form-control w-full">
              <label className="label text-base-content px-0 !block">
                <span className="label-text">Before Highlight</span>
              </label>
              <div className="relative w-full">
                <input
                  type="number"
                  min={1}
                  max={60}
                  step={0.5}
                  defaultValue={padding.before}
                  onBlur={(e) =>
                    onUpdate({
                      highlightPaddingOverride: {
                        ...padding,
                        before: clampPadding(e.target.value, padding.before),
                      },
                    })
                  }
                  className="input input-bordered bg-base-300 w-full pr-12 outline-none focus:border-base-400"
                />
                <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
                  sec
                </span>
              </div>
            </div>
            <div className="form-control w-full">
              <label className="label text-base-content px-0 !block">
                <span className="label-text">After Highlight</span>
              </label>
              <div className="relative w-full">
                <input
                  type="number"
                  min={1}
                  max={60}
                  step={0.5}
                  defaultValue={padding.after}
                  onBlur={(e) =>
                    onUpdate({
                      highlightPaddingOverride: {
                        ...padding,
                        after: clampPadding(e.target.value, padding.after),
                      },
                    })
                  }
                  className="input input-bordered bg-base-300 w-full pr-12 outline-none focus:border-base-400"
                />
                <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
                  sec
                </span>
              </div>
            </div>
          </div>
        )}
      </OverrideSection>
    </div>
  );
}

// Same background and border as the inputs in the override editor (daisyUI's --input-color)
const OVERRIDE_DROPDOWN_CLASS = `btn border-[color-mix(in_oklab,var(--color-base-content)_20%,transparent)] bg-base-300 ${OPTION_HOVER} w-full justify-between`;

function clampPadding(raw: string, fallback: number): number {
  const parsed = Number(raw);
  return Number.isFinite(parsed) ? Math.min(60, Math.max(1, parsed)) : fallback;
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
  const presets: { id: VideoQualityPreset; label: string }[] = [
    { id: 'low', label: 'Low Quality' },
    { id: 'standard', label: 'Standard' },
    { id: 'high', label: 'High Quality' },
    { id: 'custom', label: 'Custom' },
  ];

  return (
    <div>
      <div className="grid grid-cols-4 gap-3">
        {presets.map((p) => (
          <div
            key={p.id}
            onClick={() => onChange({ preset: p.id })}
            className={`bg-base-300 p-3 rounded-lg flex flex-col items-center justify-center border transition-all cursor-pointer ${OPTION_HOVER} ${
              value.preset === p.id ? 'border-primary' : 'border-base-400'
            }`}
          >
            <div className="text-sm font-semibold">{p.label}</div>
            <div className="text-xs opacity-70 mt-1">{presetSummary(p.id, maxDisplayHeight)}</div>
          </div>
        ))}
      </div>

      <Collapse open={value.preset === 'custom'}>
        <div className="grid grid-cols-2 gap-4">
          {/* Resolution */}
          <div className="form-control">
            <label className="label">
              <span className="label-text text-base-content">Resolution</span>
            </label>
            <DropdownSelect
              buttonClassName={OVERRIDE_DROPDOWN_CLASS}
              items={[
                { value: '720p', label: '720p' },
                { value: '1080p', label: '1080p' },
                ...(maxDisplayHeight >= 1440 ? [{ value: '1440p', label: '1440p' }] : []),
                ...(maxDisplayHeight >= 2160 ? [{ value: '4K', label: '4K' }] : []),
              ]}
              value={value.resolution}
              onChange={(val) => onChange({ resolution: val as '720p' | '1080p' | '1440p' | '4K' })}
            />
          </div>

          {/* Frame rate */}
          <div className="form-control">
            <label className="label">
              <span className="label-text text-base-content">Frame Rate</span>
            </label>
            <DropdownSelect
              buttonClassName={OVERRIDE_DROPDOWN_CLASS}
              items={[24, 30, 60, 120, 144].map((v) => ({
                value: String(v),
                label: `${v} FPS`,
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
              buttonClassName={OVERRIDE_DROPDOWN_CLASS}
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
              <BitrateInput
                value={value.bitrate}
                onChange={(bitrate) => onChange({ bitrate })}
                className="bg-base-300"
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
                <BitrateInput
                  value={value.minBitrate}
                  onChange={(min) =>
                    onChange({ minBitrate: min, maxBitrate: Math.max(min, value.maxBitrate) })
                  }
                  className="bg-base-300"
                />
              </div>
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Maximum Bitrate</span>
                </label>
                <BitrateInput
                  value={value.maxBitrate}
                  onChange={(max) =>
                    onChange({ maxBitrate: max, minBitrate: Math.min(max, value.minBitrate) })
                  }
                  className="bg-base-300"
                />
              </div>
            </>
          )}

          {/* CRF */}
          {value.rateControl === 'CRF' && (
            <div className="form-control">
              <label className="label">
                <span className="label-text text-base-content">CRF Value</span>
              </label>
              <div className="relative w-full">
                <input
                  type="number"
                  min={0}
                  max={51}
                  defaultValue={value.crfValue}
                  onBlur={(e) =>
                    onChange({
                      crfValue: clampInt(e.target.value, 0, 51, 23),
                    })
                  }
                  className="input input-bordered bg-base-300 w-full pr-14 outline-none focus:border-base-400"
                />
                <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
                  0-51
                </span>
              </div>
            </div>
          )}

          {/* CQ */}
          {value.rateControl === 'CQP' && (
            <div className="form-control">
              <label className="label">
                <span className="label-text text-base-content">CQ Level</span>
              </label>
              <div className="relative w-full">
                <input
                  type="number"
                  min={0}
                  max={30}
                  defaultValue={value.cqLevel}
                  onBlur={(e) =>
                    onChange({
                      cqLevel: clampInt(e.target.value, 0, 30, 20),
                    })
                  }
                  className="input input-bordered bg-base-300 w-full pr-14 outline-none focus:border-base-400"
                />
                <span className="absolute inset-y-0 right-3 flex items-center text-sm opacity-60 pointer-events-none">
                  0-30
                </span>
              </div>
            </div>
          )}

          {/* Encoder */}
          <div className="form-control">
            <label className="label">
              <span className="label-text text-base-content">Video Encoder</span>
            </label>
            <DropdownSelect
              buttonClassName={OVERRIDE_DROPDOWN_CLASS}
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
              buttonClassName={OVERRIDE_DROPDOWN_CLASS}
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
      </Collapse>
    </div>
  );
}
