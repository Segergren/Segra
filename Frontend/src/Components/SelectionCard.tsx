import React, { useLayoutEffect, useRef, useState } from 'react';
import { SelectionCardProps } from '../Models/types';
import { useDrag, useDrop } from 'react-dnd';
import { TbHeadphones } from 'react-icons/tb';

const DRAG_TYPE = 'SELECTION_CARD';

const SelectionCard: React.FC<SelectionCardProps> = React.memo(
  ({
    selection,
    index,
    moveCard,
    formatTime,
    isHovered,
    setHoveredSelectionId,
    removeSelection,
    audioTrackNames,
    onMutedAudioTracksChange,
    onAudioTrackVolumesChange,
  }) => {
    const [audioMenuPos, setAudioMenuPos] = useState<{ x: number; y: number } | null>(null);

    const indexRef = useRef(index);
    const moveCardRef = useRef(moveCard);
    useLayoutEffect(() => {
      indexRef.current = index;
      moveCardRef.current = moveCard;
    });

    const [{ isDragging }, dragRef] = useDrag(
      () => ({
        type: DRAG_TYPE,
        item: { index },
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

    const { startTime, endTime, thumbnailDataUrl, isLoading } = selection;
    const hasAudioTracks =
      audioTrackNames && audioTrackNames.length > 1 && onMutedAudioTracksChange;
    const mutedTracks = selection.mutedAudioTracks ?? [];
    const trackVolumes = selection.audioTrackVolumes ?? {};

    const toggleTrack = (trackIndex: number) => {
      if (!onMutedAudioTracksChange || !audioTrackNames) return;
      const isMuted = mutedTracks.includes(trackIndex);
      if (isMuted) {
        // Enabling this track
        if (trackIndex === 0) {
          // Enabling Full Mix: mute all individual tracks
          const newMuted = audioTrackNames.map((_, i) => i).filter((i) => i !== 0);
          onMutedAudioTracksChange(selection.id, newMuted);
        } else {
          // Enabling an individual track: mute Full Mix
          const newMuted = mutedTracks.filter((t) => t !== trackIndex);
          if (!newMuted.includes(0)) newMuted.push(0);
          onMutedAudioTracksChange(selection.id, newMuted);
        }
      } else {
        // Muting this track
        onMutedAudioTracksChange(selection.id, [...mutedTracks, trackIndex]);
      }
    };

    return (
      <div
        ref={dragDropRef}
        className={`mb-2 cursor-move w-full relative rounded-xl transition-all duration-200 !outline !outline-1 ${isHovered ? '!outline-primary' : '!outline-base-400'}`}
        style={{ opacity: isDragging ? 0.3 : 1 }}
        onMouseEnter={() => setHoveredSelectionId(selection.id)}
        onMouseLeave={() => {
          setHoveredSelectionId(null);
          setAudioMenuPos(null);
        }}
        onContextMenu={(e) => {
          e.preventDefault();
          removeSelection(selection.id);
        }}
      >
        {isLoading ? (
          <div className="flex items-center justify-center bg-base-100 bg-opacity-75 rounded-xl w-full aspect-video">
            <span className="loading loading-spinner loading-md text-accent" />
            <div className="absolute bottom-2 right-2 bg-base-100 bg-opacity-75 text-white text-xs px-2 py-1 rounded">
              {formatTime(startTime)} - {formatTime(endTime)}
            </div>
          </div>
        ) : thumbnailDataUrl ? (
          <figure className="relative rounded-xl overflow-hidden">
            <img src={thumbnailDataUrl} alt="Selection" className="w-full" />
            <div className="absolute bottom-2 right-2 bg-base-100 bg-opacity-75 text-white text-xs px-2 py-1 rounded">
              {formatTime(startTime)} - {formatTime(endTime)}
            </div>
          </figure>
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
                if (audioMenuPos) {
                  setAudioMenuPos(null);
                } else {
                  const rect = e.currentTarget.getBoundingClientRect();
                  setAudioMenuPos({ x: rect.right, y: rect.bottom + 4 });
                }
              }}
              className="flex items-center justify-center w-6 h-6 rounded transition-colors cursor-pointer bg-black/60 text-white/80 hover:text-white hover:bg-black/80"
              title="Audio tracks"
            >
              <TbHeadphones className="w-3.5 h-3.5" />
            </button>
            {audioMenuPos && (
              <div
                className="fixed p-2 bg-black/90 rounded-lg border border-base-400 min-w-48 z-[200]"
                style={{ right: window.innerWidth - audioMenuPos.x, top: audioMenuPos.y }}
                onClick={(e) => e.stopPropagation()}
              >
                {audioTrackNames.map((name, i) => {
                  const isMuted = mutedTracks.includes(i);
                  const vol = trackVolumes[i] ?? 1;
                  return (
                    <div key={i} className="flex items-center justify-between gap-2 py-0.5">
                      <div className="flex items-center gap-2 min-w-0">
                        <input
                          type="checkbox"
                          checked={!isMuted}
                          onChange={() => toggleTrack(i)}
                          className="checkbox checkbox-primary checkbox-xs shrink-0"
                        />
                        <span
                          className="text-xs text-white/80 truncate"
                          title={name.replace(' (Default)', '')}
                        >
                          {name.replace(' (Default)', '')}
                        </span>
                      </div>
                      <div className="flex items-center gap-2 shrink-0">
                        <input
                          type="range"
                          min="0"
                          max="1"
                          step="0.02"
                          value={vol}
                          onChange={(e) => {
                            if (!onAudioTrackVolumesChange) return;
                            const newVolumes = { ...trackVolumes, [i]: parseFloat(e.target.value) };
                            onAudioTrackVolumesChange(selection.id, newVolumes);
                          }}
                          className="w-16 h-1 bg-gray-600 rounded-lg appearance-none cursor-pointer accent-accent"
                        />
                        <span className="text-[10px] text-white/50 w-7 text-right tabular-nums">
                          {Math.round(vol * 100)}%
                        </span>
                      </div>
                    </div>
                  );
                })}
              </div>
            )}
          </div>
        )}
      </div>
    );
  },
);

export { DRAG_TYPE };
export default SelectionCard;
