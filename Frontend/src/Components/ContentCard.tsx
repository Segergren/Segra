import { useMemo } from 'react';
import { useSettings } from '../Context/SettingsContext';
import { Content, includeInHighlight } from '../Models/types';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { openFileLocation } from '../Utils/FileUtils';
import { useAuth } from '../Hooks/useAuth.tsx';
import { useModal } from '../Context/ModalContext';
import UploadModal from './UploadModal';
import RenameModal from './RenameModal';
import {
  MdOutlineFileUpload,
  MdOutlineInsertDriveFile,
  MdDriveFileRenameOutline,
  MdDeleteOutline,
  MdOutlineLink,
  MdOutlineMoreHoriz,
  MdOutlineCompress,
} from 'react-icons/md';
import { HiOutlineSparkles } from 'react-icons/hi';
import { useAiHighlights } from '../Context/AiHighlightsContext';
import { useCompression } from '../Context/CompressionContext';
import { FiExternalLink } from 'react-icons/fi';

type VideoType = 'Session' | 'Buffer' | 'Clip' | 'Highlight';

interface VideoCardProps {
  content?: Content; // Optional for skeleton cards
  type: VideoType;
  onClick?: (video: Content) => void; // Click handler for the entire card
  isLoading?: boolean; // Indicates if this is a loading (skeleton) card
  isSelected?: boolean; // Whether this card is selected in multi-select mode
  isSelectionMode?: boolean; // Whether multi-select mode is active
}

export default function ContentCard({
  content,
  type,
  onClick,
  isLoading,
  isSelected = false,
  isSelectionMode = false,
}: VideoCardProps) {
  const { enableAi, showNewBadgeOnVideos, state } = useSettings();
  const { appDataFolder } = state;
  const { session } = useAuth();
  const { openModal, closeModal } = useModal();
  const { aiProgress } = useAiHighlights();
  const { compressionProgress, isCompressing } = useCompression();

  const isBeingCompressed = content?.filePath ? isCompressing(content.filePath) : false;
  const currentCompressionProgress = content?.filePath
    ? compressionProgress[content.filePath]
    : undefined;

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
                  className="skeleton w-full h-0 relative bg-base-300/70 rounded-none"
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
            <figure className="relative aspect-w-16 aspect-h-9">
              {/* Thumbnail Skeleton */}
              <div
                className="skeleton w-full h-0 relative bg-base-300/70 rounded-none"
                style={{ paddingTop: '56.25%' }}
              ></div>
              <span
                className="absolute bottom-2 right-2 bg-opacity-75 text-white text-xs rounded skeleton w-full"
                style={{ aspectRatio: '16/9', visibility: 'hidden' }}
              ></span>
            </figure>
            <div className="card card-body bg-base-300">
              {/* Title Skeleton */}
              <div className="skeleton bg-base-300 h-5 w-3/4 mb-2 mt-1"></div>
              {/* Metadata Skeleton */}
              <div className="skeleton h-4 w-4/6"></div>
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
    const thumbnailPath = `${appDataFolder}/thumbnails/${folderName}/${content?.fileName}.jpeg`;
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
    return diffInHours <= 1; // Content is considered recent if created within the last hour
  }, [content?.fileName, content?.createdAt]);

  // Mark content as viewed when clicked
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

  const handleRename = () => {
    openModal(
      <RenameModal
        content={content!}
        onClose={closeModal}
        onRename={(newName) => {
          const parameters: any = {
            FileName: content!.fileName,
            ContentType: type,
            Title: newName,
          };

          sendMessageToBackend('RenameContent', parameters);
        }}
      />,
    );
  };

  const handleOpenFileLocation = () => openFileLocation(content!.filePath);

  return (
    <div
      className={`card card-compact bg-base-300 text-gray-300 w-full border border-[#49515b] ${isSelected ? '!outline !outline-1 !outline-primary' : ''} ${isBeingCompressed ? 'cursor-default opacity-75' : 'cursor-pointer'} ${isSelectionMode ? 'select-none' : ''}`}
      onClick={() => {
        if (isBeingCompressed) return;
        if (!isSelectionMode) markAsViewed();
        onClick?.(content!);
      }}
    >
      <figure className="relative aspect-video bg-black">
        <img
          src={thumbnailPath}
          alt={'thumbnail'}
          className="w-full h-full object-contain"
          loading="lazy"
          width={1600}
          height={900}
        />
        <span className="absolute bottom-2 right-2 bg-black bg-opacity-75 text-white text-xs px-2 py-1 rounded">
          {formattedDuration}
        </span>
        {isSelectionMode && (
          <input
            type="checkbox"
            className="checkbox checkbox-primary checkbox-sm absolute top-2 left-2"
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

      <div className="card-body gap-1.5 pt-2">
        <div className="flex justify-between items-center">
          <h2 className="card-title !block truncate">
            {content!.title || content!.game || 'Untitled'}
          </h2>
          <div
            className={`dropdown dropdown-end ${isBeingCompressed ? 'pointer-events-none opacity-50' : ''}`}
            onClick={(e) => e.stopPropagation()}
          >
            <label
              tabIndex={isBeingCompressed ? -1 : 0}
              className="btn btn-ghost btn-sm btn-circle hover:bg-white/10 active:bg-white/10"
            >
              <MdOutlineMoreHoriz size="28" />
            </label>
            <ul
              tabIndex={0}
              className="dropdown-content menu bg-base-300 border border-base-400 rounded-box z-999 w-52 p-2"
            >
              {(type === 'Clip' || type === 'Highlight') && (
                <li>
                  <a
                    className="flex w-full items-center gap-2 px-4 py-3 text-primary hover:bg-primary/10 active:bg-primary/20 rounded-lg transition-all duration-200 hover:pl-5 outline-none"
                    onClick={() => {
                      // Blur the active element before handling upload
                      (document.activeElement as HTMLElement).blur();
                      handleUpload();
                    }}
                  >
                    <MdOutlineFileUpload size="20" />
                    <span>Upload</span>
                  </a>
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
                      <a
                        className={`flex w-full items-center gap-2 px-4 py-3 ${
                          isDisabled
                            ? 'text-gray-400 cursor-not-allowed'
                            : 'text-purple-400 hover:bg-purple-500/10 active:bg-purple-500/20'
                        } rounded-lg transition-all duration-200 hover:pl-5 outline-none`}
                        onClick={() => {
                          if (hasHighlightBookmarks && !isProcessing) {
                            (document.activeElement as HTMLElement).blur();
                            handleCreateAiClip();
                          }
                        }}
                      >
                        <HiOutlineSparkles size="20" />
                        <span>
                          {isProcessing
                            ? 'Creating Highlight...'
                            : hasHighlightBookmarks
                              ? 'Create Highlight'
                              : 'No Highlights'}
                        </span>
                      </a>
                    );
                  })()}
                </li>
              )}
              {(type === 'Clip' || type === 'Highlight') &&
                !content?.fileName?.endsWith('_compressed') && (
                  <li>
                    <a
                      className="flex w-full items-center gap-2 px-4 py-3 text-white hover:bg-white/5 active:bg-base-200/20 rounded-lg transition-all duration-200 hover:pl-5 outline-none"
                      onClick={() => {
                        (document.activeElement as HTMLElement).blur();
                        sendMessageToBackend('CompressVideo', { FilePath: content!.filePath });
                      }}
                    >
                      <MdOutlineCompress size="20" />
                      <span>Compress</span>
                    </a>
                  </li>
                )}
              <li>
                <a
                  className="flex w-full items-center gap-2 px-4 py-3 text-white hover:bg-white/5 active:bg-base-200/20 rounded-lg transition-all duration-200 hover:pl-5 outline-none"
                  onClick={() => {
                    (document.activeElement as HTMLElement).blur();
                    handleRename();
                  }}
                >
                  <MdDriveFileRenameOutline size="20" />
                  <span>Rename</span>
                </a>
              </li>
              <li>
                <a
                  className="flex w-full items-center gap-2 px-4 py-3 text-white hover:bg-white/5 active:bg-base-200/20 rounded-lg transition-all duration-200 hover:pl-5 outline-none"
                  onClick={() => {
                    (document.activeElement as HTMLElement).blur();
                    handleOpenFileLocation();
                  }}
                >
                  <MdOutlineInsertDriveFile size="20" />
                  <span>Open File Location</span>
                </a>
              </li>
              <li>
                <a
                  className="flex w-full items-center gap-2 px-4 py-3 text-error hover:bg-error/10 active:bg-error/20 rounded-lg transition-all duration-200 hover:pl-5 outline-none"
                  onClick={() => {
                    // I don't know why it doesn't hide by itself?
                    (document.activeElement as HTMLElement).blur();

                    handleDelete();
                  }}
                >
                  <MdDeleteOutline size="20" />
                  <span>Delete</span>
                </a>
              </li>
            </ul>
          </div>
        </div>
        <p className="text-sm text-gray-200 flex items-center justify-between w-full">
          <span>
            {content!.fileSize} &bull; {new Date(content!.createdAt).toLocaleDateString()}
          </span>
          {content!.uploadId && (
            <div className="flex absolute right-3 gap-0 pr-1">
              <span
                className="btn btn-ghost btn-sm btn-circle relative group hover:bg-white/10 active:bg-white/10"
                onClick={(e) => {
                  e.stopPropagation();
                  const url = `https://segra.tv/video/${content!.uploadId}`;
                  navigator.clipboard.writeText(url);

                  // Show tooltip
                  const tooltip = e.currentTarget.querySelector('.tooltip');
                  if (tooltip) {
                    tooltip.classList.remove('hidden');
                    setTimeout(() => {
                      tooltip.classList.add('hidden');
                    }, 1000);
                  }
                }}
              >
                <MdOutlineLink size={22} />
                <div className="tooltip tooltip-top absolute left-1/2 transform -translate-x-1/2 hidden bg-secondary text-white text-xs px-2 py-1 rounded whitespace-nowrap z-9999">
                  Copied!
                </div>
              </span>
              <span
                className="btn btn-ghost btn-sm btn-circle hover:bg-white/10 active:bg-white/10"
                onClick={(e) => {
                  e.stopPropagation();
                  sendMessageToBackend('OpenInBrowser', {
                    Url: `https://segra.tv/video/${content!.uploadId}`,
                  });
                }}
              >
                <FiExternalLink size={20} className="p-x-[1px]" />
              </span>
            </div>
          )}
        </p>
      </div>
    </div>
  );
}
