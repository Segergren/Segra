import { useState, useRef, useEffect } from 'react';
import { Content } from '../Models/types';
import { useSettings, useSettingsUpdater } from '../Context/SettingsContext';
import { useAuth } from '../Hooks/useAuth.tsx';
import { Upload, Globe, EyeOff, LogIn } from 'lucide-react';
import { useSelectedVideo } from '../Context/SelectedVideoContext';
import { useSelectedMenu } from '../Context/SelectedMenuContext';
import { requestSettingsSection } from '../Utils/SettingsNavigation';
import Button from './Button';
import DropdownSelect from './DropdownSelect';

interface UploadModalProps {
  video: Content;
  onUpload: (title: string, description: string, visibility: 'Public' | 'Unlisted') => void;
  onClose: () => void;
}

export default function UploadModal({ video, onUpload, onClose }: UploadModalProps) {
  const { clipShowInBrowserAfterUpload } = useSettings();
  const updateSettings = useSettingsUpdater();
  const { session } = useAuth();
  const { setSelectedVideo } = useSelectedVideo();
  const { setSelectedMenu } = useSelectedMenu();
  const [title, setTitle] = useState(video.title || '');
  const [description, setDescription] = useState('');
  const [visibility, setVisibility] = useState<'Public' | 'Unlisted'>(() =>
    localStorage.getItem('uploadVisibility') === 'Unlisted' ? 'Unlisted' : 'Public',
  );

  const handleVisibilityChange = (value: string) => {
    const next = value === 'Unlisted' ? 'Unlisted' : 'Public';
    setVisibility(next);
    localStorage.setItem('uploadVisibility', next);
  };
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
    if (session === null) return;
    if (!title.trim()) {
      setTitleError(true);
      titleInputRef.current?.focus();
      return;
    }
    setTitleError(false);
    onUpload(title, description, visibility);
    onClose();
  };

  // Sign-in lives in Settings, so leave the editor and open the Account section
  const openSignIn = () => {
    onClose();
    requestSettingsSection('account');
    setSelectedVideo(null);
    setSelectedMenu('Settings');
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
        <div className="modal-header pb-2">
          <div className="flex items-center justify-between gap-4">
            <h3 className="font-bold text-2xl">Upload to Segra.tv</h3>
            <Button variant="ghost" size="sm" icon onClick={onClose} aria-label="Close">
              ✕
            </Button>
          </div>
          {session === null && (
            <p className="mt-1 text-sm text-gray-400">
              Sign in to Segra.tv to upload clips and share them with a link.
            </p>
          )}
        </div>
        <div className="modal-body pt-3">
          <div className="form-control w-full">
            <label className="label">
              <span className="label-text text-base-content">
                Title <span className="text-error">*</span>
              </span>
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
              className={`input input-bordered bg-base-300 w-full focus:outline focus:outline-1 focus:outline-white focus:outline-offset-0 ${titleError ? 'input-error' : ''}`}
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
              className="textarea textarea-bordered bg-base-300 w-full focus:outline focus:outline-1 focus:outline-white focus:outline-offset-0 resize-none"
              placeholder="Add a description"
            />
          </div>

          <div className="form-control w-full mt-4">
            <label className="label">
              <span className="label-text text-base-content">Visibility</span>
            </label>
            <DropdownSelect
              items={[
                {
                  value: 'Public',
                  label: (
                    <span className="flex items-center gap-2">
                      <Globe size={16} />
                      Public
                    </span>
                  ),
                },
                {
                  value: 'Unlisted',
                  label: (
                    <span className="flex items-center gap-2">
                      <EyeOff size={16} />
                      Unlisted
                    </span>
                  ),
                },
              ]}
              value={visibility}
              onChange={handleVisibilityChange}
              align="start"
            />
          </div>

          <div className="form-control mt-4">
            <label className="label cursor-pointer justify-start gap-2">
              <input
                type="checkbox"
                className="checkbox checkbox-primary focus:!outline focus:!outline-1 focus:!outline-white focus:!outline-offset-2"
                checked={clipShowInBrowserAfterUpload}
                onChange={(e) => updateSettings({ clipShowInBrowserAfterUpload: e.target.checked })}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault();
                    updateSettings({ clipShowInBrowserAfterUpload: !clipShowInBrowserAfterUpload });
                  }
                }}
              />
              <span className="label-text text-base-content">Open in Browser</span>
            </label>
          </div>
        </div>
        <div className="modal-action mt-6">
          <Button
            variant="primary"
            className="w-full focus:!outline focus:!outline-1 focus:!outline-white focus:!outline-offset-2"
            onClick={session === null ? openSignIn : handleUpload}
          >
            {session === null ? (
              <>
                <LogIn className="w-5 h-5" />
                Sign In to Upload
              </>
            ) : (
              <>
                <Upload className="w-5 h-5" />
                Upload
              </>
            )}
          </Button>
        </div>
      </div>
    </>
  );
}
