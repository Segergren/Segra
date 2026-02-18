import React from 'react';
import { SelectionCardProps } from '../Models/types';
import { useDrag, useDrop } from 'react-dnd';

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
  }) => {
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

    return (
      <div
        ref={dragDropRef}
        className={`mb-2 cursor-move w-full relative rounded-xl transition-all duration-200 !outline !outline-1 ${isHovered ? '!outline-primary' : '!outline-base-400'}`}
        style={{ opacity: isDragging ? 0.3 : 1 }}
        onMouseEnter={() => setHoveredSelectionId(selection.id)}
        onMouseLeave={() => setHoveredSelectionId(null)}
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
      </div>
    );
  },
);

export { DRAG_TYPE };
export default SelectionCard;
