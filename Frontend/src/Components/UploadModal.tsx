import { useState, useRef, useEffect } from 'react';
import { Content } from '../Models/types';
import { useSettings, useSettingsUpdater } from '../Context/SettingsContext';
import { useAuth } from '../Hooks/useAuth.tsx';
import { MdOutlineFileUpload } from 'react-icons/md';
import Button from './Button';

interface UploadModalProps {
  video: Content;
  onUpload: (title: string, description: string, visibility: 'Public' | 'Unlisted') => void;
  onClose: () => void;
}

export default function UploadModal({ video, onUpload, onClose }: UploadModalProps) {
  const { clipShowInBrowserAfterUpload } = useSettings();
  const updateSettings = useSettingsUpdater();
  const { session } = useAuth();
  const [title, setTitle] = useState(video.title || '');
  const [description, setDescription] = useState('');
  const [visibility] = useState<'Public' | 'Unlisted'>('Public');
  const [titleError, setTitleError] = useState(false);
  const titleInputRef = useRef<HTMLInputElement>(null);

  // Focus on title input when modal opens (hacky but works)
  useEffect(() => {
    const timer = setTimeout(() => {
      const el = titleInputRef.current;
      if (!el) return;
      el.focus();
      el.select();
    }, 100);
    return () => clearTimeout(timer);
  }, [video.fileName]);

  const handleUpload = () => {
    if (!title.trim()) {
      setTitleError(true);
      titleInputRef.current?.focus();
      return;
    }
    setTitleError(false);
    onUpload(title, description, visibility);
    onClose();
  };

  const handleKeyPress = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      handleUpload();
    }
  };

  return (
    <>
      <div className="bg-base-300">
        <div className="modal-header">
          <Button
            variant="ghost"
            size="sm"
            icon
            className="absolute right-4 top-1 z-10"
            onClick={onClose}
          >
            ✕
          </Button>
        </div>
        <div className="modal-body pt-8">
          <div className="form-control w-full">
            <label className="label">
              <span className="label-text text-base-content">Title</span>
            </label>
            <input
              ref={titleInputRef}
              type="text"
              placeholder="Enter a title"
              value={title}
              onChange={(e) => {
                setTitle(e.target.value);
                setTitleError(false);
              }}
              onKeyDown={handleKeyPress}
              className={`input input-bordered bg-base-300 w-full focus:outline-none ${titleError ? 'input-error' : ''}`}
            />
            {titleError && (
              <label className="label mt-1">
                <span className="label-text-alt text-error">Title is required</span>
              </label>
            )}
          </div>

          <div className="form-control w-full mt-4">
            <label className="label">
              <span className="label-text text-base-content">Description</span>
            </label>
            <textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              rows={4}
              className="textarea textarea-bordered bg-base-300 w-full focus:outline-none resize-none"
              placeholder="Add a description (optional)"
            />
          </div>

          <div className="form-control mt-4">
            <label className="label cursor-pointer justify-start gap-2">
              <input
                type="checkbox"
                className="checkbox checkbox-primary"
                checked={clipShowInBrowserAfterUpload}
                onChange={(e) => updateSettings({ clipShowInBrowserAfterUpload: e.target.checked })}
              />
              <span className="label-text text-base-content">Open in Browser After Upload</span>
            </label>
          </div>
        </div>
        <div className="modal-action mt-6">
          <Button
            variant="primary"
            className="w-full"
            onClick={handleUpload}
            disabled={session === null}
          >
            <MdOutlineFileUpload className="w-5 h-5" />
            {session === null ? 'Login to upload' : 'Upload'}
          </Button>
        </div>
      </div>
    </>
  );
}
