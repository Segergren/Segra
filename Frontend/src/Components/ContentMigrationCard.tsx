import { useState } from 'react';
import { X } from 'lucide-react';
import {
  useContentMigration,
  type ContentMigrationProgress,
} from '../Context/ContentMigrationContext';

interface ContentMigrationCardProps {
  migration: ContentMigrationProgress;
}

export default function ContentMigrationCard({ migration }: ContentMigrationCardProps) {
  const { cancelMigration } = useContentMigration();
  const [isCancelling, setIsCancelling] = useState(false);

  const getStatusText = () => {
    switch (migration.status) {
      case 'migrating':
        return isCancelling
          ? 'Cancelling...'
          : `Moving ${migration.currentFileIndex} of ${migration.totalFiles}`;
      case 'cancelled':
        return migration.message || 'Move Cancelled';
      case 'done':
        return migration.message || 'Move Complete';
      case 'error':
        return migration.message || 'Move Error';
      default:
        return 'Moving...';
    }
  };

  const getProgressPercentage = () => {
    return Math.min(migration.progress, 100);
  };

  return (
    <div className="w-full px-2">
      <div className="bg-base-300 border border-base-400 border-opacity-75 rounded-lg p-3">
        <div className="flex items-center gap-3 w-full">
          {migration.status === 'migrating' && (
            <span className="loading loading-spinner text-primary"></span>
          )}
          {migration.status === 'done' && (
            <div className="w-5 h-5 rounded-full bg-green-500 flex items-center justify-center">
              <svg className="w-3 h-3 text-white" fill="currentColor" viewBox="0 0 20 20">
                <path
                  fillRule="evenodd"
                  d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z"
                  clipRule="evenodd"
                />
              </svg>
            </div>
          )}
          {migration.status === 'cancelled' && (
            <div className="w-5 h-5 rounded-full bg-gray-500 flex items-center justify-center">
              <X className="w-3 h-3 text-white" strokeWidth={3} />
            </div>
          )}
          {migration.status === 'error' && (
            <div className="w-5 h-5 rounded-full bg-red-500 flex items-center justify-center">
              <svg className="w-3 h-3 text-white" fill="currentColor" viewBox="0 0 20 20">
                <path
                  fillRule="evenodd"
                  d="M4.293 4.293a1 1 0 011.414 0L10 8.586l4.293-4.293a1 1 0 111.414 1.414L11.414 10l4.293 4.293a1 1 0 01-1.414 1.414L10 11.414l-4.293 4.293a1 1 0 01-1.414-1.414L8.586 10 4.293 5.707a1 1 0 010-1.414z"
                  clipRule="evenodd"
                />
              </svg>
            </div>
          )}

          <div className="min-w-0 flex-1 relative">
            {migration.status === 'migrating' && (
              <button
                onClick={() => {
                  setIsCancelling(true);
                  cancelMigration();
                }}
                disabled={isCancelling}
                className="absolute right-0 -top-px flex items-center h-5 px-1 transition-colors cursor-pointer disabled:opacity-50"
                aria-label="Cancel move"
              >
                <X size={16} />
              </button>
            )}
            <div className="text-gray-200 text-sm font-medium truncate pr-6">{getStatusText()}</div>
            {migration.fileName && (
              <div className="text-gray-400 text-xs truncate">{migration.fileName}</div>
            )}
            <div className="w-full bg-base-200 rounded-full h-1.5 mt-2">
              <div
                className={`h-1.5 rounded-full transition-all duration-300 ${
                  migration.status === 'error'
                    ? 'bg-red-500'
                    : migration.status === 'done'
                      ? 'bg-green-500'
                      : migration.status === 'cancelled'
                        ? 'bg-gray-500'
                        : 'bg-primary'
                }`}
                style={{ width: `${getProgressPercentage()}%` }}
              ></div>
            </div>
            <div className="text-gray-500 text-xs mt-1">{getProgressPercentage().toFixed(0)}%</div>
          </div>
        </div>
      </div>
    </div>
  );
}
