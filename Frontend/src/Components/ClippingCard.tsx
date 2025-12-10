import React, { useState, useEffect } from 'react';
import { useClipping } from '../Hooks/useClipping';
import { MdClose } from 'react-icons/md';
import CircularProgress from './CircularProgress';

import { ClippingProgress } from '../Context/ClippingContext';

interface ClippingCardProps {
  clipping: ClippingProgress;
}

const ClippingCard: React.FC<ClippingCardProps> = ({ clipping }) => {
  const { cancelClip } = useClipping();
  const [displayProgress, setDisplayProgress] = useState(0);
  const [isCancelling, setIsCancelling] = useState(false);

  useEffect(() => {
    if (clipping.progress > 95) {
      setDisplayProgress(clipping.progress);
      return;
    }

    const timer = setInterval(() => {
      setDisplayProgress((prev) => {
        const diff = clipping.progress - prev;
        if (Math.abs(diff) < 0.1) return clipping.progress;
        return prev + diff * 0.15;
      });
    }, 50);

    return () => clearInterval(timer);
  }, [clipping.progress]);

  const handleCancel = () => {
    setIsCancelling(true);
    cancelClip(clipping.id);
  };

  const isError = clipping.progress === -1;

  return (
    <div className="w-full px-2">
      <div
        className={`bg-base-300 border ${isError ? 'border-error' : 'border-base-400'} border-opacity-75 rounded-lg p-3`}
      >
        <div className="flex items-center gap-3 w-full relative">
          {/* Progress */}
          {isError ? (
            <div className="w-4 h-4 rounded-full bg-error"></div>
          ) : clipping.progress < 100 ? (
            <CircularProgress progress={displayProgress} size={24} strokeWidth={2} duration={100} />
          ) : (
            <div className="w-4 h-4 rounded-full bg-success"></div>
          )}

          {/* Clipping Details */}
          <div className="min-w-0 flex-1">
            {clipping.progress >= 0 && clipping.progress < 100 && (
              <button
                onClick={handleCancel}
                disabled={isCancelling}
                className="absolute right-0 top-1/2 -translate-y-1/2 p-1 transition-colors cursor-pointer disabled:opacity-50"
                aria-label="Cancel clip"
              >
                <MdClose size={16} />
              </button>
            )}
            <div
              className={`text-sm font-medium truncate ${isError ? 'text-error' : 'text-gray-200'}`}
            >
              {isError ? 'Clip Failed' : 'Creating Clip'}
            </div>
            <div className={`text-xs truncate ${isError ? 'text-error/70' : 'text-gray-400'}`}>
              {isError ? clipping.error || 'Unknown error' : `${Math.round(displayProgress)}%`}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
};

export default ClippingCard;
