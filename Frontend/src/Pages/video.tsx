import React, { useRef, useState, useEffect, useMemo } from "react";
import { Content, BookmarkType, Selection, Bookmark } from "../Models/types";
import { sendMessageToBackend } from "../Utils/MessageUtils";
import { useSettings, useSettingsUpdater } from "../Context/SettingsContext";
import { DndProvider } from "react-dnd";
import { HTML5Backend } from "react-dnd-html5-backend";
import { useAuth } from "../Hooks/useAuth.tsx";
import { useSelections } from "../Context/SelectionsContext";
import { useUploads } from "../Context/UploadContext";
import { useModal } from "../Context/ModalContext";
import UploadModal from '../Components/UploadModal';
import { IconType } from "react-icons";
import { FaGun, FaTrashCan } from "react-icons/fa6";
import { MdAddBox, MdBookmark, MdBookmarkAdd, MdMovieCreation, MdOutlineHandshake, MdPause, MdPlayArrow, MdReplay10, MdForward10, MdOutlineFileUpload, MdVolumeUp, MdVolumeOff, MdVolumeMute, MdVolumeDown, MdFullscreen, MdFullscreenExit } from "react-icons/md";
import { IoSkull, IoAdd, IoRemove } from "react-icons/io5";
import SelectionCard from '../Components/SelectionCard';
import WaveSurfer from 'wavesurfer.js'

// Converts time string in format "HH:MM:SS.mmm" to seconds
const timeStringToSeconds = (timeStr: string): number => {
    const [time, milliseconds] = timeStr.split('.');
    const [hours, minutes, seconds] = time.split(':').map(Number);
    return hours * 3600 + minutes * 60 + seconds + (milliseconds ? Number(`0.${milliseconds}`) : 0);
};

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
        clearAllSelections 
    } = useSelections();
    
    // Refs
    const videoRef = useRef<HTMLVideoElement>(null);
    const audioRef = useRef<HTMLAudioElement>(null);
    const scrollContainerRef = useRef<HTMLDivElement>(null);
    const containerRef = useRef<HTMLDivElement>(null);
    const playerContainerRef = useRef<HTMLDivElement>(null);
    const latestDraggedSelectionRef = useRef<Selection | null>(null);

    // Video state
    const [currentTime, setCurrentTime] = useState(0);
    const [duration, setDuration] = useState(0);
    const [zoom, setZoom] = useState(1);
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
    const [controlsVisible, setControlsVisible] = useState(true);
    const controlsHideTimeoutRef = useRef<number | null>(null);
    const [isPointerInPlayer, setIsPointerInPlayer] = useState(false);
    const [isFullscreen, setIsFullscreen] = useState(false);

    // Interaction state
    const [isDragging, setIsDragging] = useState(false);
    const [isInteracting, setIsInteracting] = useState(false);
    const [hoveredSelectionId, setHoveredSelectionId] = useState<number | null>(null);
    const [dragState, setDragState] = useState<{ id: number | null; offset: number }>({ 
        id: null, 
        offset: 0 
    });
    const dragCandidateRef = useRef<{ id: number; startClientX: number; offset: number } | null>(null);
    const [resizingSelectionId, setResizingSelectionId] = useState<number | null>(null);
    const [resizeDirection, setResizeDirection] = useState<"start" | "end" | null>(null);
    const resizeCandidateRef = useRef<{ id: number; direction: "start" | "end"; startClientX: number } | null>(null);

    // Computed values
    const basePixelsPerSecond = duration > 0 ? containerWidth / duration : 0;
    const pixelsPerSecond = basePixelsPerSecond * zoom;
    const sortedSelections = [...selections].sort((a, b) => a.startTime - b.startTime);
    const selectionsRef = useRef(selections);
    useEffect(() => { selectionsRef.current = selections; }, [selections]);

    // Icon mapping
    const iconMapping: Record<BookmarkType, IconType> = {
        Manual: MdBookmark,
        Kill: FaGun,
        Assist: MdOutlineHandshake,
        Death: IoSkull
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
        
        vid.addEventListener("loadedmetadata", onLoadedMetadata);
        vid.addEventListener("play", onPlay);
        vid.addEventListener("pause", onPause);
        vid.addEventListener("volumechange", onVolumeChange);
        
        const handleKeyDown = (e: KeyboardEvent) => {
            const target = e.target as HTMLElement;
            const isTyping = target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || (target as any).isContentEditable;

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
        window.addEventListener("keydown", handleKeyDown, keyOptions);

        // No DOM fullscreen; we manage an overlay + window maximize from backend
        
        return () => {
            vid.removeEventListener("loadedmetadata", onLoadedMetadata);
            vid.removeEventListener("play", onPlay);
            vid.removeEventListener("pause", onPause);
            vid.removeEventListener("volumechange", onVolumeChange);
            window.removeEventListener("keydown", handleKeyDown, keyOptions as any);
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
        vid.addEventListener("play", onPlay);
        vid.addEventListener("pause", onPause);
        if (!vid.paused) onPlay();
        return () => {
            vid.removeEventListener("play", onPlay);
            vid.removeEventListener("pause", onPause);
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
        window.addEventListener("resize", handleResize);
        return () => {
            window.removeEventListener("resize", handleResize);
        };
    }, []);

    // Create refs to track zoom state
    const wheelZoomRef = useRef(zoom);
    
    // Update the wheel zoom ref when zoom changes from other sources (buttons)
    useEffect(() => {
        wheelZoomRef.current = zoom;
    }, [zoom]);

    const showControlsTemporarily = () => {
        setControlsVisible(true);
        if (controlsHideTimeoutRef.current) {
            clearTimeout(controlsHideTimeoutRef.current);
        }
        controlsHideTimeoutRef.current = window.setTimeout(() => {
            setControlsVisible(false);
        }, 2500);
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
        container.addEventListener("wheel", handleWheel, wheelEventOptions);
        
        return () => {
            container.removeEventListener("wheel", handleWheel, wheelEventOptions);
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
                const newScrollPosition = newMarkerPosition - (visibleRatio * viewportWidth);
                
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

    // Toggle video play/pause state
    const togglePlayPause = () => {
        handlePlayPause();
    };

    // Fullscreen controls: request browser fullscreen and ask backend for OS-level fullscreen
    const enterFullscreen = () => {
        setIsFullscreen(true);
        sendMessageToBackend("ToggleFullscreen", { enabled: true });
    };

    const exitFullscreen = () => {
        setIsFullscreen(false);
        sendMessageToBackend("ToggleFullscreen", { enabled: false });
    };

    const toggleFullscreen = () => {
        if (isFullscreen) exitFullscreen(); else enterFullscreen();
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
        return {majorTicks, minorTicks};
    };

    const {majorTicks, minorTicks} = generateTicks();

    // Add a new selection at the current video position
    const handleAddSelection = async () => {
        if (!videoRef.current) return;
        const start = currentTime;
        const zoomRatio = zoom / 50 * 100;
        // Cap the default selection duration at 2 minutes (120s)
        const selectionDuration = Math.min(120, Math.max(0.1, (duration * 0.0019) * (100 / zoomRatio)));
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
                startTime: s.startTime,
                endTime: s.endTime,
            }))
        };
        sendMessageToBackend("CreateClip", params);
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
        const activeOffset = dragState.id != null ? dragState.offset : dragCandidateRef.current?.offset ?? 0;
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
        window.addEventListener("mouseup", handleGlobalMouseUp);
        return () => {
            window.removeEventListener("mouseup", handleGlobalMouseUp);
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
        if (!audioRef.current) return;
        
        const timelineContainer = document.getElementsByClassName('timeline-container')[0] as HTMLElement;
        if (!timelineContainer) return;
    
        const ws = WaveSurfer.create({
            container: timelineContainer,
            waveColor: '#49515b',
            progressColor: '#49515b',
            cursorColor: 'transparent',
            media: audioRef.current,
            height: 49,
            interact: false,
            barHeight: 1.75,
            barAlign: 'bottom',
            barRadius: 2,
        });

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
    
        ws.on('ready', () => {
            // Add a class to the timeline container to trigger the fade-in
            setTimeout(() => {
                timelineContainer.classList.add('waveform-ready');
            }, 10);
        });

        ws.on('error', (error) => {
            console.error('WaveSurfer error:', error);
        });
    
        return () => {
            ws.destroy();
            if (document.head.contains(style)) {
                document.head.removeChild(style);
            }
        };
    }, []);

    // Prepare to resize on drag (click-through on simple click)
    const handleResizeMouseDown = (
        e: React.MouseEvent<HTMLDivElement>,
        id: number,
        direction: "start" | "end"
    ) => {
        // Do not stop propagation so timeline click can still happen
        resizeCandidateRef.current = { id, direction, startClientX: e.clientX };
    };

    const handleSelectionResize = (e: React.MouseEvent<HTMLDivElement>) => {
        if (!scrollContainerRef.current) return;
        if ((e.buttons & 1) !== 1 && (resizingSelectionId == null)) return;
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
        if (activeDir === "start") {
            const newStart = Math.max(0, Math.min(t, sel.endTime - 0.1));
            updatedSelection = {...sel, startTime: newStart};
        } else {
            const newEnd = Math.min(duration, Math.max(t, sel.startTime + 0.1));
            updatedSelection = {...sel, endTime: newEnd};
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
    const getAudioPath = (): string => {
        const contentFileName = `${contentFolder}/.audio/${video.type.toLowerCase()}s/${video.fileName}.mp3`;
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
                video={video}
                onClose={closeModal}
                onUpload={(title, visibility) => {
                    const parameters = {
                        FilePath: video.filePath,
                        JWT: session?.access_token,
                        Game: video.game,
                        Title: title,
                        Description: "", // TODO: implement description
                        Visibility: visibility // TODO: implement description
                    };

                    sendMessageToBackend('UploadContent', parameters);
                }}
            />
        );
    };

    

    const [selectedBookmarkTypes, setSelectedBookmarkTypes] = useState<Set<BookmarkType>>(new Set(Object.values(BookmarkType)));

    const availableBookmarkTypes = useMemo(() => {
        const types = new Set<BookmarkType>();
        video.bookmarks.forEach(bookmark => types.add(bookmark.type));
        return Array.from(types);
    }, [video.bookmarks]);

    const filteredBookmarks = useMemo(() => {
        return video.bookmarks.filter(bookmark => selectedBookmarkTypes.has(bookmark.type));
    }, [video.bookmarks, selectedBookmarkTypes]);

    const toggleBookmarkType = (type: BookmarkType) => {
        setSelectedBookmarkTypes(prev => {
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
            time: formattedTime
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
            Id: bookmarkId
        });
        
        
    };

    const handleDeleteBookmark = (bookmarkId: number) => {
        // Find the bookmark in the video's bookmarks array
        const bookmarkIndex = video.bookmarks.findIndex(b => b.id === bookmarkId);
        
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
                Id: bookmarkId
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

    return (
        <DndProvider backend={HTML5Backend}>
            <div className="flex w-full h-full bg-base-200 overflow-hidden" ref={containerRef}>
                <div className="flex-1 p-4 w-full lg:w-3/4">
                    <div
                        className={`${isFullscreen ? 'fixed inset-0 z-50 w-screen h-screen overflow-hidden bg-black' : 'relative'} ${(!controlsVisible && isPointerInPlayer) ? 'cursor-none' : ''}`}
                        ref={playerContainerRef}
                        onMouseMove={() => {
                            setIsPointerInPlayer(true);
                            showControlsTemporarily();
                        }}
                        onMouseLeave={() => {
                            setIsPointerInPlayer(false);
                            if (controlsHideTimeoutRef.current) {
                                clearTimeout(controlsHideTimeoutRef.current);
                                controlsHideTimeoutRef.current = null;
                            }
                            setControlsVisible(false);
                        }}
                    >
                        <video
                            autoPlay
                            className={`block relative ${isFullscreen ? 'w-full h-full' : 'rounded-lg w-full overflow-hidden aspect-video max-h-[calc(100vh-100px)] md:max-h-[calc(100vh-200px)]'}`}
                            src={getVideoPath()}
                            ref={videoRef}
                            onClick={togglePlayPause}
                            onDoubleClick={toggleFullscreen}
                            style={{ backgroundColor: 'black', objectFit: isFullscreen ? 'contain' as const : undefined }}
                        />
                        <audio
                            className="relative rounded-lg w-full overflow-hidden aspect-video max-h-[calc(100vh-100px)] md:max-h-[calc(100vh-200px)]"
                            src={getAudioPath()}
                            ref={audioRef}
                            hidden
                        />
                        
                        <div
                            className={`absolute left-4 right-4 bottom-4 bg-black/70 rounded-lg px-3 py-2 flex items-center gap-3 transition-opacity duration-300 ${controlsVisible ? 'opacity-100' : 'opacity-0'}`}
                        >
                            <button
                                onClick={togglePlayPause}
                                className="text-white hover:text-accent transition-colors"
                                aria-label={isPlaying ? 'Pause' : 'Play'}
                            >
                                {isPlaying ? <MdPause className="w-6 h-6" /> : <MdPlayArrow className="w-6 h-6" />}
                            </button>

                            <span className="text-xs tabular-nums text-white/90 w-12 text-right">{formatTime(currentTime)}</span>

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

                            <span className="text-xs tabular-nums text-white/90 w-12">{formatTime(duration)}</span>

                            <div className="flex items-center gap-2 ml-2">
                                <button
                                    onClick={toggleMute}
                                    className="text-white hover:text-accent transition-colors"
                                    aria-label={isMuted ? 'Unmute' : 'Mute'}
                                >
                                    {isMuted || volume === 0 ? (
                                        <MdVolumeOff className="w-6 h-6" />
                                    ) : volume < 0.33 ? (
                                        <MdVolumeMute className="w-6 h-6" />
                                    ) : volume < 0.67 ? (
                                        <MdVolumeDown className="w-6 h-6" />
                                    ) : (
                                        <MdVolumeUp className="w-6 h-6" />
                                    )}
                                </button>
                                <input
                                    type="range"
                                    min="0"
                                    max="1"
                                    step="0.01"
                                    value={volume}
                                    onChange={handleVolumeChange}
                                    onPointerUp={(e) => (e.currentTarget as HTMLInputElement).blur()}
                                    onMouseUp={(e) => (e.currentTarget as HTMLInputElement).blur()}
                                    onTouchEnd={(e) => (e.currentTarget as HTMLInputElement).blur()}
                                    className="w-24 h-1 bg-gray-600 rounded-lg appearance-none cursor-pointer accent-accent"
                                    aria-label="Volume"
                                />
                            </div>

                            <button
                                onClick={toggleFullscreen}
                                onPointerUp={(e) => (e.currentTarget as HTMLInputElement).blur()}
                                onMouseUp={(e) => (e.currentTarget as HTMLInputElement).blur()}
                                onTouchEnd={(e) => (e.currentTarget as HTMLInputElement).blur()}
                                className="text-white hover:text-accent transition-colors ml-2"
                                aria-label={isFullscreen ? 'Exit Fullscreen' : 'Enter Fullscreen'}
                            >
                                {isFullscreen ? <MdFullscreenExit className="w-6 h-6" /> : <MdFullscreen className="w-6 h-6" />}
                            </button>
                        </div>
                    </div>
                    <div
                        className="timeline-wrapper mt-2 relative overflow-x-scroll overflow-y-hidden w-full select-none"
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
                                minWidth: "100%",
                                overflow: "hidden"
                            }}
                        >
                            {filteredBookmarks.map((bookmark, index) => {
                                const timeInSeconds = timeStringToSeconds(bookmark.time);
                                const leftPos = timeInSeconds * pixelsPerSecond;
                                const Icon = iconMapping[bookmark.type as BookmarkType] || IoSkull;

                                return (
                                    <div
                                        key={`bookmark-${bookmark.id ?? index}`}
                                        className="tooltip absolute bottom-0 transform -translate-x-1/2 cursor-pointer z-10 flex flex-col items-center text-[#25272e]"
                                        data-tip={`${bookmark.type}${bookmark.subtype ? ` - ${bookmark.subtype}` : ''} (${bookmark.time})`}
                                        style={{ left: `${leftPos}px` }}
                                        onContextMenu={(e) => {
                                            e.preventDefault();
                                            handleDeleteBookmark(bookmark.id);
                                        }}
                                    >
                                        <div className="bg-[#EFAF2B] w-[26px] h-[26px] rounded-full flex items-center justify-center mb-0">
                                            <Icon size={16} />
                                        </div>
                                        <div className="w-[2px] h-[16px] bg-[#EFAF2B]" />
                                    </div>
                                );
                            })}
                            {minorTicks.map((tickTime, index) => {
                                if (tickTime >= duration) return null;
                                const leftPos = tickTime * pixelsPerSecond;
                                return (
                                    <div
                                        key={`minor-${index}`}
                                        className="absolute bottom-0 h-[6px] border-l border-white/20"
                                        style={{
                                            left: `${leftPos}px`
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
                                        className="absolute bottom-0 text-center text-white select-none -translate-x-1/2 whitespace-nowrap"
                                        style={{
                                            left: `${leftPos}px`
                                        }}
                                    >
                                        <span
                                            className="absolute bottom-full left-1/2 -translate-x-1/2 text-xs mb-[3px]"
                                        >
                                            {formatTime(tickTime)}
                                        </span>
                                        <div
                                            className="w-[2px] h-[10px] bg-white mx-auto"
                                        />
                                    </div>
                                );
                            })}
                        </div>
                        <div
                            className="timeline-container bg-base-300 border border-primary rounded-lg relative h-[50px] w-full overflow-hidden"
                            style={{
                                width: `${duration * pixelsPerSecond}px`,
                                minWidth: "100%"
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
                                            className={`absolute top-0 left-0 h-full cursor-move ${hidden ? "hidden" : ""} transition-colors overflow-hidden rounded-r-sm rounded-l-sm shadow-md
                                                bg-primary/40 border border-primary/40`}
                                            style={{ left: `${left}px`, width: `${width}px` }}
                                            onMouseEnter={() => { setHoveredSelectionId(sel.id); }}
                                            onMouseLeave={() => { setHoveredSelectionId(null); }}
                                            onMouseDown={(e) => handleSelectionMouseDown(e, sel.id)}
                                            onContextMenu={(e) => { e.preventDefault(); removeSelection(sel.id); }}
                                        >
                                            <div className="absolute left-0 top-0 h-full w-[4px] bg-accent/80 rounded-l-sm pointer-events-none" />
                                            <div className="absolute right-0 top-0 h-full w-[4px] bg-accent/80 rounded-r-sm pointer-events-none" />
                                            
                                            <div className="absolute top-0 -left-[8px] w-[18px] h-full bg-transparent cursor-col-resize pointer-events-auto" onMouseDown={(e) => handleResizeMouseDown(e, sel.id, "start")} aria-label="Resize segment start" />
                                            <div className="absolute top-0 -right-[8px] w-[18px] h-full bg-transparent cursor-col-resize pointer-events-auto" onMouseDown={(e) => handleResizeMouseDown(e, sel.id, "end")} aria-label="Resize segment end" />
                                        </div>
                                        
                                    </>
                                );
                            })}
                            {resizingSelectionId == null && (
                                <div
                                    className="marker absolute top-0 left-0 w-1 h-full rounded-sm -translate-x-1/2 cursor-pointer shadow bg-accent"
                                    style={{ left: `${currentTime * pixelsPerSecond}px` }}
                                    onMouseDown={handleMarkerDragStart}
                                />
                            )}
                        </div>
                    </div>
                    <div className="flex items-center justify-between gap-4 py-1">
                        <div className="flex items-center gap-3">
                            <div className="flex items-center join bg-base-300 rounded-lg border border-custom">
                                <button
                                    onClick={() => skipTime(-10)}
                                    className="btn btn-sm btn-secondary h-10 text-gray-400 hover:text-accent join-item"
                                >
                                    <MdReplay10 className="w-6 h-6" />
                                </button>
                                <button
                                    onClick={handlePlayPause}
                                    className="btn btn-sm btn-secondary h-10 text-gray-400 hover:text-accent join-item"
                                    data-tip={isPlaying ? "Pause" : "Play"}
                                >
                                    {isPlaying ? <MdPause className="w-6 h-6" /> : <MdPlayArrow className="w-6 h-6" />}
                                </button>
                                <button
                                    onClick={() => skipTime(10)}
                                    className="btn btn-sm btn-secondary h-10 text-gray-400 hover:text-accent join-item"
                                    data-tip="Forward 10s"
                                >
                                    <MdForward10 className="w-6 h-6" />
                                </button>
                            </div>
                            {(video.type === "Clip" || video.type === "Highlight") && (
                                <button
                                    className="btn btn-sm btn-secondary border-custom hover:border-custom h-10 px-6 text-gray-400 hover:text-accent flex items-center"
                                    onClick={handleUpload}
                                    disabled={uploads[video.fileName + ".mp4"]?.status === 'uploading' || uploads[video.fileName + ".mp4"]?.status === 'processing'}
                                >
                                    <MdOutlineFileUpload className="w-6 h-6" />
                                    <span>Upload</span>
                                </button>
                            )}
                            {(video.type === "Session" || video.type === "Buffer") && (
                                <>
                                    <button
                                        className={`btn btn-sm btn-secondary border-custom hover:border-custom h-10 text-gray-400 hover:text-accent flex items-center gap-1`}
                                        onClick={handleCreateClip}
                                    >
                                        <MdMovieCreation className="w-6 h-6" />
                                        <span>Create Clip</span>
                                    </button>
                                    <div className="indicator">
                                        <button className={`btn btn-sm btn-secondary border-custom hover:border-custom h-10 text-gray-400 hover:text-accent flex items-center gap-1`} onClick={handleAddSelection}>
                                            {showNoSegmentsIndicator && <span className="indicator-item badge badge-sm badge-primary animate-pulse"></span>}
                                            <MdAddBox className="w-6 h-6" />
                                            <span>Add Segment</span>
                                        </button>
                                    </div>
                                </>
                            )}
                        </div>

                        <div className="flex items-center gap-3">
                            {(video.type === "Session" || video.type === "Buffer") && (
                                <>
                                    <div className="flex items-center gap-2 bg-base-300 rounded-lg">
                                        <button
                                            onClick={handleAddBookmark}
                                            className="btn btn-sm btn-secondary border-custom hover:border-custom h-10 text-gray-400 hover:text-accent tooltip tooltip-secondary tooltip-bottom" data-tip="Add Bookmark"
                                        >
                                            <MdBookmarkAdd className="w-6 h-6" />
                                        </button>
                                    </div>
                                    {availableBookmarkTypes.length > 0 && (
                                        <div className="flex items-center gap-0 bg-base-300 px-0 rounded-lg h-10 join border border-custom">
                                            {availableBookmarkTypes.map(type => (
                                                <button
                                                    key={type}
                                                    onClick={() => toggleBookmarkType(type)}
                                                    className={`btn btn-sm btn-secondary border-none transition-colors join-item ${selectedBookmarkTypes.has(type)
                                                            ? 'text-accent'
                                                            : 'text-gray-400'}`}
                                                >
                                                    {React.createElement(iconMapping[type] || IoSkull, {
                                                        className: "w-6 h-6"
                                                    })}
                                                </button>
                                            ))}
                                        </div>
                                    )}
                                </>
                            )}

                            <div className="flex items-center gap-1 bg-base-300 px-0 rounded-lg h-10 border border-custom">
                                <button
                                    onClick={() => handleZoomChange(false)}
                                    className="btn btn-sm btn-secondary disabled:opacity-100 disabled:bg-base-300"
                                    disabled={zoom <= 1}
                                >
                                    <IoRemove className="w-4 h-4" />
                                </button>
                                <span className="text-sm font-medium text-center text-gray-300">
                                    {Math.round(zoom * 100 / 5) * 5}%
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
                {(video.type === "Session" || video.type === "Buffer") && (
                    <div className="bg-base-300 text-neutral-content w-52 2xl:w-72 flex flex-col h-full pl-4 pr-1 pt-4 border-l border-custom">
                        <div className="overflow-y-scroll flex-1  mt-1 p-1">
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
                            <label className="cursor-pointer flex items-center">
                                <input
                                    type="checkbox"
                                    name="clipClearSelectionsAfterCreatingClip"
                                    checked={settings.clipClearSelectionsAfterCreatingClip}
                                    onChange={(e) => updateSettings({clipClearSelectionsAfterCreatingClip: e.target.checked})}
                                    className="checkbox checkbox-sm checkbox-accent"
                                />
                                <span className="text-sm ml-2">Auto-Clear Selections</span>
                            </label>
                        </div>
                        <div className="flex items-center gap-0 bg-base-300 px-0 rounded-lg h-10 mb-2 mr-3 tooltip">
                            <button
                                className="btn btn-sm btn-secondary border border-custom disabled:border-custom hover:border-custom h-10 text-gray-400 hover:text-accent w-full py-0"
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
