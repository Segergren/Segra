import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { SegmentCardProps } from '../Models/types';
import { useDrag, useDrop } from 'react-dnd';
import { Headphones } from 'lucide-react';
import { useDeleteConfirmation } from '../Hooks/useDeleteConfirmation';
import AudioTrackMixer, { AUDIO_TRACK_MENU_CLASS } from './AudioTrackMixer';
import { hasCustomAudioMix, toggleTrackInMix } from '../Utils/AudioMixUtils';

const DRAG_TYPE = 'SEGMENT_CARD';

const SegmentCard: React.FC<SegmentCardProps> = React.memo(
  ({
    segment,
    index,
    moveCard,
    formatTime,
    isHovered,
    segmentHover,
    removeSegment,
    audioTrackNames,
    audioTrackTypes,
    onAudioMixChange,
  }) => {
    const [audioMenuPos, setAudioMenuPos] = useState<{
      x: number;
      y: number;
      flipUp: boolean;
      visible: boolean;
    } | null>(null);

    const indexRef = useRef(index);
    const moveCardRef = useRef(moveCard);
    const audioMenuOpenRef = useRef(false);
    useLayoutEffect(() => {
      indexRef.current = index;
      moveCardRef.current = moveCard;
      audioMenuOpenRef.current = !!audioMenuPos?.visible;
    });

    const [{ isDragging }, dragRef] = useDrag(
      () => ({
        type: DRAG_TYPE,
        item: { index },
        canDrag: () => !audioMenuOpenRef.current,
        collect: (monitor) => ({
          isDragging: monitor.isDragging(),
        }),
      }),
      [index],
    );

    const [, dropRef] = useDrop(
      () => ({
        accept: DRAG_TYPE,
        hover: (item: { index: number }) => {
          if (item.index !== indexRef.current) {
            moveCardRef.current(item.index, indexRef.current);
            item.index = indexRef.current;
          }
        },
      }),
      [],
    );

    const dragDropRef = (node: HTMLDivElement | null) => {
      dragRef(node);
      dropRef(node);
    };

    const { startTime, endTime, thumbnailDataUrl, isLoading } = segment;
    const confirmDelete = useDeleteConfirmation();

    // Fade the first thumbnail in and crossfade later ones over the current
    // image, instead of flashing a loading state.
    const [baseSrc, setBaseSrc] = useState(thumbnailDataUrl);
    const [baseVisible, setBaseVisible] = useState(!!thumbnailDataUrl);
    const [incomingSrc, setIncomingSrc] = useState<string | null>(null);
    const [incomingVisible, setIncomingVisible] = useState(false);

    useEffect(() => {
      if (!thumbnailDataUrl || thumbnailDataUrl === baseSrc) return;
      if (!baseSrc) {
        // First thumbnail: mount it hidden and fade it in.
        setBaseSrc(thumbnailDataUrl);
        setBaseVisible(false);
        return;
      }
      setIncomingSrc(thumbnailDataUrl);
      setIncomingVisible(false);
    }, [thumbnailDataUrl, baseSrc]);
    const hasAudioTracks = audioTrackNames && audioTrackNames.length > 1 && onAudioMixChange;
    const mutedTracks = segment.mutedAudioTracks ?? [];
    const trackVolumes = segment.audioTrackVolumes ?? {};
    const trackIndices = audioTrackNames?.map((_, i) => i) ?? [];
    const customMix = hasCustomAudioMix(segment.mutedAudioTracks, segment.audioTrackVolumes);
    const closeAudioMenu = () =>
      setAudioMenuPos((prev) => (prev ? { ...prev, visible: false } : null));

    return (
      <div
        ref={dragDropRef}
        className={`mb-2 cursor-move w-full relative rounded-xl transition-all duration-200 !outline !outline-1 ${isHovered ? '!outline-primary' : '!outline-base-400'}`}
        style={{ opacity: isDragging ? 0.3 : 1 }}
        onMouseEnter={(e) => segmentHover.enter(segment.id, e)}
        onMouseMove={segmentHover.move}
        onMouseDown={segmentHover.commit}
        onMouseLeave={() => {
          segmentHover.leave();
          setAudioMenuPos((prev) => (prev ? { ...prev, visible: false } : null));
        }}
        onContextMenu={(e) => {
          e.preventDefault();
          confirmDelete({
            title: 'Delete segment?',
            description: 'Remove this segment from the clip? This action cannot be undone.',
            onConfirm: () => removeSegment(segment.id),
          });
        }}
      >
        {baseSrc ? (
          <figure className="relative rounded-xl overflow-hidden">
            <img
              src={baseSrc}
              alt="Segment"
              className={`w-full transition-opacity duration-300 ${
                baseVisible ? 'opacity-100' : 'opacity-0'
              }`}
              onLoad={() => requestAnimationFrame(() => setBaseVisible(true))}
            />
            {incomingSrc && (
              <img
                src={incomingSrc}
                alt="Segment"
                className={`absolute inset-0 w-full transition-opacity duration-300 ${
                  incomingVisible ? 'opacity-100' : 'opacity-0'
                }`}
                onLoad={() => requestAnimationFrame(() => setIncomingVisible(true))}
                onTransitionEnd={() => {
                  setBaseSrc(incomingSrc);
                  setBaseVisible(true);
                  setIncomingSrc(null);
                  setIncomingVisible(false);
                }}
              />
            )}
            <div className="absolute bottom-2 right-2 bg-black/75 text-white text-xs px-2 py-1 rounded">
              {formatTime(startTime)} - {formatTime(endTime)}
            </div>
          </figure>
        ) : isLoading || thumbnailDataUrl ? (
          // Reserve 16:9 space while the first thumbnail loads so it fades in
          // without shifting the layout.
          <div className="relative w-full aspect-video rounded-xl bg-base-100/40">
            <div className="absolute bottom-2 right-2 bg-black/75 text-white text-xs px-2 py-1 rounded">
              {formatTime(startTime)} - {formatTime(endTime)}
            </div>
          </div>
        ) : (
          <div className="h-32 bg-gray-700 flex items-center justify-center text-white">
            <span>No thumbnail</span>
          </div>
        )}

        {hasAudioTracks && (
          <div className="absolute top-1.5 right-1.5">
            <button
              onClick={(e) => {
                e.stopPropagation();
                if (audioMenuPos?.visible) {
                  setAudioMenuPos((prev) => (prev ? { ...prev, visible: false } : null));
                } else {
                  const rect = e.currentTarget.getBoundingClientRect();
                  const estimatedHeight = 24 + audioTrackNames.length * 32;
                  const fitsBelow = rect.bottom + 4 + estimatedHeight <= window.innerHeight;
                  const top = fitsBelow
                    ? rect.bottom + 4
                    : Math.max(8, rect.top - 4 - estimatedHeight);
                  const next = {
                    x: rect.right,
                    y: top,
                    flipUp: !fitsBelow,
                    visible: false,
                  };
                  setAudioMenuPos(next);
                  requestAnimationFrame(() =>
                    setAudioMenuPos((prev) => (prev ? { ...prev, visible: true } : null)),
                  );
                }
              }}
              className={`flex items-center justify-center w-6 h-6 rounded-full transition-all cursor-pointer bg-black/60 backdrop-blur-sm hover:bg-black/80 ${audioMenuPos?.visible || customMix ? 'text-accent' : 'text-white/80 hover:text-white'} ${isHovered || audioMenuPos?.visible || customMix ? 'opacity-100' : 'opacity-0'}`}
              aria-label="Segment audio"
            >
              <Headphones className="w-3.5 h-3.5" />
            </button>
            {audioMenuPos && (
              <div
                className={`fixed z-[200] cursor-default transition-all duration-300 ${AUDIO_TRACK_MENU_CLASS} ${
                  audioMenuPos.visible
                    ? 'opacity-100 translate-y-0 pointer-events-auto'
                    : audioMenuPos.flipUp
                      ? 'opacity-0 translate-y-2 pointer-events-none'
                      : 'opacity-0 -translate-y-2 pointer-events-none'
                }`}
                style={{ right: window.innerWidth - audioMenuPos.x, top: audioMenuPos.y }}
                onClick={(e) => e.stopPropagation()}
                onDragStart={(e) => e.preventDefault()}
                onTransitionEnd={() =>
                  setAudioMenuPos((prev) => (prev && !prev.visible ? null : prev))
                }
              >
                <AudioTrackMixer
                  tracks={audioTrackNames.map((name, i) => ({
                    index: i,
                    name,
                    type: audioTrackTypes?.[i],
                  }))}
                  mutedTracks={new Set(mutedTracks)}
                  volumes={trackVolumes}
                  onToggleMute={(index) =>
                    onAudioMixChange(segment.id, {
                      mutedAudioTracks: toggleTrackInMix(mutedTracks, index, trackIndices),
                    })
                  }
                  onVolumeChange={(index, volume, unmute) =>
                    onAudioMixChange(segment.id, {
                      audioTrackVolumes: { ...trackVolumes, [index]: volume },
                      mutedAudioTracks: unmute
                        ? toggleTrackInMix(mutedTracks, index, trackIndices)
                        : mutedTracks,
                    })
                  }
                  onClose={audioMenuPos.visible ? closeAudioMenu : undefined}
                />
              </div>
            )}
          </div>
        )}
      </div>
    );
  },
);

export default SegmentCard;
