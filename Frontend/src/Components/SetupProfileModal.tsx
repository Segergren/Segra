import { useState, useEffect, useCallback, useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { MdCameraAlt } from 'react-icons/md';
import { api } from '../lib/api';
import { useAuth } from '../Hooks/useAuth';
import Button from './Button';

function validateUsername(value: string): string {
  if (value.length < 3) return 'Username must be at least 3 characters';
  if (value.length > 20) return 'Username must be less than 20 characters';
  if (!/^[a-zA-Z0-9_-]+$/.test(value))
    return 'Username can only contain letters, numbers, underscores and hyphens';
  return '';
}

export default function SetupProfileModal() {
  const { session } = useAuth();
  const queryClient = useQueryClient();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [username, setUsername] = useState('');
  const [usernameError, setUsernameError] = useState('');
  const [isTaken, setIsTaken] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState('');
  const [avatarFile, setAvatarFile] = useState<File | null>(null);
  const [avatarPreview, setAvatarPreview] = useState<string | null>(null);

  const checkUsername = useCallback(async (name: string) => {
    try {
      const data = await api.checkUsername(name);
      setIsTaken(!data.available);
    } catch {
      setIsTaken(false);
    }
  }, []);

  useEffect(() => {
    if (!username || validateUsername(username)) {
      setIsTaken(false);
      return;
    }
    const timeout = setTimeout(() => checkUsername(username), 500);
    return () => clearTimeout(timeout);
  }, [username, checkUsername]);

  const handleChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const val = e.target.value;
    setUsername(val);
    setUsernameError(validateUsername(val));
    setError('');
  };

  const handleAvatarSelect = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;

    const allowedTypes = ['image/jpeg', 'image/png', 'image/gif', 'image/webp'];
    if (!allowedTypes.includes(file.type)) {
      setError('Avatar must be a JPEG, PNG, GIF, or WebP image');
      return;
    }

    if (file.size > 5 * 1024 * 1024) {
      setError('Avatar must be less than 5MB');
      return;
    }

    setAvatarFile(file);

    const reader = new FileReader();
    reader.onload = () => {
      const result = reader.result;
      if (
        typeof result === 'string' &&
        /^data:image\/(jpeg|png|gif|webp);base64,[A-Za-z0-9+/=]+$/.test(result)
      ) {
        setAvatarPreview(result);
      }
    };
    reader.readAsDataURL(file);
    setError('');
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!session) return;

    const validation = validateUsername(username);
    if (validation) {
      setError(validation);
      return;
    }
    if (isTaken) {
      setError('Username is already taken');
      return;
    }

    setIsSubmitting(true);
    try {
      // Upload avatar first if selected
      if (avatarFile) {
        const avatarData = await api.uploadAvatar(session.access_token, avatarFile);
        if (avatarData.error) {
          setError(avatarData.error);
          setIsSubmitting(false);
          return;
        }
      }

      // Then set username
      const data = await api.updateProfile(session.access_token, { username });
      if (data.error) {
        setError(data.error);
        setIsSubmitting(false);
        return;
      }

      await queryClient.invalidateQueries({ queryKey: ['profile'] });
    } catch {
      setError('Failed to update profile');
      setIsSubmitting(false);
    }
  };

  const isDisabled = isSubmitting || isTaken || !!usernameError || !username;

  return (
    <div className="fixed inset-0 z-[9999] flex items-center justify-center bg-black/60">
      <div className="bg-base-300 rounded-2xl p-8 w-full max-w-sm border border-custom shadow-xl">
        <div className="flex flex-col items-center gap-6">
          <h2 className="text-xl font-bold">Set Up Your Profile</h2>

          {/* Avatar */}
          <div
            className="relative w-24 h-24 cursor-pointer group"
            onClick={() => fileInputRef.current?.click()}
          >
            <div className="w-full h-full rounded-full overflow-hidden bg-base-200 ring-2 ring-base-100 flex items-center justify-center">
              {avatarPreview ? (
                <img
                  src={avatarPreview}
                  alt="Avatar preview"
                  className="w-full h-full object-cover"
                />
              ) : (
                <MdCameraAlt className="text-base-content/40" size={32} />
              )}
            </div>
            <input
              ref={fileInputRef}
              type="file"
              accept="image/jpeg,image/png,image/gif,image/webp"
              className="hidden"
              onChange={handleAvatarSelect}
            />
          </div>
          <span className="text-xs opacity-50 -mt-4">Avatar</span>

          {error && (
            <div className="alert alert-error w-full text-sm">
              <span>{error}</span>
            </div>
          )}

          <form onSubmit={handleSubmit} className="w-full space-y-4">
            <div className="form-control">
              <label className="label">
                <span className="label-text">Choose your username</span>
              </label>
              <input
                type="text"
                value={username}
                onChange={handleChange}
                className={`input input-bordered bg-base-200 w-full ${
                  isTaken || usernameError ? 'input-error' : ''
                }`}
                placeholder="Username"
                disabled={isSubmitting}
                autoFocus
              />
              {isTaken && (
                <label className="label">
                  <span className="label-text-alt text-error">This username is already taken</span>
                </label>
              )}
              {usernameError && (
                <label className="label">
                  <span className="label-text-alt text-error">{usernameError}</span>
                </label>
              )}
            </div>

            <Button
              type="submit"
              variant="primary"
              className="w-full font-semibold text-white"
              loading={isSubmitting}
              disabled={isDisabled}
            >
              Continue
            </Button>
          </form>
        </div>
      </div>
    </div>
  );
}
