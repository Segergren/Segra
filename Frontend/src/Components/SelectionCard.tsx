import React, { useState } from 'react';
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
  }) => {
    const [showAudioMenu, setShowAudioMenu] = useState(false);

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
          if (item.index !== index) {
            moveCard(item.index, index);
            item.index = index;
          }
        },
      }),
      [index, moveCard],
    );

    const dragDropRef = (node: HTMLDivElement | null) => {
      dragRef(node);
      dropRef(node);
    };

    const { startTime, endTime, thumbnailDataUrl, isLoading } = selection;
    const hasAudioTracks =
      audioTrackNames && audioTrackNames.length > 1 && onMutedAudioTracksChange;
    const mutedTracks = selection.mutedAudioTracks ?? [];

    const toggleTrack = (trackIndex: number) => {
      if (!onMutedAudioTracksChange) return;
      const isMuted = mutedTracks.includes(trackIndex);
      const newMuted = isMuted
        ? mutedTracks.filter((t) => t !== trackIndex)
        : [...mutedTracks, trackIndex];
      onMutedAudioTracksChange(selection.id, newMuted);
    };

    const hasMutedTracks = mutedTracks.length > 0;

    return (
      <div
        ref={dragDropRef}
        className={`mb-2 cursor-move w-full relative rounded-xl transition-all duration-200 !outline !outline-1 ${isHovered ? '!outline-primary' : '!outline-base-400'}`}
        style={{ opacity: isDragging ? 0.3 : 1 }}
        onMouseEnter={() => setHoveredSelectionId(selection.id)}
        onMouseLeave={() => {
          setHoveredSelectionId(null);
          setShowAudioMenu(false);
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
          <div className="absolute top-1.5 left-1.5">
            <button
              onClick={(e) => {
                e.stopPropagation();
                setShowAudioMenu(!showAudioMenu);
              }}
              className={`flex items-center justify-center w-6 h-6 rounded transition-colors cursor-pointer ${hasMutedTracks ? 'bg-red-500/70 text-white' : 'bg-black/60 text-white/80 hover:text-white hover:bg-black/80'}`}
              title="Audio tracks"
            >
              <TbHeadphones className="w-3.5 h-3.5" />
            </button>
            {showAudioMenu && (
              <div
                className="absolute top-full left-0 mt-1 p-2 bg-black/90 rounded-lg border border-base-400 min-w-40 z-50"
                onClick={(e) => e.stopPropagation()}
              >
                {audioTrackNames.map((name, i) => {
                  // Skip track 0 (Full Mix)
                  if (i === 0) return null;
                  const isMuted = mutedTracks.includes(i);
                  return (
                    <label key={i} className="flex items-center gap-2 py-0.5 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={!isMuted}
                        onChange={() => toggleTrack(i)}
                        className="checkbox checkbox-primary checkbox-xs"
                      />
                      <span className="text-xs text-white/80 truncate">{name}</span>
                    </label>
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
