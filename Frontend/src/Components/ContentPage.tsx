import { useAppState } from '../Context/AppStateContext';
import ContentCard from './ContentCard';
import { useSelectedVideo } from '../Context/SelectedVideoContext';
import { Content, ContentType } from '../Models/types';
import { useScroll } from '../Context/ScrollContext';
import { useLayoutEffect, useRef, useState, useMemo, useEffect, useCallback } from 'react';
import type { LucideIcon } from 'lucide-react';
import { ArrowLeft, FileUp, Folder, Grid3X3, Trash2 } from 'lucide-react';
import { AnimatePresence, motion } from 'framer-motion';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import ContentFilters, { SortOption } from './ContentFilters';
import { useModal } from '../Context/ModalContext';
import { useImports } from '../Context/ImportContext';
import Button from './Button';

// Escape a filename for use inside a CSS attribute-selector string. Windows
// filenames can't contain " or \, but escape defensively all the same.
const escapeAttrValue = (value: string) => value.replace(/["\\]/g, '\\$&');

type ContentViewMode = 'default' | 'folders';

const getContentFolderName = (item: Content) => {
  if (item.isImported) return 'Imported';
  return item.game?.trim() || 'Unknown Game';
};

const getContentTypeFolderName = (type: ContentType) =>
  type === 'Session'
    ? 'Full Sessions'
    : type === 'Buffer'
      ? 'Replay Buffers'
      : type === 'Clip'
        ? 'Clips'
        : 'Highlights';

interface ContentPageProps {
  contentType: ContentType;
  sectionId: string;
  title: string;
  Icon: LucideIcon;
  progressItems?: Record<string, any>; // For AI highlights or clipping progress
  isProgressVisible?: boolean;
  progressCardElement?: React.ReactNode; // Direct element instead of component
}

export default function ContentPage({
  contentType,
  sectionId,
  title,
  Icon,
  progressItems = {},
  isProgressVisible = false,
  progressCardElement,
}: ContentPageProps) {
  const state = useAppState();
  const { setSelectedVideo } = useSelectedVideo();
  const { scrollPositions, setScrollPosition } = useScroll();
  const { isModalOpen } = useModal();
  const { imports } = useImports();
  const containerRef = useRef<HTMLDivElement>(null);
  const isSettingScroll = useRef(false);

  // When a single-file import finishes, scroll the freshly imported card into
  // view. The completed import message doesn't carry the stored filename, so we
  // snapshot the current items and diff once the reloaded content arrives.
  const handledImportIdsRef = useRef<Set<string>>(new Set());
  const importSnapshotRef = useRef<Set<string> | null>(null);

  const [selectedItems, setSelectedItems] = useState<Set<string>>(new Set());
  const [isCtrlPressed, setIsCtrlPressed] = useState(false);
  const [highlightedFileName, setHighlightedFileName] = useState<string | null>(null);
  const [contentViewMode, setContentViewMode] = useState<ContentViewMode>(() => {
    const saved = localStorage.getItem(`${sectionId}-view-mode`);
    return saved === 'folders' ? 'folders' : 'default';
  });
  const [selectedFolder, setSelectedFolder] = useState<string | null>(null);
  const highlightTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const contentItems = state.content.filter((video) => video.type === contentType);
  const [selectedGames, setSelectedGames] = useState<string[]>(() => {
    try {
      const saved = localStorage.getItem(`${sectionId}-filters`);
      return saved ? JSON.parse(saved) : [];
    } catch {
      return [];
    }
  });

  const [sortOption, setSortOption] = useState<SortOption>(() => {
    try {
      const saved = localStorage.getItem(`${sectionId}-sort`);
      return saved ? JSON.parse(saved) : 'newest';
    } catch {
      return 'newest';
    }
  });

  const uniqueGames = useMemo(() => {
    const games = contentItems.map((item) => item.game);
    const uniqueGameList = [...new Set(games)].sort();
    // Add "Imported" to the list if any items are imported
    if (contentItems.some((item) => item.isImported)) {
      return ['Imported', ...uniqueGameList];
    }
    return uniqueGameList;
  }, [contentItems]);

  const filteredItems = useMemo(() => {
    let filtered = [...contentItems];

    if (contentViewMode === 'default' && selectedGames.length > 0) {
      filtered = filtered.filter((item) => {
        if (selectedGames.includes('Imported') && item.isImported) {
          return true;
        }
        return selectedGames.filter((g) => g !== 'Imported').includes(item.game);
      });
    }

    filtered.sort((a, b) => {
      switch (sortOption) {
        case 'newest':
          return new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime();
        case 'oldest':
          return new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime();
        case 'size':
          return (b.fileSizeKb ?? 0) - (a.fileSizeKb ?? 0);
        case 'duration': {
          const toSecs = (dur: string) =>
            dur.split(':').reduce((acc, t) => 60 * acc + (parseInt(t, 10) || 0), 0);
          return toSecs(b.duration) - toSecs(a.duration);
        }
        case 'game': {
          const byGame = a.game.localeCompare(b.game);
          return byGame !== 0
            ? byGame
            : new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime();
        }
        default:
          return 0;
      }
    });

    return filtered;
  }, [contentItems, contentViewMode, selectedGames, sortOption]);

  const folderGroups = useMemo(() => {
    const groups = new Map<string, Content[]>();

    for (const item of filteredItems) {
      const folderName = getContentFolderName(item);
      const existing = groups.get(folderName) ?? [];
      existing.push(item);
      groups.set(folderName, existing);
    }

    return Array.from(groups.entries()).map(([name, items]) => {
      const sortedItems = [...items].sort(
        (a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
      );
      const latestItem = sortedItems[0];
      const coverImageId = sortedItems.find((item) => item.coverImageId)?.coverImageId;
      const totalSizeKb = items.reduce((total, item) => total + (item.fileSizeKb ?? 0), 0);

      return {
        name,
        items,
        latestItem,
        coverImageId,
        totalSizeKb,
      };
    });
  }, [filteredItems]);

  const selectedFolderGroup = useMemo(
    () => folderGroups.find((group) => group.name === selectedFolder) ?? null,
    [folderGroups, selectedFolder],
  );
  const visibleItems = selectedFolderGroup?.items ?? filteredItems;

  const getFolderArtworkUrl = (group: (typeof folderGroups)[number]) => {
    if (group.coverImageId) {
      return `https://segra.tv/api/games/cover/${group.coverImageId}`;
    }

    if (!group.latestItem) return null;

    const thumbnailPath = `${state.cacheFolder}/thumbnails/${getContentTypeFolderName(contentType)}/${group.latestItem.fileName}.jpeg`;
    return `http://localhost:2222/api/thumbnail?input=${encodeURIComponent(thumbnailPath)}`;
  };

  const handleGameFilterChange = (games: string[]) => {
    setSelectedGames(games);
    localStorage.setItem(`${sectionId}-filters`, JSON.stringify(games));
  };

  const handleSortChange = (option: SortOption) => {
    setSortOption(option);
    localStorage.setItem(`${sectionId}-sort`, JSON.stringify(option));
  };

  const handleViewModeChange = (mode: ContentViewMode) => {
    setContentViewMode(mode);
    localStorage.setItem(`${sectionId}-view-mode`, mode);
    if (mode === 'default') {
      setSelectedFolder(null);
    }
  };

  const formatFolderSize = (sizeKb: number) => {
    if (sizeKb <= 0) return null;

    const sizeMb = sizeKb / 1024;
    if (sizeMb < 1024) {
      return `${Math.round(sizeMb)} MB`;
    }

    return `${(sizeMb / 1024).toFixed(1)} GB`;
  };

  const formatFolderDate = (date: string) => {
    const parsed = new Date(date);
    if (Number.isNaN(parsed.getTime())) return null;
    return parsed.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  };

  const handlePlay = (video: Content) => {
    setSelectedVideo(video);
  };

  const handleCardClick = useCallback(
    (video: Content) => {
      if (isCtrlPressed) {
        setSelectedItems((prev) => {
          const newSet = new Set(prev);
          if (newSet.has(video.fileName)) {
            newSet.delete(video.fileName);
          } else {
            newSet.add(video.fileName);
          }
          return newSet;
        });
      } else {
        if (selectedItems.size === 0) {
          handlePlay(video);
        } else {
          setSelectedItems(new Set());
        }
      }
    },
    [isCtrlPressed, selectedItems.size],
  );

  const handleDeleteSelected = useCallback(() => {
    if (selectedItems.size === 0) return;

    const items = Array.from(selectedItems).map((fileName) => ({
      FileName: fileName,
      ContentType: contentType,
    }));

    sendMessageToBackend('DeleteMultipleContent', { Items: items });
    setSelectedItems(new Set());
  }, [selectedItems, contentType]);

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      if (isModalOpen) return;
      if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return;

      if (e.key === 'Control') {
        setIsCtrlPressed(true);
      }

      if (e.ctrlKey && e.key === 'a') {
        e.preventDefault();
        if (selectedItems.size === visibleItems.length && visibleItems.length > 0) {
          setSelectedItems(new Set());
        } else {
          setSelectedItems(new Set(visibleItems.map((item) => item.fileName)));
        }
      }

      if (e.key === 'Escape') {
        setSelectedItems(new Set());
      }

      if (e.key === 'Delete' && selectedItems.size > 0) {
        e.preventDefault();
        handleDeleteSelected();
      }
    };

    const handleKeyUp = (e: KeyboardEvent) => {
      if (isModalOpen) return;

      if (e.key === 'Control') {
        setIsCtrlPressed(false);
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    window.addEventListener('keyup', handleKeyUp);

    return () => {
      window.removeEventListener('keydown', handleKeyDown);
      window.removeEventListener('keyup', handleKeyUp);
    };
  }, [selectedItems, visibleItems, isModalOpen, handleDeleteSelected]);

  useEffect(() => {
    if (selectedFolder && !selectedFolderGroup) {
      setSelectedFolder(null);
    }
  }, [selectedFolder, selectedFolderGroup]);

  const prevContentFileNamesRef = useRef<string>('');

  useEffect(() => {
    const currentKey = contentItems.map((item) => item.fileName).join(',');

    if (currentKey === prevContentFileNamesRef.current) return;
    prevContentFileNamesRef.current = currentKey;

    const validFileNames = new Set(contentItems.map((item) => item.fileName));

    setSelectedItems((prev) => {
      let hasInvalid = false;
      prev.forEach((fileName) => {
        if (!validFileNames.has(fileName)) {
          hasInvalid = true;
        }
      });
      if (!hasInvalid) return prev; // Return same reference if nothing changed

      const newSet = new Set<string>();
      prev.forEach((fileName) => {
        if (validFileNames.has(fileName)) {
          newSet.add(fileName);
        }
      });
      return newSet;
    });
  }, [contentItems]);

  // Detect a single-file import completing and remember the items that existed
  // just before its reloaded content arrives.
  useEffect(() => {
    for (const importItem of Object.values(imports)) {
      if (
        importItem.status === 'done' &&
        importItem.totalFiles === 1 &&
        !handledImportIdsRef.current.has(importItem.id)
      ) {
        handledImportIdsRef.current.add(importItem.id);
        importSnapshotRef.current = new Set(contentItems.map((item) => item.fileName));
      }
    }
  }, [imports, contentItems]);

  // Once the reloaded content includes the newly imported item, scroll to it.
  useEffect(() => {
    const snapshot = importSnapshotRef.current;
    if (!snapshot) return;

    const newItem = contentItems.find((item) => item.isImported && !snapshot.has(item.fileName));
    if (!newItem) return; // Reloaded content hasn't arrived yet

    importSnapshotRef.current = null;

    // Pulse the card border twice to draw the eye to it (0.7s delay + 2x0.9s = 2.5s).
    setHighlightedFileName(newItem.fileName);
    if (highlightTimeoutRef.current) clearTimeout(highlightTimeoutRef.current);
    highlightTimeoutRef.current = setTimeout(() => setHighlightedFileName(null), 2600);

    requestAnimationFrame(() => {
      containerRef.current
        ?.querySelector(`[data-content-filename="${escapeAttrValue(newItem.fileName)}"]`)
        ?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    });
  }, [contentItems]);

  useEffect(() => {
    return () => {
      if (highlightTimeoutRef.current) clearTimeout(highlightTimeoutRef.current);
    };
  }, []);

  useLayoutEffect(() => {
    const position =
      sectionId === 'clips'
        ? scrollPositions.clips
        : sectionId === 'highlights'
          ? scrollPositions.highlights
          : sectionId === 'replayBuffer'
            ? scrollPositions.replayBuffer
            : sectionId === 'sessions'
              ? scrollPositions.sessions
              : 0;

    if (containerRef.current && position > 0) {
      isSettingScroll.current = true;
      containerRef.current.scrollTop = position;
      setTimeout(() => {
        isSettingScroll.current = false;
      }, 100);
    }
  }, []); // Only run on mount

  const scrollTimeout = useRef<ReturnType<typeof setTimeout> | null>(null);

  const handleScroll = () => {
    if (containerRef.current && !isSettingScroll.current) {
      if (scrollTimeout.current) {
        clearTimeout(scrollTimeout.current);
      }

      scrollTimeout.current = setTimeout(() => {
        const currentPos = containerRef.current?.scrollTop;
        if (currentPos === undefined) return;

        const pageKey =
          sectionId === 'clips'
            ? 'clips'
            : sectionId === 'highlights'
              ? 'highlights'
              : sectionId === 'replayBuffer'
                ? 'replayBuffer'
                : sectionId === 'sessions'
                  ? 'sessions'
                  : null;

        if (pageKey) {
          setScrollPosition(pageKey, currentPos);
        }
      }, 500);
    }
  };

  const progressValues = Object.values(progressItems);
  const hasProgress = progressValues.length > 0;

  return (
    <div
      ref={containerRef}
      className="p-5 space-y-6 overflow-y-scroll h-full bg-base-200 overflow-x-hidden"
      onScroll={handleScroll}
    >
      <div className="flex justify-between items-center mb-4">
        <div className="flex items-center gap-3">
          <h1 className="text-3xl font-bold">{title}</h1>
        </div>
        <div className="flex items-center gap-2">
          {(sectionId === 'sessions' || sectionId === 'replayBuffer') && (
            <Button
              variant="primary"
              size="sm"
              className="no-animation h-8 gap-1"
              onClick={() => sendMessageToBackend('ImportFile', { sectionId })}
            >
              <FileUp size={16} />
              Import
            </Button>
          )}
          <div className="join">
            <button
              type="button"
              className={`join-item h-8 inline-flex items-center gap-1.5 rounded-l-lg border border-base-400 px-3 text-sm font-semibold transition-colors ${
                contentViewMode === 'default'
                  ? 'bg-base-300 text-primary'
                  : 'bg-transparent text-gray-300 hover:bg-white/10 hover:text-primary'
              }`}
              onClick={() => handleViewModeChange('default')}
              title="Default view"
            >
              <Grid3X3 size={16} />
              Default
            </button>
            <button
              type="button"
              className={`join-item h-8 inline-flex items-center gap-1.5 rounded-r-lg border border-base-400 px-3 text-sm font-semibold transition-colors ${
                contentViewMode === 'folders'
                  ? 'bg-base-300 text-primary'
                  : 'bg-transparent text-gray-300 hover:bg-white/10 hover:text-primary'
              }`}
              onClick={() => handleViewModeChange('folders')}
              title="Folder view"
            >
              <Folder size={16} />
              Folders
            </button>
          </div>
          <ContentFilters
            uniqueGames={uniqueGames}
            onGameFilterChange={handleGameFilterChange}
            onSortChange={handleSortChange}
            sectionId={sectionId}
            selectedGames={selectedGames}
            sortOption={sortOption}
            showGameFilter={contentViewMode === 'default'}
          />
        </div>
      </div>

      {contentItems.length > 0 || hasProgress ? (
        contentViewMode === 'folders' ? (
          <div className="space-y-4">
            {isProgressVisible && progressCardElement && (
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 2xl:grid-cols-5 gap-4">
                {progressCardElement}
              </div>
            )}

            {selectedFolderGroup ? (
              <>
                <div className="flex items-center justify-between gap-3">
                  <Button
                    variant="ghost"
                    size="sm"
                    className="no-animation h-8 gap-1"
                    onClick={() => setSelectedFolder(null)}
                  >
                    <ArrowLeft size={16} />
                    Back to Folders
                  </Button>
                  <span className="text-sm text-gray-400">
                    {selectedFolderGroup.name} · {selectedFolderGroup.items.length}{' '}
                    {selectedFolderGroup.items.length === 1 ? 'item' : 'items'}
                  </span>
                </div>

                <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 2xl:grid-cols-5 gap-4">
                  {selectedFolderGroup.items.map((video) => (
                    <ContentCard
                      key={video.fileName}
                      content={video}
                      onClick={() => handleCardClick(video)}
                      type={contentType}
                      isSelected={selectedItems.has(video.fileName)}
                      isSelectionMode={isCtrlPressed || selectedItems.size > 0}
                      isHighlighted={video.fileName === highlightedFileName}
                    />
                  ))}
                </div>
              </>
            ) : (
              <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 2xl:grid-cols-4 gap-5">
                {folderGroups.map((group) => {
                  const artworkUrl = getFolderArtworkUrl(group);
                  const folderSize = formatFolderSize(group.totalSizeKb);
                  const latestDate = group.latestItem
                    ? formatFolderDate(group.latestItem.createdAt)
                    : null;

                  return (
                    <button
                      key={group.name}
                      type="button"
                      className="group relative aspect-[16/9] overflow-hidden rounded-lg border border-custom bg-base-300 text-left shadow transition-all hover:border-primary/70 hover:-translate-y-0.5"
                      onClick={() => setSelectedFolder(group.name)}
                    >
                      {artworkUrl ? (
                        <img
                          src={artworkUrl}
                          alt=""
                          className="absolute inset-0 h-full w-full object-cover transition-transform duration-300 group-hover:scale-105"
                          loading="lazy"
                          draggable={false}
                        />
                      ) : (
                        <div className="absolute inset-0 flex items-center justify-center bg-base-300">
                          <Folder size={56} className="text-primary/70" />
                        </div>
                      )}
                      <div className="absolute inset-0 bg-gradient-to-t from-black/85 via-black/30 to-black/10" />
                      <div className="absolute inset-x-0 bottom-0 p-4">
                        <div className="flex items-end justify-between gap-3">
                          <div className="min-w-0">
                            <div className="truncate text-lg font-bold text-white">
                              {group.name}
                            </div>
                            <div className="mt-1 text-sm text-gray-300">
                              {group.items.length} {group.items.length === 1 ? 'item' : 'items'}
                              {folderSize && ` · ${folderSize}`}
                            </div>
                          </div>
                          {latestDate && (
                            <div className="shrink-0 rounded bg-black/55 px-2 py-1 text-xs text-gray-200">
                              {latestDate}
                            </div>
                          )}
                        </div>
                      </div>
                    </button>
                  );
                })}
              </div>
            )}
          </div>
        ) : (
          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 2xl:grid-cols-5 gap-4">
            {isProgressVisible && progressCardElement}

            {filteredItems.map((video) => (
              <ContentCard
                key={video.fileName}
                content={video}
                onClick={() => handleCardClick(video)}
                type={contentType}
                isSelected={selectedItems.has(video.fileName)}
                isSelectionMode={isCtrlPressed || selectedItems.size > 0}
                isHighlighted={video.fileName === highlightedFileName}
              />
            ))}
          </div>
        )
      ) : (
        <div className="flex flex-col items-center justify-center h-64 text-gray-500">
          <Icon size={60} className="mb-4" />
          <p className="text-xl">No {title.toLowerCase()} found</p>
        </div>
      )}

      <AnimatePresence>
        {selectedItems.size > 0 && (
          <motion.div
            initial={{ opacity: 0, y: 50 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, y: 50 }}
            transition={{ duration: 0.2 }}
            className="fixed bottom-3 left-1/2 -translate-x-1/2 bg-base-300 border border-base-400 rounded-xl px-4 py-2 flex items-center gap-3 shadow-lg z-50"
          >
            <span className="text-sm text-gray-300">{selectedItems.size} Selected</span>
            <Button variant="danger" size="sm" className="h-8" onClick={handleDeleteSelected}>
              <Trash2 size={16} />
              Delete
            </Button>
            <Button
              variant="primary"
              size="sm"
              className="h-8"
              onClick={() => setSelectedItems(new Set())}
            >
              Cancel
            </Button>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
