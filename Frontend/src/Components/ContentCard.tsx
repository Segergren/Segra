import { useMemo, useRef, useState, useEffect, useLayoutEffect, useCallback } from 'react';
import { useSettings } from '../Context/SettingsContext';
import { useAppState } from '../Context/AppStateContext';
import { BookmarkType, Content, includeInHighlight } from '../Models/types';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { openFileLocation } from '../Utils/FileUtils';
import { useAuth } from '../Hooks/useAuth.tsx';
import { useModal } from '../Context/ModalContext';
import UploadModal from './UploadModal';
import {
  Upload,
  FolderOpen,
  PenLine,
  Trash2,
  Link,
  Check,
  Ellipsis,
  Minimize2,
  Crown,
  ExternalLink,
  Copy,
  Bookmark,
} from 'lucide-react';
import { useAiHighlights } from '../Context/AiHighlightsContext';
import { useCompression } from '../Context/CompressionContext';
import Button from './Button';

type VideoType = 'Session' | 'Buffer' | 'Clip' | 'Highlight';

// Content keys (type:fileName) present at the first populated list. Anything absent afterwards
// appeared while the app was open and is the only kind whose thumbnail animates in.
const knownContentKeys = new Set<string>();
let hasSeededKnownContent = false;

// Thumbnails already loaded this session, so a remounted card shows instantly without re-animating.
const loadedThumbnailKeys = new Set<string>();

interface VideoCardProps {
  content?: Content; // Optional for skeleton cards
  type: VideoType;
  onClick?: (video: Content) => void; // Click handler for the entire card
  isLoading?: boolean; // Indicates if this is a loading (skeleton) card
  isSelected?: boolean; // Whether this card is selected in multi-select mode
  isSelectionMode?: boolean; // Whether multi-select mode is active
  isHighlighted?: boolean; // Briefly pulse the card to draw attention (e.g. after import)
}

export default function ContentCard({
  content,
  type,
  onClick,
  isLoading,
  isSelected = false,
  isSelectionMode = false,
  isHighlighted = false,
}: VideoCardProps) {
  const { enableAi, showNewBadgeOnVideos, airplaneMode } = useSettings();
  const { cacheFolder, content: allContent } = useAppState();
  const { session } = useAuth();
  const { openModal, closeModal } = useModal();
  const { aiProgress } = useAiHighlights();
  const { compressionProgress, isCompressing } = useCompression();

  const isBeingCompressed = content?.filePath ? isCompressing(content.filePath) : false;
  const currentCompressionProgress = content?.filePath
    ? compressionProgress[content.filePath]
    : undefined;

  const dropdownRef = useRef<HTMLDivElement>(null);
  const [isDropdownOpen, setIsDropdownOpen] = useState(false);
  const [copied, setCopied] = useState(false);
  const [opened, setOpened] = useState(false);
  const [isRenaming, setIsRenaming] = useState(false);
  const [renameValue, setRenameValue] = useState('');
  const renameInputRef = useRef<HTMLInputElement>(null);

  const thumbnailRef = useRef<HTMLImageElement>(null);
  const thumbnailKey = `${type}:${content?.fileName ?? ''}`;
  const isNewContent =
    hasSeededKnownContent && content != null && !knownContentKeys.has(thumbnailKey);

  // Existing content starts "loaded" (instant); only newly appeared content crossfades from the shimmer.
  const [thumbnailLoaded, setThumbnailLoaded] = useState(
    () => !isNewContent || loadedThumbnailKeys.has(thumbnailKey),
  );

  // Seed the known set on the first populated list; record each card on mount so it stays "known".
  useEffect(() => {
    if (!hasSeededKnownContent && allContent.length > 0) {
      for (const item of allContent) knownContentKeys.add(`${item.type}:${item.fileName}`);
      hasSeededKnownContent = true;
    }
    if (content?.fileName) knownContentKeys.add(thumbnailKey);
  }, [allContent, thumbnailKey, content?.fileName]);

  const markThumbnailLoaded = useCallback(() => {
    loadedThumbnailKeys.add(thumbnailKey);
    setThumbnailLoaded(true);
  }, [thumbnailKey]);

  // A cached thumbnail can already be `complete` on mount. Mark it loaded before paint so it shows
  // instantly (no crossfade) rather than briefly shimmering.
  useLayoutEffect(() => {
    const img = thumbnailRef.current;
    if (img?.complete && img.naturalWidth > 0) {
      markThumbnailLoaded();
    }
  }, [markThumbnailLoaded]);

  const updateDropdownPosition = useCallback(() => {
    if (dropdownRef.current) {
      const rect = dropdownRef.current.getBoundingClientRect();
      const spaceBelow = window.innerHeight - rect.bottom;
      // If less than 245px below, open upward
      if (spaceBelow < 245) {
        dropdownRef.current.classList.add('dropdown-top');
      } else {
        dropdownRef.current.classList.remove('dropdown-top');
      }
    }
  }, []);

  // Update position on scroll/resize while dropdown is open
  useEffect(() => {
    if (!isDropdownOpen) return;
    const handler = () => updateDropdownPosition();
    window.addEventListener('resize', handler);
    window.addEventListener('scroll', handler, true); // Capture phase for parent scrolling
    return () => {
      window.removeEventListener('resize', handler);
      window.removeEventListener('scroll', handler, true);
    };
  }, [isDropdownOpen, updateDropdownPosition]);

  if (isLoading) {
    // Render a skeleton card
    return (
      <div
        className={
          type === 'Highlight'
            ? 'card card-compact w-full relative highlight-card'
            : 'card card-compact bg-base-300 text-gray-300 w-full border border-[#49515b]'
        }
      >
        {type === 'Highlight' && (
          <div className="absolute inset-0 rounded-lg highlight-border">
            <div className="card absolute inset-px bg-base-300 z-2">
              <figure className="relative aspect-w-16 aspect-h-9">
                {/* Thumbnail Skeleton */}
                <div
                  className="skeleton w-full h-0 relative bg-base-200/75 rounded-none"
                  style={{ paddingTop: '56.25%' }}
                ></div>
                <span
                  className="absolute bottom-2 right-2 bg-opacity-75 text-white text-xs rounded skeleton w-full"
                  style={{ aspectRatio: '16/9', visibility: 'hidden' }}
                ></span>
              </figure>
              <div className="card-body text-gray-300">
                {/* Title Skeleton */}
                <div className="skeleton bg-base-300 h-5 w-3/4 mb-2 mt-1"></div>
                {/* Metadata Skeleton */}
                <div className="skeleton h-4 w-4/6"></div>
              </div>
            </div>
          </div>
        )}

        {type !== 'Highlight' && (
          <>
            {/* Mirrors the loaded card's figure/body exactly so the skeleton is the same height. */}
            <figure className="relative aspect-video bg-black">
              <div className="skeleton w-full h-full rounded-none bg-base-200/75"></div>
            </figure>
            <div className="card-body gap-1 pt-2">
              <div className="flex justify-between items-center">
                <h2 className="card-title !block w-3/4">
                  {/* Title Skeleton */}
                  <span className="skeleton inline-block align-middle h-4 w-full rounded"></span>
                </h2>
                <span
                  aria-hidden="true"
                  className="btn btn-ghost btn-sm btn-circle invisible shrink-0"
                >
                  <Ellipsis size={24} />
                </span>
              </div>
              {/* Metadata Skeleton */}
              <div className="text-sm">
                <span className="skeleton inline-block align-middle h-3.5 w-4/6 rounded"></span>
              </div>
            </div>
          </>
        )}
      </div>
    );
  }

  const getThumbnailPath = (): string => {
    // Map type to folder name for thumbnails in AppData
    const folderName =
      type === 'Session'
        ? 'Full Sessions'
        : type === 'Buffer'
          ? 'Replay Buffers'
          : type === 'Clip'
            ? 'Clips'
            : 'Highlights';
    const thumbnailPath = `${cacheFolder}/thumbnails/${folderName}/${content?.fileName}.jpeg`;
    return `http://localhost:2222/api/thumbnail?input=${encodeURIComponent(thumbnailPath)}`;
  };

  const formatDuration = (duration: string): string => {
    try {
      const time = duration.split('.')[0]; // Remove fractional seconds
      const [hours, minutes, seconds] = time.split(':').map(Number);

      if (hours > 0) {
        return `${hours}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
      } else {
        return `${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
      }
    } catch {
      return '00:00'; // Fallback for invalid duration
    }
  };

  const thumbnailPath = getThumbnailPath();
  const formattedDuration = formatDuration(content!.duration);
  const manualBookmarkCount =
    content?.bookmarks?.filter((b) => b.type === BookmarkType.Manual).length ?? 0;

  // Check if content was created within the last hour and hasn't been viewed yet
  const isRecent = useMemo((): boolean => {
    if (!content) return false;

    // Check if this content has been viewed already
    const viewedContent = localStorage.getItem('viewed-content') || '{}';
    const viewedContentObj = JSON.parse(viewedContent);
    if (viewedContentObj[content.fileName]) {
      return false;
    }

    const createdAt = new Date(content.createdAt);
    const now = new Date();
    const diffInHours = (now.getTime() - createdAt.getTime()) / (1000 * 60 * 60);
    return diffInHours <= 1;
  }, [content?.fileName, content?.createdAt]);

  const markAsViewed = () => {
    if (!content) return;

    const viewedContent = localStorage.getItem('viewed-content') || '{}';
    const viewedContentObj = JSON.parse(viewedContent);
    viewedContentObj[content.fileName] = true;
    localStorage.setItem('viewed-content', JSON.stringify(viewedContentObj));
  };

  const handleUpload = () => {
    openModal(
      <UploadModal
        key={`${Math.random()}`}
        video={content!}
        onClose={closeModal}
        onUpload={(title, description, visibility) => {
          const parameters: any = {
            FilePath: content!.filePath,
            JWT: session?.access_token,
            Game: content?.game,
            Title: title,
            Description: description,
            Visibility: visibility,
            IgdbId: content?.igdbId?.toString(),
          };

          sendMessageToBackend('UploadContent', parameters);
        }}
      />,
    );
  };

  const handleCreateAiClip = () => {
    const parameters: any = {
      FileName: content!.fileName,
    };

    sendMessageToBackend('CreateAiClip', parameters);
  };

  const handleDelete = () => {
    const parameters: any = {
      FileName: content!.fileName,
      ContentType: type,
    };

    sendMessageToBackend('DeleteContent', parameters);
  };

  const startRenaming = () => {
    setRenameValue(content!.title || '');
    setIsRenaming(true);
    setTimeout(() => {
      renameInputRef.current?.focus();
      renameInputRef.current?.select();
    }, 0);
  };

  const commitRename = () => {
    if (!isRenaming) return;
    setIsRenaming(false);
    const trimmed = renameValue.trim();
    const invalidChars = /[<>:"/\\|?*]/;
    if (trimmed && invalidChars.test(trimmed)) return;
    sendMessageToBackend('RenameContent', {
      FileName: content!.fileName,
      ContentType: type,
      Title: trimmed,
    });
  };

  const handleOpenFileLocation = () => openFileLocation(content!.filePath);

  return (
    <div
      data-content-filename={content!.fileName}
      className={`card card-compact bg-base-300 text-gray-300 w-full border border-[#49515b] ${isSelected ? '!outline !outline-1 !outline-primary' : ''} ${isHighlighted ? 'import-pulse' : ''} ${isBeingCompressed ? 'cursor-default opacity-75' : 'cursor-pointer'} ${isSelectionMode ? 'select-none' : ''}`}
      onClick={() => {
        if (isBeingCompressed) return;
        if (!isSelectionMode) markAsViewed();
        onClick?.(content!);
      }}
    >
      <figure className="relative aspect-video bg-black">
        {/* Shimmer crossfades out as the image fades in, so the figure never flashes black. */}
        <div
          className={`absolute inset-0 rounded-none bg-base-200/75 transition-opacity duration-200 ${thumbnailLoaded ? 'opacity-0' : 'skeleton opacity-100'}`}
        ></div>
        <img
          ref={thumbnailRef}
          src={thumbnailPath}
          alt={'thumbnail'}
          className={`w-full h-full object-contain select-none transition-opacity duration-200 ${thumbnailLoaded ? 'opacity-100' : 'opacity-0'}`}
          loading="lazy"
          width={1600}
          height={900}
          draggable={false}
          onLoad={markThumbnailLoaded}
          onError={markThumbnailLoaded}
        />
        <span className="absolute bottom-2 right-2 bg-black/75 text-white text-xs px-2 py-1 rounded">
          {formattedDuration}
        </span>
        {manualBookmarkCount > 0 && (
          <span className="absolute top-2 right-2 bg-black/75 text-yellow-400 text-xs px-2 py-1 rounded">
            <Bookmark size={12} fill="currentColor" className="inline align-middle mr-1" />
            <span className="align-middle">{manualBookmarkCount}</span>
          </span>
        )}
        {isSelectionMode && (
          <input
            type="checkbox"
            className="checkbox checkbox-primary checkbox-sm absolute top-2 left-2 [&:not(:checked)]:bg-black/30"
            checked={isSelected}
            readOnly
          />
        )}
        {isRecent &&
          (type === 'Session' || type === 'Buffer') &&
          showNewBadgeOnVideos &&
          !isSelectionMode && (
            <span className="absolute top-2 left-2 badge badge-primary badge-sm text-base-300 opacity-90">
              NEW
            </span>
          )}
        {currentCompressionProgress && (
          <div className="absolute inset-0 bg-black/60 flex flex-col items-center justify-center">
            <p className="text-white text-sm mb-2">
              {currentCompressionProgress.status === 'compressing'
                ? 'Compressing...'
                : currentCompressionProgress.status === 'done'
                  ? 'Done!'
                  : currentCompressionProgress.status === 'skipped'
                    ? 'Skipped'
                    : 'Error'}
            </p>
            {currentCompressionProgress.status === 'compressing' && (
              <div className="w-2/3 h-1.5 bg-white/20 rounded-full overflow-hidden">
                <div
                  className="h-full bg-white/90 rounded-full transition-all duration-1000"
                  style={{ width: `${currentCompressionProgress.progress}%` }}
                />
              </div>
            )}
            {currentCompressionProgress.message && (
              <p className="text-white/70 text-xs mt-1">{currentCompressionProgress.message}</p>
            )}
          </div>
        )}
      </figure>

      <div className="card-body gap-1 pt-2">
        <div className="flex justify-between items-center">
          {isRenaming ? (
            <input
              ref={renameInputRef}
              type="text"
              value={renameValue}
              onChange={(e) => setRenameValue(e.target.value)}
              onBlur={commitRename}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault();
                  commitRename();
                } else if (e.key === 'Escape') {
                  e.preventDefault();
                  setIsRenaming(false);
                }
              }}
              onClick={(e) => e.stopPropagation()}
              className="card-title !block truncate bg-transparent outline-none w-full"
              placeholder={content!.game || 'Untitled'}
            />
          ) : (
            <h2 className="card-title !block truncate">
              {content!.title || content!.game || 'Untitled'}
            </h2>
          )}
          <div
            ref={dropdownRef}
            className={`dropdown dropdown-end ${isBeingCompressed ? 'pointer-events-none opacity-50' : ''}`}
            onClick={(e) => e.stopPropagation()}
            onFocus={() => {
              updateDropdownPosition();
              setIsDropdownOpen(true);
            }}
            onBlur={(e) => {
              // Only close if focus moved outside the dropdown
              if (!e.currentTarget.contains(e.relatedTarget)) {
                setIsDropdownOpen(false);
              }
            }}
          >
            <label
              tabIndex={isBeingCompressed ? -1 : 0}
              className="btn btn-ghost btn-sm btn-circle hover:bg-white/10 active:bg-white/10"
            >
              <Ellipsis size={24} />
            </label>
            <ul
              tabIndex={0}
              className="dropdown-content menu bg-base-300 border border-base-400 rounded-box z-999 w-52 p-2"
            >
              {!airplaneMode && (type === 'Clip' || type === 'Highlight') && (
                <li>
                  <Button
                    variant="menuPrimary"
                    onClick={() => {
                      (document.activeElement as HTMLElement).blur();
                      handleUpload();
                    }}
                  >
                    <Upload size={20} />
                    <span>Upload</span>
                  </Button>
                </li>
              )}
              {(type === 'Clip' || type === 'Highlight' || type === 'Buffer') && (
                <li>
                  <Button
                    variant="menu"
                    onClick={() => {
                      (document.activeElement as HTMLElement).blur();
                      sendMessageToBackend('CopyFileToClipboard', {
                        FilePath: content!.filePath,
                      });
                    }}
                  >
                    <Copy size={20} />
                    <span>Copy</span>
                  </Button>
                </li>
              )}
              {type === 'Session' && enableAi && (
                <li>
                  {(() => {
                    const hasHighlightBookmarks = content?.bookmarks?.some((b) =>
                      includeInHighlight(b.type),
                    );
                    const isProcessing = Object.values(aiProgress).some(
                      (progress) =>
                        progress.content.fileName === content?.fileName &&
                        progress.status === 'processing',
                    );
                    const isDisabled = !hasHighlightBookmarks || isProcessing;

                    return (
                      <Button
                        variant="menuPurple"
                        disabled={isDisabled}
                        onClick={() => {
                          if (hasHighlightBookmarks && !isProcessing) {
                            (document.activeElement as HTMLElement).blur();
                            handleCreateAiClip();
                          }
                        }}
                      >
                        <Crown size={20} />
                        <span>
                          {isProcessing
                            ? 'Creating Highlight...'
                            : hasHighlightBookmarks
                              ? 'Create Highlight'
                              : 'No Highlights'}
                        </span>
                      </Button>
                    );
                  })()}
                </li>
              )}
              <li>
                <Button
                  variant="menu"
                  onClick={() => {
                    (document.activeElement as HTMLElement).blur();
                    startRenaming();
                  }}
                >
                  <PenLine size={20} />
                  <span>Rename</span>
                </Button>
              </li>
              <li>
                <Button
                  variant="menu"
                  onClick={() => {
                    (document.activeElement as HTMLElement).blur();
                    handleOpenFileLocation();
                  }}
                >
                  <FolderOpen size={20} />
                  <span>Open File Location</span>
                </Button>
              </li>
              {(type === 'Clip' || type === 'Highlight') &&
                !content?.fileName?.endsWith('_compressed') && (
                  <li>
                    <Button
                      variant="menu"
                      onClick={() => {
                        (document.activeElement as HTMLElement).blur();
                        sendMessageToBackend('CompressVideo', { FilePath: content!.filePath });
                      }}
                    >
                      <Minimize2 size={20} />
                      <span>Compress</span>
                    </Button>
                  </li>
                )}
              <li>
                <Button
                  variant="menuDanger"
                  onClick={() => {
                    (document.activeElement as HTMLElement).blur();
                    handleDelete();
                  }}
                >
                  <Trash2 size={20} />
                  <span>Delete</span>
                </Button>
              </li>
            </ul>
          </div>
        </div>
        <div className="text-sm text-gray-200 flex items-center justify-between w-full">
          <span>
            {content!.fileSize} &bull; {new Date(content!.createdAt).toLocaleDateString()}
          </span>
          {!airplaneMode && content!.uploadId && (
            <div className="flex absolute right-3 gap-0 pr-1">
              <span
                className="btn btn-ghost btn-sm btn-circle relative group hover:bg-white/10 active:bg-white/10"
                onClick={(e) => {
                  e.stopPropagation();
                  const url = `https://segra.tv/video/${content!.uploadId}`;
                  navigator.clipboard.writeText(url);
                  setCopied(true);
                  setTimeout(() => setCopied(false), 1500);
                }}
              >
                <label
                  className={`swap overflow-hidden justify-center ${copied ? 'swap-active' : ''}`}
                >
                  <div className="swap-off">
                    <Link size={20} />
                  </div>
                  <div className="swap-on">
                    <Check size={20} />
                  </div>
                </label>
              </span>
              <span
                className="btn btn-ghost btn-sm btn-circle hover:bg-white/10 active:bg-white/10"
                onClick={(e) => {
                  e.stopPropagation();
                  sendMessageToBackend('OpenInBrowser', {
                    Url: `https://segra.tv/video/${content!.uploadId}`,
                  });
                  setOpened(true);
                  setTimeout(() => setOpened(false), 1500);
                }}
              >
                <label
                  className={`swap overflow-hidden justify-center ${opened ? 'swap-active' : ''}`}
                >
                  <div className="swap-off">
                    <ExternalLink size={20} />
                  </div>
                  <div className="swap-on">
                    <Check size={20} />
                  </div>
                </label>
              </span>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
