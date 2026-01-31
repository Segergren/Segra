import { useState } from 'react';
import { AnimatePresence, motion } from 'framer-motion';
import { useUploads, type UploadProgress } from '../Context/UploadContext';
import CircularProgress from './CircularProgress';
import { MdClose } from 'react-icons/md';

interface UploadCardProps {
  upload: UploadProgress;
}

export default function UploadCard({ upload }: UploadCardProps) {
  const { cancelUpload } = useUploads();
  const [isCancelling, setIsCancelling] = useState(false);
  const isUploading = upload.status === 'uploading';

  const handleCancel = () => {
    setIsCancelling(true);
    cancelUpload(upload.fileName);
  };

  const getStatusText = () => {
    switch (upload.status) {
      case 'uploading':
        return `Uploading ${Math.round(upload.progress)}%`;
      case 'processing':
        return 'Processing';
      case 'done':
        return 'Upload Complete';
      case 'error':
        return upload.message || 'Upload Failed';
      default:
        return 'Uploading...';
    }
  };

  return (
    <div className="w-full px-2">
      <div className="bg-base-300 border border-base-400 border-opacity-75 rounded-lg p-3">
        <div className="flex items-center gap-3 w-full relative">
          {/* Progress indicator */}
          {isUploading ? (
            <CircularProgress progress={upload.progress} size={24} strokeWidth={3} duration={20} />
          ) : (
            <span className="loading loading-spinner text-primary"></span>
          )}

          {/* Upload Details */}
          <div className="min-w-0 flex-1">
            <AnimatePresence>
              {isUploading && (
                <motion.button
                  initial={{ opacity: 0 }}
                  animate={{ opacity: 1 }}
                  exit={{ opacity: 0 }}
                  transition={{ duration: 0.2 }}
                  onClick={handleCancel}
                  disabled={isCancelling}
                  className="absolute right-0 top-1/2 -translate-y-1/2 p-1 transition-colors cursor-pointer disabled:opacity-50"
                  aria-label="Cancel upload"
                >
                  <MdClose size={16} />
                </motion.button>
              )}
            </AnimatePresence>
            <div className="text-gray-200 text-sm font-medium truncate">{getStatusText()}</div>
            <div className="text-gray-400 text-xs truncate">{upload.title}</div>
          </div>
        </div>
      </div>
    </div>
  );
}
