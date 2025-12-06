import React, { useRef, useState, useEffect, useMemo } from 'react';
import { Content, BookmarkType, Selection, Bookmark } from '../Models/types';
import { sendMessageToBackend } from '../Utils/MessageUtils';
import { useSettings, useSettingsUpdater } from '../Context/SettingsContext';
import { openFileLocation } from '../Utils/FileUtils';
import { useSelectedVideo } from '../Context/SelectedVideoContext';
import { DndProvider } from 'react-dnd';
import { HTML5Backend } from 'react-dnd-html5-backend';
import { useAuth } from '../Hooks/useAuth.tsx';
import { useSelections } from '../Context/SelectionsContext';
import { useUploads } from '../Context/UploadContext';
import { useModal } from '../Context/ModalContext';
import UploadModal from '../Components/UploadModal';
import { IconType } from 'react-icons';
import { FaGun, FaTrashCan } from 'react-icons/fa6';
import {
  MdAddBox,
  MdBookmark,
  MdBookmarkAdd,
  MdMovieCreation,
  MdOutlineHandshake,
  MdPause,
  MdPlayArrow,
  MdReplay10,
  MdForward10,
  MdOutlineFileUpload,
  MdVolumeUp,
  MdVolumeOff,
  MdVolumeMute,
  MdVolumeDown,
  MdFullscreen,
  MdFullscreenExit,
  MdArrowBack,
} from 'react-icons/md';
import { IoSkull, IoAdd, IoRemove } from 'react-icons/io5';
import SelectionCard from '../Components/SelectionCard';
import WaveSurfer from 'wavesurfer.js';
import { TbZoomIn, TbZoomOut } from 'react-icons/tb';
import { IoIosFootball } from 'react-icons/io';
import { AnimatePresence, motion } from 'framer-motion';

// Converts time string in format "HH:MM:SS.mmm" to seconds
const timeStringToSeconds = (timeStr: string): number => {
  const [time, milliseconds] = timeStr.split('.');
  const [hours, minutes, seconds] = time.split(':').map(Number);
  return hours * 3600 + minutes * 60 + seconds + (milliseconds ? Number(`0.${milliseconds}`) : 0);
};

const PLAYBACK_SPEEDS = [0.25, 0.5, 1, 1.5, 2] as const;
const formatPlaybackRateLabel = (rate: number) => `${rate}x`;

function TopInfoBar({ video }: { video: Content }) {
  const { setSelectedVideo } = useSelectedVideo();
  const created = new Date(video.createdAt);
  const isValidDate = !isNaN(created.getTime());
  const locale = Intl.DateTimeFormat().resolvedOptions().locale?.toLowerCase() || '';
  const isUS = locale.includes('-us');
  const createdDateStr = !isValidDate
    ? video.createdAt
    : isUS
      ? created.toLocaleDateString('en-US', { year: 'numeric', month: '2-digit', day: '2-digit' })
      : `${created.getFullYear()}-${String(created.getMonth() + 1).padStart(2, '0')}-${String(created.getDate()).padStart(2, '0')}`;

  const createdTimeStr = !isValidDate
    ? ''
    : created.toLocaleTimeString(isUS ? 'en-US' : undefined, {
      hour: '2-digit',
      minute: '2-digit',
      hour12: isUS,
    });

  return (
    <div className="flex items-center gap-2 px-2 py-1 mb-2 text-xs leading-tight text-gray-300 border rounded-lg bg-base-300 border-custom">
      <button
        className="h-6 min-h-0 px-1 text-gray-300 btn btn-ghost btn-xs hover:text-gray-200"
        onClick={() => setSelectedVideo(null)}
        aria-label="Back"
      >
        <MdArrowBack className="w-4 h-4" />
      </button>
      <div className="flex flex-wrap items-center gap-2">
        <span>
          Created: {createdDateStr}
          {createdTimeStr ? ` ${createdTimeStr}` : ''}
        </span>
        <span>•</span>
        <span>Size: {video.fileSize}</span>
        <span>•</span>
        <span>
          Location:{' '}
          <button
            className="text-gray-300 cursor-pointer hover:underline hover:text-gray-200"
            onClick={() => openFileLocation(video.filePath)}
          >
            {video.filePath}
          </button>
        </span>
      </div>
    </div>
  );
}

// Fetches a video thumbnail from the backend for a specific timestamp
const fetchThumbnailAtTime = async (videoPath: string, timeInSeconds: number): Promise<string> => {
  const url = `http://localhost:2222/api/thumbnail?input=${encodeURIComponent(videoPath)}&time=${timeInSeconds}`;
  const response = await fetch(url);

  if (!response.ok) {
    throw new Error(`Failed to fetch thumbnail: ${response.statusText}`);
  }

  const blob = await response.blob();
  return URL.createObjectURL(blob);
};

export default function VideoComponent({ video }: { video: Content }) {
  // Context hooks
  const settings = useSettings();
  const updateSettings = useSettingsUpdater();
  const { contentFolder } = settings;
  const { session } = useAuth();
  const { uploads } = useUploads();
  const { openModal, closeModal } = useModal();
  const {
    selections,
    addSelection,
    updateSelection,
    removeSelection,
    updateSelectionsArray,
    clearAllSelections,
  } = useSelections();

  // Refs
  const videoRef = useRef<HTMLVideoElement>(null);
  const scrollContainerRef = useRef<HTMLDivElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const playerContainerRef = useRef<HTMLDivElement>(null);
  const latestDraggedSelectionRef = useRef<Selection | null>(null);
  const speedButtonRef = useRef<HTMLButtonElement | null>(null);
  const speedDropdownRef = useRef<HTMLDivElement | null>(null);

  // Video state
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [zoom, setZoom] = useState(1);

  // Scale and pan state for zooming into the video element itself
  const [videoScale, setVideoScale] = useState(1);
  const [videoTranslate, setVideoTranslate] = useState({ x: 0, y: 0 });
  const [isPanning, setIsPanning] = useState(false);
  const [showSpeedMenu, setShowSpeedMenu] = useState(false);
  const videoPanStartRef = useRef<{ x: number; y: number } | null>(null);
  const videoLastPointerRef = useRef<number | null>(null);
  const panMovedRef = useRef(false);
  const videoScaleRef = useRef<number>(videoScale);

  useEffect(() => {
    videoScaleRef.current = videoScale;
  }, [videoScale]);

  // Close speed menu when clicking outside or pressing Escape
  useEffect(() => {
    if (!showSpeedMenu) return;

    const handleClickOutside = (event: MouseEvent) => {
      if (!speedDropdownRef.current?.contains(event.target as Node)) {
        setShowSpeedMenu(false);
        speedButtonRef.current?.blur();
      }
    };

    const handleEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setShowSpeedMenu(false);
        speedButtonRef.current?.blur();
      }
    };

    document.addEventListener('mousedown', handleClickOutside);
    document.addEventListener('keydown', handleEscape);
    return () => {
      document.removeEventListener('mousedown', handleClickOutside);
      document.removeEventListener('keydown', handleEscape);
    };
  }, [showSpeedMenu]);

  // Clamp translation so the video remains at least partially visible
  const clampTranslate = (t: { x: number; y: number }) => {
    const el = playerContainerRef.current;
    const vid = videoRef.current;
    if (!el || !vid) return t;
    const vw = el.clientWidth;
    const vh = el.clientHeight;
    const sw = vid.clientWidth * videoScaleRef.current;
    const sh = vid.clientHeight * videoScaleRef.current;

    // Horizontal clamp: if video wider than container, allow panning between left and right edges.
    // Otherwise center horizontally.
    let minX: number;
    let maxX: number;
    if (sw > vw) {
      minX = vw - sw; // video right edge aligns with container right
      maxX = 0; // video left edge aligns with container left
    } else {
      // center
      minX = maxX = (vw - sw) / 2;
    }

    // Vertical clamp: enforce the requested rules.
    // - when panning down, if top of video moves below top of parent, clip to top (y <= 0)
    // - when panning up, if bottom of video moves above bottom of parent, clip to bottom (y >= vh - sh)
    let minY: number;
    let maxY: number;
    if (sh > vh) {
      minY = vh - sh; // bottom of video aligned with bottom of parent
      maxY = 0; // top of video aligned with top of parent
    } else {
      // center vertically
      minY = maxY = (vh - sh) / 2;
    }

    return {
      x: Math.max(minX, Math.min(maxX, t.x)),
      y: Math.max(minY, Math.min(maxY, t.y)),
    };
  };

  const clampVideoScale = (s: number) => Math.min(Math.max(s, 1), 4);

  // Change video scale, optionally focusing on a specific point, otherwise center
  const applyVideoScale = (desiredScale: number, focusPoint?: { x: number; y: number }) => {
    const videoEl = videoRef.current;
    if (!videoEl) return;

    const previousScale = videoScaleRef.current || 1;
    const nextScale = clampVideoScale(desiredScale);
    if (previousScale === nextScale) return;

    const cx = focusPoint?.x ?? videoEl.clientWidth / 2;
    const cy = focusPoint?.y ?? videoEl.clientHeight / 2;
    const ratio = nextScale / previousScale;

    videoScaleRef.current = nextScale;
    setVideoScale(nextScale);
    setVideoTranslate((prev) => {
      if (nextScale === 1) {
        return { x: 0, y: 0 };
      }

      const x = prev.x - (cx - prev.x) * (ratio - 1);
      const y = prev.y - (cy - prev.y) * (ratio - 1);
      return clampTranslate({ x, y });
    });
  };

  // Wheel zoom handler for the video element (use Ctrl/Meta to activate)
  const onVideoWheel = (e: React.WheelEvent) => {
    if (!(e.ctrlKey || e.metaKey)) return;
    e.preventDefault();
    const videoEl = e.currentTarget;
    const rect = videoEl.getBoundingClientRect();
    const currentScale = videoScaleRef.current || 1;
    const localX = (e.clientX - rect.left) / currentScale;
    const localY = (e.clientY - rect.top) / currentScale;
    const factor = e.deltaY < 0 ? 1.12 : 0.9;

    applyVideoScale(currentScale * factor, { x: localX, y: localY });
  };

  // Container state
  const [containerWidth, setContainerWidth] = useState(0);
  const [isPlaying, setIsPlaying] = useState(true);
  const [showNoSegmentsIndicator, setShowNoSegmentsIndicator] = useState(false);
  const [volume, setVolume] = useState(() => {
    // Initialize volume from localStorage or default to 1
    const savedVolume = localStorage.getItem('segra-volume');
    return savedVolume ? parseFloat(savedVolume) : 1;
  });
  const [isMuted, setIsMuted] = useState(() => {
    // Initialize muted state from localStorage or default to false
    return localStorage.getItem('segra-muted') === 'true';
  });
  const [playbackRate, setPlaybackRate] = useState(() => {
    const saved = localStorage.getItem('segra-playbackRate');
    return saved ? parseFloat(saved) : 1;
  });
  const [controlsVisible, setControlsVisible] = useState(true);
  const controlsHideTimeoutRef = useRef<number | null>(null);
  const isPointerOverControlsRef = useRef(false);
  const [isPointerInPlayer, setIsPointerInPlayer] = useState(false);
  const [isFullscreen, setIsFullscreen] = useState(false);

  useEffect(() => {
    if (!controlsVisible) {
      setShowSpeedMenu(false);
      speedButtonRef.current?.blur();
    }
  }, [controlsVisible]);

  // Interaction state
  const [isDragging, setIsDragging] = useState(false);
  const [isInteracting, setIsInteracting] = useState(false);
  const [hoveredSelectionId, setHoveredSelectionId] = useState<number | null>(null);
  const [dragState, setDragState] = useState<{ id: number | null; offset: number }>({
    id: null,
    offset: 0,
  });
  const dragCandidateRef = useRef<{ id: number; startClientX: number; offset: number } | null>(
    null,
  );
  const [resizingSelectionId, setResizingSelectionId] = useState<number | null>(null);
  const [resizeDirection, setResizeDirection] = useState<'start' | 'end' | null>(null);
  const resizeCandidateRef = useRef<{
    id: number;
    direction: 'start' | 'end';
    startClientX: number;
  } | null>(null);

  const isHighlightOrClip = video.type === 'Highlight' || video.type === 'Clip';
  const videoWrapperClassName = [
    'block relative w-full',
    isFullscreen ? 'h-full' : 'rounded-lg overflow-hidden aspect-video max-h-[calc(100vh-100px)]',
    !isFullscreen && (isHighlightOrClip ? 'md:max-h-[calc(100vh-230px)]' : 'md:max-h-[calc(100vh-200px)]'),
  ]
    .filter(Boolean)
    .join(' ');

  // Computed values
  const basePixelsPerSecond = duration > 0 ? containerWidth / duration : 0;
  const pixelsPerSecond = basePixelsPerSecond * zoom;

  // Make sure bookmarks are only shown when we have valid duration and zoom
  // Prevents weird positioning on initial load
  const bookmarksReady = duration > 0 && pixelsPerSecond > 0;
  const sortedSelections = [...selections].sort((a, b) => a.startTime - b.startTime);
  const selectionsRef = useRef(selections);
  useEffect(() => {
    selectionsRef.current = selections;
  }, [selections]);

  // Icon mapping
  const iconMapping: Record<BookmarkType, IconType> = {
    Manual: MdBookmark,
    Kill: FaGun,
    Goal: IoIosFootball,
    Assist: MdOutlineHandshake,
    Death: IoSkull,
  };

  // Track in-flight thumbnail requests to avoid stale overwrites
  const thumbnailReqTokenRef = useRef<Map<number, number>>(new Map());

  // Refreshes the thumbnail for a selection without overwriting live fields
  const refreshSelectionThumbnail = async (selection: Selection): Promise<void> => {
    const id = selection.id;
    // Read the latest selection from state (may be undefined immediately after add)
    const current = selectionsRef.current.find((s) => s.id === id);

    // Mark loading on latest state if present (new selection already has isLoading=true)
    if (current) {
      updateSelection({ ...current, isLoading: true });
    }

    // Bump request token for this id
    const nextToken = (thumbnailReqTokenRef.current.get(id) ?? 0) + 1;
    thumbnailReqTokenRef.current.set(id, nextToken);

    try {
      const latest = selectionsRef.current.find((s) => s.id === id) ?? current ?? selection;
      const contentFileName = `${contentFolder}/${video.type.toLowerCase()}s/${video.fileName}.mp4`;
      const thumbnailUrl = await fetchThumbnailAtTime(contentFileName, latest.startTime);

      // Only apply if this is the latest request for this selection
      if (thumbnailReqTokenRef.current.get(id) === nextToken) {
        const newest = selectionsRef.current.find((s) => s.id === id) ?? latest;
        updateSelection({ ...newest, thumbnailDataUrl: thumbnailUrl, isLoading: false });
      }
    } catch {
      if (thumbnailReqTokenRef.current.get(id) === nextToken) {
        const newest = selectionsRef.current.find((s) => s.id === id) ?? current ?? selection;
        updateSelection({ ...newest, isLoading: false });
      }
    }
  };

  // Initialize video metadata and setup keyboard controls
  useEffect(() => {
    const vid = videoRef.current;
    if (!vid) return;

    // Apply saved volume and muted state on load
    vid.volume = volume;
    vid.muted = isMuted;
    // Apply saved playback rate
    vid.playbackRate = playbackRate;

    const onLoadedMetadata = () => {
      setDuration(vid.duration);
      setZoom(1);
    };

    const onPlay = () => setIsPlaying(true);
    const onPause = () => setIsPlaying(false);
    const onVolumeChange = () => {
      if (vid) {
        setVolume(vid.volume);
        setIsMuted(vid.muted);

        // Save to localStorage when volume changes
        localStorage.setItem('segra-volume', vid.volume.toString());
        localStorage.setItem('segra-muted', vid.muted.toString());
      }
    };

    const onRateChange = () => {
      if (vid) {
        const r = vid.playbackRate || 1;
        setPlaybackRate(r);
        localStorage.setItem('segra-playbackRate', r.toString());
      }
    };

    vid.addEventListener('loadedmetadata', onLoadedMetadata);
    vid.addEventListener('play', onPlay);
    vid.addEventListener('pause', onPause);
    vid.addEventListener('volumechange', onVolumeChange);
    vid.addEventListener('ratechange', onRateChange);

    const handleKeyDown = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement;
      const isTyping =
        target.tagName === 'INPUT' ||
        target.tagName === 'TEXTAREA' ||
        (target as any).isContentEditable;

      // Space to toggle play/pause globally (unless typing)
      if ((e.code === 'Space' || e.key === ' ' || e.key === 'Spacebar') && !isTyping) {
        if (e.repeat) return; // avoid rapid toggle on key repeat
        e.preventDefault();
        handlePlayPause();
        return;
      }

      // F to toggle fullscreen overlay (unless typing)
      if ((e.key === 'f' || e.key === 'F') && !isTyping) {
        if (e.repeat) return;
        e.preventDefault();
        toggleFullscreen();
        return;
      }

      // Arrow keys: seek 10s back/forward (allow holding)
      if ((e.key === 'ArrowLeft' || e.code === 'ArrowLeft') && !isTyping) {
        e.preventDefault();
        showControlsTemporarily();
        skipTime(-10);
        return;
      }
      if ((e.key === 'ArrowRight' || e.code === 'ArrowRight') && !isTyping) {
        e.preventDefault();
        showControlsTemporarily();
        skipTime(10);
        return;
      }

      // Volume up/down (5% steps, allow holding)
      if ((e.key === 'ArrowUp' || e.code === 'ArrowUp') && !isTyping) {
        e.preventDefault();
        setPlayerVolume((videoRef.current?.volume ?? volume) + 0.05);
        showControlsTemporarily();
        return;
      }
      if ((e.key === 'ArrowDown' || e.code === 'ArrowDown') && !isTyping) {
        e.preventDefault();
        setPlayerVolume((videoRef.current?.volume ?? volume) - 0.05);
        showControlsTemporarily();
        return;
      }

      // Mute/unmute
      if ((e.key === 'm' || e.key === 'M') && !isTyping) {
        e.preventDefault();
        toggleMute();
        showControlsTemporarily();
        return;
      }
      if (e.key === 'Escape' && isFullscreen) {
        e.preventDefault();
        exitFullscreen();
      }
    };

    const keyOptions: AddEventListenerOptions & EventListenerOptions = { capture: true };
    window.addEventListener('keydown', handleKeyDown, keyOptions);

    // No DOM fullscreen; we manage an overlay + window maximize from backend

    return () => {
      vid.removeEventListener('loadedmetadata', onLoadedMetadata);
      vid.removeEventListener('play', onPlay);
      vid.removeEventListener('pause', onPause);
      vid.removeEventListener('volumechange', onVolumeChange);
      vid.removeEventListener('ratechange', onRateChange);
      window.removeEventListener('keydown', handleKeyDown, keyOptions as any);
    };
  }, [volume, isMuted, isFullscreen]);

  // Handle video playback time updates using requestAnimationFrame for smooth updates
  useEffect(() => {
    const vid = videoRef.current;
    if (!vid) return;
    let rafId = 0;
    const updateCurrentTime = () => {
      setCurrentTime(vid.currentTime);
      if (!vid.paused && !vid.ended) {
        rafId = requestAnimationFrame(updateCurrentTime);
      }
    };
    const onPlay = () => {
      rafId = requestAnimationFrame(updateCurrentTime);
    };
    const onPause = () => {
      cancelAnimationFrame(rafId);
    };
    vid.addEventListener('play', onPlay);
    vid.addEventListener('pause', onPause);
    if (!vid.paused) onPlay();
    return () => {
      vid.removeEventListener('play', onPlay);
      vid.removeEventListener('pause', onPause);
      cancelAnimationFrame(rafId);
    };
  }, []);

  // Update container width on window resize
  useEffect(() => {
    if (scrollContainerRef.current) {
      setContainerWidth(scrollContainerRef.current.clientWidth);
    }

    const handleResize = () => {
      if (scrollContainerRef.current) {
        setContainerWidth(scrollContainerRef.current.clientWidth);
      }
    };

    const preventPageZoom = (e: WheelEvent) => {
      if (e.ctrlKey || e.metaKey) {
        e.preventDefault();
      }
    };

    window.addEventListener('resize', handleResize);
    window.addEventListener('wheel', preventPageZoom, { passive: false });

    return () => {
      window.removeEventListener('resize', handleResize);
      window.removeEventListener('wheel', preventPageZoom);
    };
  }, []);

  // Create refs to track zoom state
  const wheelZoomRef = useRef(zoom);

  // Update the wheel zoom ref when zoom changes from other sources (buttons)
  useEffect(() => {
    wheelZoomRef.current = zoom;
  }, [zoom]);

  // TODO: refactor
  const showControlsTemporarily = () => {
    setControlsVisible(true);

    if (controlsHideTimeoutRef.current) {
      clearTimeout(controlsHideTimeoutRef.current);
      controlsHideTimeoutRef.current = null;
    }

    if (isPointerOverControlsRef.current) {
      return;
    }

    controlsHideTimeoutRef.current = window.setTimeout(() => {
      if (!isPointerOverControlsRef.current) {
        setControlsVisible(false);
      }
      controlsHideTimeoutRef.current = null;
    }, 2500);
  };

  const handleControlsMouseEnter = () => {
    isPointerOverControlsRef.current = true;

    if (controlsHideTimeoutRef.current) {
      clearTimeout(controlsHideTimeoutRef.current);
      controlsHideTimeoutRef.current = null;
    }

    setControlsVisible(true);
  };

  const handleControlsMouseLeave = () => {
    isPointerOverControlsRef.current = false;
    showControlsTemporarily();
  };

  // Handle timeline zooming with mouse wheel
  useEffect(() => {
    const container = scrollContainerRef.current;
    if (!container) return;

    const handleWheel = (e: WheelEvent) => {
      e.preventDefault();
      if (duration === 0) return;

      // Get container dimensions
      const rect = container.getBoundingClientRect();
      const cursorX = e.clientX - rect.left;
      const scrollLeft = container.scrollLeft;

      // Calculate base pixels per second for time conversion
      const basePixelsPerSecond = containerWidth / duration;
      const oldPixelsPerSecond = basePixelsPerSecond * wheelZoomRef.current;

      // Calculate time at cursor position
      const timeAtCursor = (cursorX + scrollLeft) / oldPixelsPerSecond;

      // Calculate new zoom level
      const zoomFactor = e.deltaY < 0 ? 1.2 : 0.8;
      const newZoom = Math.min(Math.max(wheelZoomRef.current * zoomFactor, 1), 50);

      // Update zoom ref immediately
      wheelZoomRef.current = newZoom;

      // Calculate new scroll position based on cursor time point
      const newPixelsPerSecond = basePixelsPerSecond * newZoom;
      const newCursorPosition = timeAtCursor * newPixelsPerSecond;
      const newScrollLeft = newCursorPosition - cursorX;

      // Apply scroll position immediately
      requestAnimationFrame(() => {
        if (container) {
          container.scrollLeft = newScrollLeft;

          // Double check the position after the frame renders
          requestAnimationFrame(() => {
            if (container) {
              const currentScrollLeft = container.scrollLeft;
              if (Math.abs(currentScrollLeft - newScrollLeft) > 5) {
                container.scrollLeft = newScrollLeft;
              }
            }
          });
        }
      });

      // Update React state
      setZoom(newZoom);
    };

    const wheelEventOptions: AddEventListenerOptions = { passive: false };
    container.addEventListener('wheel', handleWheel, wheelEventOptions);

    return () => {
      container.removeEventListener('wheel', handleWheel, wheelEventOptions);
    };
  }, [duration, containerWidth]); // Remove zoom from dependencies to prevent recreation

  const handleZoomChange = (increment: boolean) => {
    if (!scrollContainerRef.current) return;

    const container = scrollContainerRef.current;
    const scrollLeft = container.scrollLeft;

    // Calculate time at marker position (current time)
    const basePixelsPerSecond = containerWidth / duration;
    const oldPixelsPerSecond = basePixelsPerSecond * zoom;

    // Use current time as the focus point for zooming
    const markerTime = currentTime;
    const markerPosition = markerTime * oldPixelsPerSecond;

    // Calculate new zoom
    const newZoom = increment ? zoom * 1.5 : zoom * 0.5;
    const finalZoom = Math.min(Math.max(newZoom, 1), 50);

    // Update zoom state
    setZoom(finalZoom);

    // Calculate new scroll position to keep marker in view
    setTimeout(() => {
      if (container) {
        const newPixelsPerSecond = basePixelsPerSecond * finalZoom;
        const newMarkerPosition = markerTime * newPixelsPerSecond;

        // Calculate new scroll position to center on marker
        // Adjust scroll to position marker at same relative position
        const viewportWidth = container.clientWidth;
        const markerOffset = markerPosition - scrollLeft;
        const visibleRatio = markerOffset / viewportWidth;
        const newScrollPosition = newMarkerPosition - visibleRatio * viewportWidth;

        container.scrollLeft = newScrollPosition;
      }
    }, 0);
  };

  // Video control functions
  const handlePlayPause = () => {
    if (videoRef.current) {
      if (videoRef.current.paused) {
        videoRef.current.play();
      } else {
        videoRef.current.pause();
      }
    }
  };

  const skipTime = (seconds: number) => {
    if (videoRef.current) {
      const newTime = videoRef.current.currentTime + seconds;
      videoRef.current.currentTime = Math.max(0, Math.min(newTime, videoRef.current.duration));
    }
  };

  const setPlayerVolume = (vol: number) => {
    const target = Math.max(0, Math.min(1, vol));
    const el = videoRef.current;
    if (!el) {
      setVolume(target);
      return;
    }
    el.volume = target;
    if (target === 0) {
      el.muted = true;
      setIsMuted(true);
    } else if (el.muted) {
      el.muted = false;
      setIsMuted(false);
    }
    setVolume(target);
    localStorage.setItem('segra-volume', target.toString());
    localStorage.setItem('segra-muted', el.muted.toString());
  };

  // Pointer handlers for panning the video when zoomed
  const onVideoPointerDown = (e: React.PointerEvent) => {
    if (videoScaleRef.current <= 1) return;
    (e.target as Element).setPointerCapture(e.pointerId);
    setIsPanning(true);
    // Reset pan-moved flag for this gesture
    panMovedRef.current = false;
    videoPanStartRef.current = { x: e.clientX - videoTranslate.x, y: e.clientY - videoTranslate.y };
    videoLastPointerRef.current = e.pointerId;
  };

  const onVideoPointerMove = (e: React.PointerEvent) => {
    if (!isPanning || !videoPanStartRef.current) return;
    const start = videoPanStartRef.current;
    const dx = e.clientX - start.x;
    const dy = e.clientY - start.y;
    // If movement exceeds a small threshold, mark this gesture as a pan so we can suppress click
    if (Math.hypot(dx, dy) > 4) panMovedRef.current = true;
    setVideoTranslate((_prev) => clampTranslate({ x: dx, y: dy }));
  };

  const onVideoPointerUp = (e: React.PointerEvent) => {
    try {
      (e.target as Element).releasePointerCapture?.(e.pointerId);
    } catch (err) {
      // ignore pointer release errors
      // console.debug('pointer release error', err);
    }
    setIsPanning(false);
    videoPanStartRef.current = null;
    videoLastPointerRef.current = null;
  };

  // Click handler for the video element which suppresses clicks that are actually pans
  const onVideoClick = (e: React.MouseEvent) => {
    if (panMovedRef.current) {
      // This click is the end of a pan gesture — ignore it and reset the flag
      panMovedRef.current = false;
      e.stopPropagation();
      return;
    }
    togglePlayPause();
  };

  // Toggle video play/pause state
  const togglePlayPause = () => {
    handlePlayPause();
  };

  // Fullscreen controls: request browser fullscreen and ask backend for OS-level fullscreen
  const enterFullscreen = () => {
    setIsFullscreen(true);
    sendMessageToBackend('ToggleFullscreen', { enabled: true });
  };

  const exitFullscreen = () => {
    setIsFullscreen(false);
    sendMessageToBackend('ToggleFullscreen', { enabled: false });
  };

  const toggleFullscreen = () => {
    if (isFullscreen) exitFullscreen();
    else enterFullscreen();
  };

  // Prevent page scrollbars while our overlay is active
  useEffect(() => {
    const el = document.documentElement;
    const body = document.body;
    if (isFullscreen) {
      el.style.overflow = 'hidden';
      body.style.overflow = 'hidden';
    } else {
      el.style.overflow = '';
      body.style.overflow = '';
    }
    setVideoTranslate({ x: 0, y: 0 });
    applyVideoScale(1);
    return () => {
      el.style.overflow = '';
      body.style.overflow = '';
    };
  }, [isFullscreen]);

  // Handle clicks on the timeline to seek video
  const handleTimelineClick = (e: React.MouseEvent<HTMLDivElement>) => {
    if (isInteracting || !scrollContainerRef.current) return;
    const rect = scrollContainerRef.current.getBoundingClientRect();
    const clickPos = e.clientX - rect.left + scrollContainerRef.current.scrollLeft;
    const newTime = clickPos / pixelsPerSecond;
    const clampedTime = Math.max(0, Math.min(newTime, duration));
    setCurrentTime(clampedTime);
    if (videoRef.current) {
      videoRef.current.currentTime = clampedTime;
    }
  };

  // Handle timeline marker drag interactions
  const handleMarkerDragStart = (e: React.MouseEvent<HTMLDivElement>) => {
    e.stopPropagation();
    setIsDragging(true);
    setIsInteracting(true);
  };

  const handleMarkerDrag = (e: React.MouseEvent<HTMLDivElement>) => {
    if (!isDragging || !scrollContainerRef.current) return;
    const rect = scrollContainerRef.current.getBoundingClientRect();
    const dragPos = e.clientX - rect.left + scrollContainerRef.current.scrollLeft;
    const newTime = dragPos / pixelsPerSecond;
    setCurrentTime(Math.max(0, Math.min(newTime, duration)));
    if (videoRef.current) {
      videoRef.current.currentTime = newTime;
    }
  };

  const handleMarkerDragEnd = () => {
    setIsDragging(false);
    setTimeout(() => setIsInteracting(false), 0);
  };

  // Format time in seconds to "HH:MM:SS" when needed, otherwise "MM:SS"
  const formatTime = (time: number) => {
    const totalSeconds = Math.max(0, Math.floor(time));
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;
    if (hours > 0) {
      return `${hours.toString()}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
    }
    return `${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
  };

  // Generate major and minor tick marks for the timeline based on zoom level
  const generateTicks = () => {
    const maxTicks = 10;
    const minTickSpacing = 50;
    const totalPixels = duration * pixelsPerSecond;
    let majorTickInterval = Math.ceil(duration / maxTicks);
    let approxTickSpacing = totalPixels / (duration / majorTickInterval);
    while (approxTickSpacing < minTickSpacing) {
      majorTickInterval *= 2;
      approxTickSpacing = totalPixels / (duration / majorTickInterval);
    }
    const majorTicks: number[] = [];
    for (let t = majorTickInterval; t < duration; t += majorTickInterval) {
      majorTicks.push(t);
    }
    const minorTicks: number[] = [];
    const minorTicksPerMajor = 9;
    const minorInterval = majorTickInterval / minorTicksPerMajor;
    for (let t = minorInterval; t < duration; t += minorInterval) {
      if (Math.abs(t % majorTickInterval) < 0.0001) continue;
      minorTicks.push(t);
    }
    return { majorTicks, minorTicks };
  };

  const { majorTicks, minorTicks } = generateTicks();

  // Add a new selection at the current video position
  const handleAddSelection = async () => {
    if (!videoRef.current) return;
    const start = currentTime;
    const zoomRatio = (zoom / 50) * 100;
    // Cap the default selection duration at 2 minutes (120s)
    const selectionDuration = Math.min(120, Math.max(0.1, duration * 0.0019 * (100 / zoomRatio)));
    const end = currentTime + selectionDuration;

    const newSelection: Selection = {
      id: Date.now(),
      type: video.type,
      startTime: start,
      endTime: end,
      thumbnailDataUrl: undefined,
      isLoading: true,
      fileName: video.fileName,
      game: video.game,
      title: video.title,
      igdbId: video.igdbId,
    };
    addSelection(newSelection);
    // Kick off thumbnail generation; uses latest state and guards against stale overwrites
    refreshSelectionThumbnail(newSelection);
  };

  // Create a clip from current selections
  const handleCreateClip = () => {
    if (selections.length === 0) {
      setShowNoSegmentsIndicator(true);
      setTimeout(() => setShowNoSegmentsIndicator(false), 2000);
      return;
    }

    const params = {
      Selections: selections.map((s) => ({
        id: s.id,
        type: s.type,
        fileName: s.fileName,
        game: s.game,
        title: s.title,
        startTime: s.startTime,
        endTime: s.endTime,
        igdbId: s.igdbId,
      })),
    };
    sendMessageToBackend('CreateClip', params);
  };

  // Handle selection drag and drop operations (drag start removed to allow segment click-through)

  const handleSelectionDrag = (e: React.MouseEvent<HTMLDivElement>) => {
    if (!scrollContainerRef.current) return;
    if ((e.buttons & 1) !== 1 && dragState.id == null) return;
    const rect = scrollContainerRef.current.getBoundingClientRect();
    const dragPos = e.clientX - rect.left + scrollContainerRef.current.scrollLeft;
    const cursorTime = dragPos / pixelsPerSecond;

    // If no active drag, see if we should start due to threshold
    if (dragState.id == null) {
      const cand = dragCandidateRef.current;
      if (!cand) return;
      const delta = Math.abs(e.clientX - cand.startClientX);
      if (delta <= 3) return; // not enough movement yet
      setDragState({ id: cand.id, offset: cand.offset });
      setIsInteracting(true);
    }

    const activeId = dragState.id ?? dragCandidateRef.current?.id;
    const activeOffset =
      dragState.id != null ? dragState.offset : (dragCandidateRef.current?.offset ?? 0);
    if (activeId == null) return;
    const sel = selections.find((s) => s.id === activeId);
    if (sel) {
      const segLength = sel.endTime - sel.startTime;
      let newStart = cursorTime - activeOffset;
      newStart = Math.max(0, Math.min(newStart, duration - segLength));
      const updatedSelection = { ...sel, startTime: newStart, endTime: newStart + segLength };
      updateSelection(updatedSelection);
      latestDraggedSelectionRef.current = updatedSelection;
    }
  };

  const handleSelectionDragEnd = () => {
    const draggedId = dragState.id;
    setDragState({ id: null, offset: 0 });
    dragCandidateRef.current = null;
    setTimeout(() => setIsInteracting(false), 0);
    if (draggedId != null && latestDraggedSelectionRef.current) {
      const sel = latestDraggedSelectionRef.current;
      latestDraggedSelectionRef.current = null;
      void refreshSelectionThumbnail(sel);
    }
  };

  // Handle global mouse up events for drag operations
  useEffect(() => {
    const handleGlobalMouseUp = () => {
      handleMarkerDragEnd();
      if (dragState.id !== null) {
        handleSelectionDragEnd();
      }
      if (resizingSelectionId !== null) {
        handleSelectionResizeEnd();
      }
      dragCandidateRef.current = null;
      resizeCandidateRef.current = null;
    };
    window.addEventListener('mouseup', handleGlobalMouseUp);
    return () => {
      window.removeEventListener('mouseup', handleGlobalMouseUp);
    };
  }, [dragState.id, resizingSelectionId]);

  // Start a potential drag on mousedown without blocking click-through
  const handleSelectionMouseDown = (e: React.MouseEvent<HTMLDivElement>, id: number) => {
    if (!scrollContainerRef.current) return;
    const rect = scrollContainerRef.current.getBoundingClientRect();
    const dragPos = e.clientX - rect.left + scrollContainerRef.current.scrollLeft;
    const cursorTime = dragPos / pixelsPerSecond;
    const sel = selections.find((s) => s.id === id);
    if (sel) {
      dragCandidateRef.current = {
        id,
        startClientX: e.clientX,
        offset: cursorTime - sel.startTime,
      };
    }
  };

  useEffect(() => {
    if (!settings.showAudioWaveformInTimeline) return;

    const timelineContainer = document.getElementsByClassName(
      'timeline-container',
    )[0] as HTMLElement;
    if (!timelineContainer) return;

    let wavesurfer: ReturnType<typeof WaveSurfer.create> | null = null;
    const style = document.createElement('style');
    style.textContent = `
          .timeline-container ::part(wrapper),
          .timeline-container ::part(scroll),
          .timeline-container ::part(canvases),
          .timeline-container ::part(progress),
          .timeline-container ::part(cursor) {
            pointer-events: none !important;
          }
          .timeline-container ::part(canvases) {
            opacity: 0;
            transition: opacity 1000ms ease-in;
          }
          .timeline-container.waveform-ready ::part(canvases) {
            opacity: 0.6;
          }
        `;
    document.head.appendChild(style);

    // Fetch the peaks data and then initialize WaveSurfer
    const peaksUrl = getWaveformPath();
    fetch(peaksUrl)
      .then((response) => response.json())
      .then((peaksData) => {
        let durationFromPeaks: number | undefined = undefined;
        const data: number[] = Array.isArray(peaksData?.data) ? peaksData.data : [];
        const sr = Number(peaksData?.sample_rate) || 0;
        const spp = Number(peaksData?.samples_per_pixel) || 0;
        if (sr > 0 && spp > 0 && data.length > 1) {
          const columns = Math.floor(data.length / 2); // min/max pairs for mono
          durationFromPeaks = (columns * spp) / sr;
        }

        wavesurfer = WaveSurfer.create({
          container: timelineContainer,
          waveColor: '#49515b',
          progressColor: '#49515b',
          cursorColor: 'transparent',
          peaks: peaksData.data,
          duration: durationFromPeaks,
          height: 49,
          interact: false,
          barHeight: 1,
          barAlign: 'bottom',
          barRadius: 2,
        });

        wavesurfer.on('error', (err: Error) => {
          console.error('[Waveform] WaveSurfer error:', err);
        });
      })
      .catch((error: Error) => {
        console.error('Error loading audio peaks:', error);
      });

    return () => {
      if (wavesurfer) {
        wavesurfer.destroy();
      }
      if (document.head.contains(style)) {
        document.head.removeChild(style);
      }
    };
  }, [settings.showAudioWaveformInTimeline]);

  // Prepare to resize on drag (click-through on simple click)
  const handleResizeMouseDown = (
    e: React.MouseEvent<HTMLDivElement>,
    id: number,
    direction: 'start' | 'end',
  ) => {
    // Do not stop propagation so timeline click can still happen
    resizeCandidateRef.current = { id, direction, startClientX: e.clientX };
  };

  const handleSelectionResize = (e: React.MouseEvent<HTMLDivElement>) => {
    if (!scrollContainerRef.current) return;
    if ((e.buttons & 1) !== 1 && resizingSelectionId == null) return;
    const rect = scrollContainerRef.current.getBoundingClientRect();
    const pos = e.clientX - rect.left + scrollContainerRef.current.scrollLeft;
    const t = pos / pixelsPerSecond;
    // If no active resize yet, check if we should start (threshold)
    if (resizingSelectionId == null || !resizeDirection) {
      const cand = resizeCandidateRef.current;
      if (!cand) return;
      const delta = Math.abs(e.clientX - cand.startClientX);
      if (delta <= 3) return; // not enough movement
      setResizingSelectionId(cand.id);
      setResizeDirection(cand.direction);
      setIsInteracting(true);
    }

    const activeId = resizingSelectionId ?? resizeCandidateRef.current?.id ?? null;
    const activeDir = resizeDirection ?? resizeCandidateRef.current?.direction ?? null;
    if (activeId == null || !activeDir) return;
    const sel = selections.find((s) => s.id === activeId);
    if (!sel) return;

    let updatedSelection;
    if (activeDir === 'start') {
      const newStart = Math.max(0, Math.min(t, sel.endTime - 0.1));
      updatedSelection = { ...sel, startTime: newStart };
    } else {
      const newEnd = Math.min(duration, Math.max(t, sel.startTime + 0.1));
      updatedSelection = { ...sel, endTime: newEnd };
    }
    latestDraggedSelectionRef.current = updatedSelection;
    updateSelection(updatedSelection);

    // While resizing, keep the video time at the active edge and update marker state
    const edgeTime = activeDir === 'start' ? updatedSelection.startTime : updatedSelection.endTime;
    if (videoRef.current) {
      const clamped = Math.max(0, Math.min(edgeTime, duration));
      videoRef.current.currentTime = clamped;
    }
    setCurrentTime(edgeTime);
  };

  const handleSelectionResizeEnd = () => {
    setResizingSelectionId(null);
    setResizeDirection(null);
    resizeCandidateRef.current = null;
    setIsInteracting(false);
    if (latestDraggedSelectionRef.current) {
      const sel = latestDraggedSelectionRef.current;
      latestDraggedSelectionRef.current = null;
      void refreshSelectionThumbnail(sel);
    }
  };

  // Right-click to remove selection disabled to keep segments click-through

  // Move selection card in the sidebar
  const moveCard = (dragIndex: number, hoverIndex: number) => {
    const newSelections = [...selections];
    const [removed] = newSelections.splice(dragIndex, 1);
    newSelections.splice(hoverIndex, 0, removed);
    updateSelectionsArray(newSelections);
  };

  // Get video source URL
  const getVideoPath = (): string => {
    const contentFileName = `${contentFolder}/${video.type.toLowerCase()}s/${video.fileName}.mp4`;
    return `http://localhost:2222/api/content?input=${encodeURIComponent(contentFileName)}&type=${video.type.toLowerCase()}`;
  };

  // Get audio source URL
  const getWaveformPath = (): string => {
    const contentFileName = `${contentFolder}/.waveforms/${video.type.toLowerCase()}s/${video.fileName}.peaks.json`;
    return `http://localhost:2222/api/content?input=${encodeURIComponent(contentFileName)}&type=${video.type.toLowerCase()}`;
  };

  // Handle video upload operation
  const handleUpload = () => {
    // Ensure video is paused before opening upload modal
    if (videoRef.current && !videoRef.current.paused) {
      videoRef.current.pause();
    }

    openModal(
      <UploadModal
        key={`${Math.random()}`}
        video={video}
        onClose={closeModal}
        onUpload={(title, visibility) => {
          const parameters = {
            FilePath: video.filePath,
            JWT: session?.access_token,
            Game: video.game,
            Title: title,
            Description: '', // TODO: implement description
            Visibility: visibility, // TODO: implement description
            IgdbId: video.igdbId?.toString(),
          };

          sendMessageToBackend('UploadContent', parameters);
        }}
      />,
    );
  };

  const [selectedBookmarkTypes, setSelectedBookmarkTypes] = useState<Set<BookmarkType>>(
    new Set(Object.values(BookmarkType)),
  );

  const availableBookmarkTypes = useMemo(() => {
    const order = [BookmarkType.Kill, BookmarkType.Goal, BookmarkType.Assist, BookmarkType.Death, BookmarkType.Manual];
    return order.filter((type) => video.bookmarks.some((b) => b.type === type));
  }, [video.bookmarks]);

  const filteredBookmarks = useMemo(() => {
    return video.bookmarks.filter((bookmark) => selectedBookmarkTypes.has(bookmark.type));
  }, [video.bookmarks, selectedBookmarkTypes]);

  const toggleBookmarkType = (type: BookmarkType) => {
    setSelectedBookmarkTypes((prev) => {
      const newSet = new Set(prev);
      if (newSet.has(type)) {
        newSet.delete(type);
      } else {
        newSet.add(type);
      }
      return newSet;
    });
  };

  const handleAddBookmark = () => {
    if (!videoRef.current) return;

    const currentTimeInSeconds = videoRef.current.currentTime;
    // Format time as HH:MM:SS.mmm for consistency with backend
    const hours = Math.floor(currentTimeInSeconds / 3600);
    const minutes = Math.floor((currentTimeInSeconds % 3600) / 60);
    const seconds = Math.floor(currentTimeInSeconds % 60);
    const milliseconds = Math.floor((currentTimeInSeconds % 1) * 1000);

    const formattedTime = `${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}.${milliseconds.toString().padStart(3, '0')}`;

    // Default to Manual bookmark type if not specified
    const bookmarkType = BookmarkType.Manual;

    // Generate a random ID between 1 and MAX_INT
    const bookmarkId = Math.floor(Math.random() * 2147483647) + 1;

    // Create a new bookmark object
    const newBookmark: Bookmark = {
      id: bookmarkId,
      type: bookmarkType,
      time: formattedTime,
    };

    // Add the bookmark to the video's bookmarks array
    video.bookmarks.push(newBookmark);

    // Force a re-render to show the new bookmark
    const bookmarks = [...video.bookmarks];
    video.bookmarks = bookmarks;

    // Send message to backend to add bookmark
    sendMessageToBackend('AddBookmark', {
      FilePath: video.filePath,
      Type: bookmarkType,
      Time: formattedTime,
      ContentType: video.type,
      Id: bookmarkId,
    });
  };

  const handleDeleteBookmark = (bookmarkId: number) => {
    // Find the bookmark in the video's bookmarks array
    const bookmarkIndex = video.bookmarks.findIndex((b) => b.id === bookmarkId);

    if (bookmarkIndex !== -1) {
      // Remove the bookmark from the array
      video.bookmarks.splice(bookmarkIndex, 1);

      // Force a re-render to update the UI
      const bookmarks = [...video.bookmarks];
      video.bookmarks = bookmarks;

      // Send message to backend to delete the bookmark
      sendMessageToBackend('DeleteBookmark', {
        FilePath: video.filePath,
        ContentType: video.type,
        Id: bookmarkId,
      });
    }
  };

  // Handle volume change
  const handleVolumeChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const newVolume = parseFloat(e.target.value);
    setPlayerVolume(newVolume);
  };

  // Toggle mute state
  const toggleMute = () => {
    if (videoRef.current) {
      const newMutedState = !videoRef.current.muted;
      videoRef.current.muted = newMutedState;
      setIsMuted(newMutedState);

      // Save to localStorage
      localStorage.setItem('segra-muted', newMutedState.toString());
    }
  };

  const setPlaybackRateForPlayer = (rate: number) => {
    const r = Math.max(0.25, Math.min(2, rate));
    if (videoRef.current) videoRef.current.playbackRate = r;
    setPlaybackRate(r);
    localStorage.setItem('segra-playbackRate', r.toString());
  };

  return (
    <DndProvider backend={HTML5Backend}>
      <div className="flex w-full h-full overflow-hidden bg-base-200" ref={containerRef}>
        <div className="flex-1 w-full p-4 lg:w-3/4">
          <TopInfoBar video={video} />
          <div
            className={`${isFullscreen ? 'fixed inset-0 z-50 w-screen h-screen overflow-hidden bg-black' : 'relative'} ${!controlsVisible && isPointerInPlayer ? 'cursor-none' : ''}`}
            ref={playerContainerRef}
            onMouseMove={() => {
              setIsPointerInPlayer(true);
              showControlsTemporarily();
            }}
            onMouseLeave={() => {
              setIsPointerInPlayer(false);
              isPointerOverControlsRef.current = false;
              if (controlsHideTimeoutRef.current) {
                clearTimeout(controlsHideTimeoutRef.current);
                controlsHideTimeoutRef.current = null;
              }
              setControlsVisible(false);
            }}
          >
            <div className={videoWrapperClassName}>
              <video
                autoPlay
                className="w-full h-full"
                src={getVideoPath()}
                ref={videoRef}
                onClick={onVideoClick}
                onDoubleClick={toggleFullscreen}
                onPointerDown={onVideoPointerDown}
                onPointerMove={onVideoPointerMove}
                onPointerUp={onVideoPointerUp}
                onWheel={onVideoWheel}
                style={{
                  backgroundColor: 'black',
                  objectFit: isFullscreen ? ('contain' as const) : undefined,
                  transform: `translate(${videoTranslate.x}px, ${videoTranslate.y}px) scale(${videoScale})`,
                  transformOrigin: '0 0',
                  touchAction: videoScale > 1 ? 'none' : undefined,
                  cursor: videoScale > 1 && isPanning ? 'grabbing' : undefined,
                }}
              />
            </div>

            <div
              className={`absolute left-4 right-4 bottom-4 bg-black/70 rounded-lg px-3 py-2 flex items-center gap-3 transition-opacity duration-300 select-none ${controlsVisible ? 'opacity-100' : 'opacity-0'}`}
              onMouseEnter={handleControlsMouseEnter}
              onMouseLeave={handleControlsMouseLeave}
            >
              <button
                onClick={togglePlayPause}
                className="text-white transition-colors cursor-pointer hover:text-accent"
                aria-label={isPlaying ? 'Pause' : 'Play'}
              >
                {isPlaying ? <MdPause className="w-6 h-6" /> : <MdPlayArrow className="w-6 h-6" />}
              </button>

              <span className="w-12 text-xs text-right tabular-nums text-white/90">
                {formatTime(currentTime)}
              </span>

              <input
                type="range"
                min={0}
                max={Math.max(0.01, duration)}
                step={0.01}
                value={Math.min(currentTime, duration)}
                onChange={(e) => {
                  const t = parseFloat(e.target.value);
                  setCurrentTime(t);
                  if (videoRef.current) videoRef.current.currentTime = t;
                }}
                onPointerUp={(e) => (e.currentTarget as HTMLInputElement).blur()}
                onMouseUp={(e) => (e.currentTarget as HTMLInputElement).blur()}
                onTouchEnd={(e) => (e.currentTarget as HTMLInputElement).blur()}
                className="flex-1 h-1 bg-gray-600 rounded-lg appearance-none cursor-pointer accent-accent"
              />

              <span className="w-12 text-xs tabular-nums text-white/90">
                {formatTime(duration)}
              </span>

              <div className="flex items-center gap-2 ml-2">
                <button
                  onClick={toggleMute}
                  className="text-white transition-colors cursor-pointer hover:text-accent"
                  aria-label={isMuted ? 'Unmute' : 'Mute'}
                >
                  {isMuted || volume === 0 ? (
                    <MdVolumeOff className="w-6 h-6" />
                  ) : volume < 0.20 ? (
                    <MdVolumeMute className="w-6 h-6" />
                  ) : volume < 0.70 ? (
                    <MdVolumeDown className="w-6 h-6" />
                  ) : (
                    <MdVolumeUp className="w-6 h-6" />
                  )}
                </button>
                <input
                  type="range"
                  min="0"
                  max="1"
                  step="0.02"
                  value={volume}
                  onChange={handleVolumeChange}
                  onPointerUp={(e) => (e.currentTarget as HTMLInputElement).blur()}
                  onMouseUp={(e) => (e.currentTarget as HTMLInputElement).blur()}
                  onTouchEnd={(e) => (e.currentTarget as HTMLInputElement).blur()}
                  className="w-24 h-1 bg-gray-600 rounded-lg appearance-none cursor-pointer accent-accent"
                  aria-label="Volume"
                />
              </div>

              <div className="flex items-center gap-2 ml-3">
                <button
                  onClick={() => {
                    const current = videoScaleRef.current || 1;
                    applyVideoScale(current - 0.5);
                  }}
                  disabled={videoScale <= 1}
                  className="flex items-center justify-center p-1 text-white cursor-pointer transition-colors border rounded-md border-base-400 hover:text-accent hover:bg-accent/20 disabled:opacity-50 disabled:cursor-not-allowed"
                  aria-label="Zoom out"
                >
                  <TbZoomOut className="w-4 h-4" />
                </button>
                <button
                  onClick={() => {
                    const current = videoScaleRef.current || 1;
                    applyVideoScale(current + 0.5);
                  }}
                  disabled={videoScale >= 4}
                  className="flex items-center justify-center p-1 text-white cursor-pointer transition-colors border rounded-md border-base-400 hover:text-accent hover:bg-accent/20 disabled:opacity-50 disabled:cursor-not-allowed"
                  aria-label="Zoom in"
                >
                  <TbZoomIn className="w-4 h-4" />
                </button>
                <div className="relative" ref={speedDropdownRef}>
                  <button
                    ref={speedButtonRef}
                    type="button"
                    className="flex items-center justify-center gap-1 px-2 py-1 text-xs font-medium text-white cursor-pointer transition-colors border rounded-md border-base-400 hover:text-accent hover:bg-accent/20"
                    aria-label="Change playback speed"
                    aria-haspopup="menu"
                    aria-expanded={showSpeedMenu}
                    onClick={() => {
                      if (showSpeedMenu) {
                        setShowSpeedMenu(false);
                        speedButtonRef.current?.blur();
                      } else {
                        setShowSpeedMenu(true);
                      }
                    }}
                  >
                    <span>{formatPlaybackRateLabel(playbackRate)}</span>
                  </button>
                  {showSpeedMenu && (
                    <div className="absolute right-0 bottom-full z-50 mb-2 border rounded-md shadow-lg bg-black/70 border-base-400">
                      <div className="flex flex-col">
                        {PLAYBACK_SPEEDS.map((speed) => {
                          const isActive = speed === playbackRate;
                          return (
                            <button
                              key={speed}
                              role="menuitemradio"
                              aria-checked={isActive}
                              onClick={() => {
                                setPlaybackRateForPlayer(speed);
                                setShowSpeedMenu(false);
                                speedButtonRef.current?.blur();
                              }}
                              className={`flex w-full items-center justify-center px-3 py-1 cursor-pointer text-sm transition-colors ${isActive ? 'text-white bg-accent/20' : 'text-white/80 hover:text-white hover:bg-accent/10'}`}
                            >
                              <span>{formatPlaybackRateLabel(speed)}</span>
                            </button>
                          );
                        })}
                      </div>
                    </div>
                  )}
                </div>
              </div>

              <button
                onClick={toggleFullscreen}
                onPointerUp={(e) => (e.currentTarget as HTMLInputElement).blur()}
                onMouseUp={(e) => (e.currentTarget as HTMLInputElement).blur()}
                onTouchEnd={(e) => (e.currentTarget as HTMLInputElement).blur()}
                className="ml-2 text-white cursor-pointer transition-colors hover:text-accent"
                aria-label={isFullscreen ? 'Exit Fullscreen' : 'Enter Fullscreen'}
              >
                {isFullscreen ? (
                  <MdFullscreenExit className="w-6 h-6" />
                ) : (
                  <MdFullscreen className="w-6 h-6" />
                )}
              </button>
            </div>
          </div>
          <div
            className="relative w-full mt-2 overflow-x-scroll overflow-y-hidden select-none timeline-wrapper"
            ref={scrollContainerRef}
            onMouseMove={(e) => {
              handleSelectionDrag(e);
              handleSelectionResize(e);
              handleMarkerDrag(e);
            }}
          >
            <div
              className="ticks-container relative h-[42px]"
              style={{
                width: `${duration * pixelsPerSecond}px`,
                minWidth: '100%',
                overflow: 'hidden',
              }}
            >
              <AnimatePresence>
                {bookmarksReady &&
                  filteredBookmarks.map((bookmark, index) => {
                    const timeInSeconds = timeStringToSeconds(bookmark.time);
                    const leftPos = timeInSeconds * pixelsPerSecond;
                    const Icon = iconMapping[bookmark.type as BookmarkType] || IoSkull;

                    return (
                      <motion.div
                        key={`bookmark-${bookmark.id ?? index}`}
                        initial={{ opacity: 0, scale: 0.5 }}
                        animate={{ opacity: 1, scale: 1 }}
                        exit={{ opacity: 0, scale: 0.5 }}
                        transition={{ duration: 0.1 }}
                        className="tooltip absolute bottom-0 transform -translate-x-1/2 cursor-pointer z-10 flex flex-col items-center text-[#25272e]"
                        data-tip={`${bookmark.type}${bookmark.subtype ? ` - ${bookmark.subtype}` : ''} (${bookmark.time})`}
                        style={{ left: `${leftPos}px` }}
                        onClick={() => {
                          const seekTo = Math.max(
                            0,
                            timeInSeconds - (bookmark.type == BookmarkType.Manual ? 10 : 5),
                          );
                          setCurrentTime(seekTo);
                          if (videoRef.current) {
                            videoRef.current.currentTime = seekTo;
                          }
                        }}
                        onContextMenu={(e) => {
                          e.preventDefault();
                          handleDeleteBookmark(bookmark.id);
                        }}
                      >
                        <div className="bg-[#EFAF2B] w-[26px] h-[26px] rounded-full flex items-center justify-center mb-0">
                          {bookmark.type === 'Goal' || bookmark.type === 'Assist' ? <Icon size={20} /> : <Icon size={16} />}
                        </div>
                        <div className="w-[2px] h-[16px] bg-[#EFAF2B]" />
                      </motion.div>
                    );
                  })}
              </AnimatePresence>
              {minorTicks.map((tickTime, index) => {
                if (tickTime >= duration) return null;
                const leftPos = tickTime * pixelsPerSecond;
                return (
                  <div
                    key={`minor-${index}`}
                    className="absolute bottom-0 h-[6px] border-l border-white/20"
                    style={{
                      left: `${leftPos}px`,
                    }}
                  />
                );
              })}
              {majorTicks.map((tickTime, index) => {
                if (tickTime > duration) return null;
                const leftPos = tickTime * pixelsPerSecond;
                return (
                  <div
                    key={`major-${index}`}
                    className="absolute bottom-0 text-center text-white -translate-x-1/2 select-none whitespace-nowrap"
                    style={{
                      left: `${leftPos}px`,
                    }}
                  >
                    <span className="absolute bottom-full left-1/2 -translate-x-1/2 text-xs mb-[3px]">
                      {formatTime(tickTime)}
                    </span>
                    <div className="w-[2px] h-[10px] bg-white mx-auto" />
                  </div>
                );
              })}
            </div>
            <div
              className="timeline-container bg-base-300 border border-base-400 rounded-lg relative h-[50px] w-full overflow-hidden waveform-ready"
              style={{
                width: `${duration * pixelsPerSecond}px`,
                minWidth: '100%',
              }}
              onClick={handleTimelineClick}
            >
              {sortedSelections.map((sel) => {
                const left = sel.startTime * pixelsPerSecond;
                const width = (sel.endTime - sel.startTime) * pixelsPerSecond;
                const hidden = sel.fileName !== video.fileName;
                return (
                  <>
                    <div
                      key={sel.id}
                      className={`absolute top-0 left-0 h-full cursor-move ${hidden ? 'hidden' : ''} transition-colors overflow-hidden rounded-r-sm rounded-l-sm shadow-md
                                                bg-primary/40 border border-primary/40`}
                      style={{ left: `${left}px`, width: `${width}px` }}
                      onMouseEnter={() => {
                        setHoveredSelectionId(sel.id);
                      }}
                      onMouseLeave={() => {
                        setHoveredSelectionId(null);
                      }}
                      onMouseDown={(e) => handleSelectionMouseDown(e, sel.id)}
                      onContextMenu={(e) => {
                        e.preventDefault();
                        removeSelection(sel.id);
                      }}
                    >
                      <div className="absolute left-0 top-0 h-full w-[4px] bg-accent/80 rounded-l-sm pointer-events-none" />
                      <div className="absolute right-0 top-0 h-full w-[4px] bg-accent/80 rounded-r-sm pointer-events-none" />

                      <div
                        className="absolute top-0 -left-[8px] w-[18px] h-full bg-transparent cursor-col-resize pointer-events-auto"
                        onMouseDown={(e) => handleResizeMouseDown(e, sel.id, 'start')}
                        aria-label="Resize segment start"
                      />
                      <div
                        className="absolute top-0 -right-[8px] w-[18px] h-full bg-transparent cursor-col-resize pointer-events-auto"
                        onMouseDown={(e) => handleResizeMouseDown(e, sel.id, 'end')}
                        aria-label="Resize segment end"
                      />
                    </div>
                  </>
                );
              })}
              {resizingSelectionId == null && (
                <div
                  className="absolute top-0 left-0 z-10 w-1 h-full -translate-x-1/2 rounded-sm shadow cursor-pointer marker bg-accent"
                  style={{ left: `${currentTime * pixelsPerSecond}px` }}
                  onMouseDown={handleMarkerDragStart}
                />
              )}
            </div>
          </div>
          <div className="flex items-center justify-between gap-4 py-1">
            <div className="flex items-center gap-3">
              <div className="flex items-center border rounded-lg join bg-base-300 border-custom">
                <button
                  onClick={() => skipTime(-10)}
                  className="h-10 text-gray-400 btn btn-sm btn-secondary hover:text-accent join-item"
                >
                  <MdReplay10 className="w-6 h-6" />
                </button>
                <button
                  onClick={handlePlayPause}
                  className="h-10 text-gray-400 btn btn-sm btn-secondary hover:text-accent join-item"
                  data-tip={isPlaying ? 'Pause' : 'Play'}
                >
                  {isPlaying ? (
                    <MdPause className="w-6 h-6" />
                  ) : (
                    <MdPlayArrow className="w-6 h-6" />
                  )}
                </button>
                <button
                  onClick={() => skipTime(10)}
                  className="h-10 text-gray-400 btn btn-sm btn-secondary hover:text-accent join-item"
                  data-tip="Forward 10s"
                >
                  <MdForward10 className="w-6 h-6" />
                </button>
              </div>
              {(video.type === 'Clip' || video.type === 'Highlight') && (
                <button
                  className="flex items-center h-10 text-gray-400 btn btn-sm btn-secondary border-custom hover:border-custom hover:text-accent"
                  onClick={handleUpload}
                  disabled={
                    uploads[video.fileName + '.mp4']?.status === 'uploading' ||
                    uploads[video.fileName + '.mp4']?.status === 'processing'
                  }
                >
                  <MdOutlineFileUpload className="w-6 h-6" />
                  <span>Upload</span>
                </button>
              )}
              {(video.type === 'Session' || video.type === 'Buffer') && (
                <>
                  <button
                    className={`btn btn-sm btn-secondary border-custom hover:border-custom h-10 text-gray-400 hover:text-accent flex items-center gap-1`}
                    onClick={handleCreateClip}
                  >
                    <MdMovieCreation className="w-6 h-6" />
                    <span>Create Clip</span>
                  </button>
                  <div className="indicator">
                    <button
                      className={`btn btn-sm btn-secondary border-custom hover:border-custom h-10 text-gray-400 hover:text-accent flex items-center gap-1`}
                      onClick={handleAddSelection}
                    >
                      {showNoSegmentsIndicator && (
                        <span className="indicator-item badge badge-sm badge-primary animate-pulse"></span>
                      )}
                      <MdAddBox className="w-6 h-6" />
                      <span>Add Segment</span>
                    </button>
                  </div>
                </>
              )}
            </div>

            <div className="flex items-center gap-3">
              {(video.type === 'Session' || video.type === 'Buffer') && (
                <>
                  {availableBookmarkTypes.length > 0 && (
                    <div className="flex items-center h-10 gap-0 px-0 border rounded-lg bg-base-300 join border-custom">
                      {availableBookmarkTypes.map((type) => (
                        <button
                          key={type}
                          onClick={() => toggleBookmarkType(type)}
                          className={`btn btn-sm btn-secondary border-none transition-colors join-item ${selectedBookmarkTypes.has(type) ? 'text-accent' : 'text-gray-400'}`}
                        >
                          {React.createElement(iconMapping[type] || IoSkull, {
                            className: 'w-6 h-6',
                          })}
                        </button>
                      ))}
                    </div>
                  )}
                  <div className="flex items-center gap-2 rounded-lg bg-base-300">
                    <button
                      onClick={handleAddBookmark}
                      className="h-10 text-gray-400 btn btn-sm btn-secondary border-custom hover:border-custom hover:text-accent"
                    >
                      <MdBookmarkAdd className="w-6 h-6" />
                    </button>
                  </div>
                </>
              )}

              <div className="flex items-center h-10 gap-1 px-0 border rounded-lg bg-base-300 border-custom">
                <button
                  onClick={() => handleZoomChange(false)}
                  className="btn btn-sm btn-secondary disabled:opacity-100 disabled:bg-base-300"
                  disabled={zoom <= 1}
                >
                  <IoRemove className="w-4 h-4" />
                </button>
                <span className="text-sm font-medium text-center text-gray-300">
                  {Math.round((zoom * 100) / 5) * 5}%
                </span>
                <button
                  onClick={() => handleZoomChange(true)}
                  className="btn btn-sm btn-secondary"
                  disabled={zoom >= 50}
                >
                  <IoAdd className="w-4 h-4" />
                </button>
              </div>
            </div>
          </div>
        </div>
        {(video.type === 'Session' || video.type === 'Buffer') && (
          <div className="flex flex-col h-full pt-4 pl-4 pr-1 border-l bg-base-300 text-neutral-content w-52 2xl:w-72 border-custom">
            <div className="flex-1 p-1 mt-1 overflow-y-scroll">
              {selections.map((sel, index) => (
                <SelectionCard
                  key={sel.id}
                  selection={sel}
                  index={index}
                  moveCard={moveCard}
                  formatTime={formatTime}
                  isHovered={hoveredSelectionId === sel.id}
                  setHoveredSelectionId={setHoveredSelectionId}
                  removeSelection={removeSelection}
                />
              ))}
            </div>
            <div className="flex items-center justify-between my-3 mr-3">
              <label className="flex items-center cursor-pointer">
                <input
                  type="checkbox"
                  name="clipClearSelectionsAfterCreatingClip"
                  checked={settings.clipClearSelectionsAfterCreatingClip}
                  onChange={(e) =>
                    updateSettings({ clipClearSelectionsAfterCreatingClip: e.target.checked })
                  }
                  className="checkbox checkbox-sm checkbox-accent"
                />
                <span className="ml-2 text-sm">Auto-Clear Selections</span>
              </label>
            </div>
            <div className="flex items-center h-10 gap-0 px-0 mb-2 mr-3 rounded-lg bg-base-300 tooltip">
              <button
                className="w-full h-10 py-0 text-gray-400 border btn btn-sm btn-secondary border-custom disabled:border-custom hover:border-custom hover:text-accent"
                onClick={clearAllSelections}
                disabled={selections.length === 0}
              >
                <FaTrashCan className="w-4 h-4" />
                <span>Clear</span>
              </button>
            </div>
          </div>
        )}
      </div>
    </DndProvider>
  );
}