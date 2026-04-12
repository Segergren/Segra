import { useState, useEffect, useRef } from 'react';
import { Content } from '../Models/types';
import Button from './Button';

interface RenameModalProps {
  content: Content;
  onRename: (newName: string) => void;
  onClose: () => void;
}

export default function RenameModal({ content, onRename, onClose }: RenameModalProps) {
  // Use the same logic as ContentCard: title || game || "Untitled"
  const displayedTitle = content.title || content.game || 'Untitled';
  const actualTitle = content.title || '';
  const [newName, setNewName] = useState(actualTitle);
  const [nameError, setNameError] = useState(false);
  const nameInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const timer = setTimeout(() => {
      nameInputRef.current?.focus();
      nameInputRef.current?.select();
    }, 100);

    return () => clearTimeout(timer);
  }, []);

  const handleRename = () => {
    const trimmedName = newName.trim();

    // Check if name contains invalid characters (only if not empty)
    const invalidChars = /[<>:"/\\|?*]/;
    if (trimmedName && invalidChars.test(trimmedName)) {
      setNameError(true);
      nameInputRef.current?.focus();
      return;
    }

    setNameError(false);
    onRename(trimmedName);
    onClose();
  };

  const handleKeyPress = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      handleRename();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      onClose();
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
          <h3 className="font-bold text-2xl mb-6">Rename</h3>

          <div className="form-control w-full">
            <input
              ref={nameInputRef}
              type="text"
              value={newName}
              onChange={(e) => {
                setNewName(e.target.value);
                setNameError(false);
              }}
              onKeyDown={handleKeyPress}
              className={`input input-bordered bg-base-300 w-full ${nameError ? 'input-error' : ''}`}
              placeholder={displayedTitle}
            />
            {nameError && (
              <label className="label mt-1">
                <span className="label-text-alt text-error">
                  Invalid title, please avoid using special characters.
                </span>
              </label>
            )}
          </div>
        </div>
        <div className="modal-action mt-6">
          <Button variant="primary" className="w-full" onClick={handleRename}>
            Rename
          </Button>
        </div>
      </div>
    </>
  );
}
