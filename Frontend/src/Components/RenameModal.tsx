import { useState, useEffect, useRef } from "react";
import { Content } from "../Models/types";
import { MdDriveFileRenameOutline } from "react-icons/md";

interface RenameModalProps {
  content: Content;
  onRename: (newName: string) => void;
  onClose: () => void;
}

export default function RenameModal({
  content,
  onRename,
  onClose,
}: RenameModalProps) {
  // Use the same logic as ContentCard: title || game || "Untitled"
  const displayedTitle = content.title || content.game || "Untitled";
  const actualTitle = content.title || "";
  const [newName, setNewName] = useState(actualTitle);
  const [nameError, setNameError] = useState(false);
  const nameInputRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    nameInputRef.current?.focus();
    nameInputRef.current?.select();
  }, []);

  const handleRename = () => {
    const trimmedName = newName.trim();
    if (!trimmedName) {
      setNameError(true);
      nameInputRef.current?.focus();
      return;
    }

    // Check if name contains invalid characters
    const invalidChars = /[<>:"/\\|?*]/;
    if (invalidChars.test(trimmedName)) {
      setNameError(true);
      nameInputRef.current?.focus();
      return;
    }

    setNameError(false);
    onRename(trimmedName);
    onClose();
  };

  const handleKeyPress = (e: React.KeyboardEvent) => {
    if (e.key === "Enter") {
      e.preventDefault();
      handleRename();
    } else if (e.key === "Escape") {
      e.preventDefault();
      onClose();
    }
  };

  return (
    <>
      <div className="bg-base-300">
        <div className="modal-header">
          <button
            className="btn btn-sm btn-circle btn-ghost absolute right-4 top-2"
            onClick={onClose}
          >
            ✕
          </button>
        </div>
        <div className="modal-body">
          <div className="flex items-center gap-3 mb-6 mt-4">
            <MdDriveFileRenameOutline className="w-8 h-8 text-primary" />
            <div>
              <h3 className="text-lg font-semibold text-base-content">
                Rename
              </h3>
              <p className="text-sm text-gray-400">
                Enter a new title for this {content.type.toLowerCase()}
              </p>
            </div>
          </div>

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
              className={`input input-bordered bg-base-300 w-full ${nameError ? "input-error" : ""}`}
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
        <div className="modal-action mt-6 gap-2">
          <button
            className="btn btn-ghost bg-base-300 h-10 text-gray-400 border-base-400 hover:text-base-content hover:border-base-400 hover:bg-base-200"
            onClick={onClose}
          >
            Cancel
          </button>
          <button
            className="btn btn-primary h-10 text-base-300 flex items-center gap-2"
            onClick={handleRename}
          >
            <MdDriveFileRenameOutline className="w-5 h-5" />
            Rename
          </button>
        </div>
      </div>
    </>
  );
}
