import { useSettings } from '../Context/SettingsContext';
import ContentCard from './ContentCard';
import { useSelectedVideo } from '../Context/SelectedVideoContext';
import { Content, ContentType } from '../Models/types';
import { useScroll } from '../Context/ScrollContext';
import { useLayoutEffect, useRef, useState, useMemo, useEffect, useCallback } from 'react';
import { IconType } from 'react-icons';
import { MdUploadFile, MdDeleteOutline } from 'react-icons/md';
import { AnimatePresence, motion } from 'framer-motion';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import ContentFilters, { SortOption } from './ContentFilters';
import { useModal } from '../Context/ModalContext';

interface ContentPageProps {
  contentType: ContentType;
  sectionId: string;
  title: string;
  Icon: IconType;
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
  const { state } = useSettings();
  const { setSelectedVideo } = useSelectedVideo();
  const { scrollPositions, setScrollPosition } = useScroll();
  const { isModalOpen } = useModal();
  const containerRef = useRef<HTMLDivElement>(null);
  const isSettingScroll = useRef(false);

  const [selectedItems, setSelectedItems] = useState<Set<string>>(new Set());
  const [isCtrlPressed, setIsCtrlPressed] = useState(false);

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
    return [...new Set(games)].sort();
  }, [contentItems]);

  const filteredItems = useMemo(() => {
    let filtered = [...contentItems];

    if (selectedGames.length > 0) {
      filtered = filtered.filter((item) => selectedGames.includes(item.game));
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
  }, [contentItems, selectedGames, sortOption]);

  const handleGameFilterChange = (games: string[]) => {
    setSelectedGames(games);
    localStorage.setItem(`${sectionId}-filters`, JSON.stringify(games));
  };

  const handleSortChange = (option: SortOption) => {
    setSortOption(option);
    localStorage.setItem(`${sectionId}-sort`, JSON.stringify(option));
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

      if (e.key === 'Control') {
        setIsCtrlPressed(true);
      }

      if (e.ctrlKey && e.key === 'a') {
        e.preventDefault();
        if (selectedItems.size === filteredItems.length && filteredItems.length > 0) {
          setSelectedItems(new Set());
        } else {
          setSelectedItems(new Set(filteredItems.map((item) => item.fileName)));
        }
      }

      if (e.key === 'Escape') {
        setSelectedItems(new Set());
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
  }, [selectedItems, filteredItems, isModalOpen]);

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
            <button
              className="btn btn-sm no-animation btn-secondary border border-base-400 h-8 hover:text-primary hover:border-base-400 flex items-center gap-1 text-gray-300"
              onClick={() => sendMessageToBackend('ImportFile', { sectionId })}
            >
              <MdUploadFile size={16} />
              Import
            </button>
          )}
          <ContentFilters
            uniqueGames={uniqueGames}
            onGameFilterChange={handleGameFilterChange}
            onSortChange={handleSortChange}
            sectionId={sectionId}
            selectedGames={selectedGames}
            sortOption={sortOption}
          />
        </div>
      </div>

      {contentItems.length > 0 || hasProgress ? (
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 2xl:grid-cols-5 gap-4">
          {isProgressVisible && progressCardElement}

          {filteredItems.map((video, index) => (
            <ContentCard
              key={index}
              content={video}
              onClick={() => handleCardClick(video)}
              type={contentType}
              isSelected={selectedItems.has(video.fileName)}
              isSelectionMode={isCtrlPressed || selectedItems.size > 0}
            />
          ))}
        </div>
      ) : (
        <div className="flex flex-col items-center justify-center h-64 text-gray-500">
          <Icon className="text-6xl mb-4" />
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
            <button
              className="btn btn-sm btn-ghost bg-error/20 hover:bg-error/10 text-error border-error h-8"
              onClick={handleDeleteSelected}
            >
              <MdDeleteOutline size={16} />
              Delete
            </button>
            <button
              className="btn btn-sm btn-secondary border-base-400 hover:border-base-400 text-gray-400 hover:text-gray-300 h-8"
              onClick={() => setSelectedItems(new Set())}
            >
              Cancel
            </button>
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}
