import { useState, useEffect, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useQueryClient } from '@tanstack/react-query';
import { Camera } from 'lucide-react';
import { api } from '../lib/api';
import { useAuth } from '../Hooks/useAuth';
import Button from './Button';

// Returns an i18n key for the first failed rule, or an empty string when the name is valid.
function validateUsername(value: string): string {
  if (value.length < 3) return 'profile.setup.errors.usernameTooShort';
  if (value.length > 20) return 'profile.setup.errors.usernameTooLong';
  if (!/^[a-zA-Z0-9_-]+$/.test(value)) return 'profile.setup.errors.usernameInvalidChars';
  return '';
}

export default function SetupProfileModal() {
  const { t } = useTranslation('account');
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
      setError(t('profile.setup.errors.avatarType'));
      return;
    }

    if (file.size > 5 * 1024 * 1024) {
      setError(t('profile.setup.errors.avatarSize'));
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
      setError(t(validation));
      return;
    }
    if (isTaken) {
      setError(t('profile.setup.errors.usernameTaken'));
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
      setError(t('profile.setup.errors.updateFailed'));
      setIsSubmitting(false);
    }
  };

  const isDisabled = isSubmitting || isTaken || !!usernameError || !username;

  return (
    <div className="fixed inset-0 z-[9999] flex items-center justify-center bg-black/60">
      <div className="bg-base-300 rounded-2xl p-8 w-full max-w-sm border border-custom shadow-xl">
        <div className="flex flex-col items-center gap-6">
          <h2 className="text-xl font-bold">{t('profile.setup.title')}</h2>

          {/* Avatar */}
          <div
            className="relative w-24 h-24 cursor-pointer group"
            onClick={() => fileInputRef.current?.click()}
          >
            <div className="w-full h-full rounded-full overflow-hidden bg-base-200 ring-2 ring-base-100 flex items-center justify-center">
              {avatarPreview ? (
                <img
                  src={avatarPreview}
                  alt={t('profile.setup.avatarPreviewAlt')}
                  className="w-full h-full object-cover"
                />
              ) : (
                <Camera className="text-base-content/40" size={32} />
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
          <span className="text-xs opacity-50 -mt-4">{t('profile.setup.avatar')}</span>

          {error && (
            <div className="alert alert-error w-full text-sm">
              <span>{error}</span>
            </div>
          )}

          <form onSubmit={handleSubmit} className="w-full space-y-4">
            <div className="form-control">
              <label className="label">
                <span className="label-text">{t('profile.setup.usernameLabel')}</span>
              </label>
              <input
                type="text"
                value={username}
                onChange={handleChange}
                className={`input input-bordered bg-base-200 w-full ${
                  isTaken || usernameError ? 'input-error' : ''
                }`}
                placeholder={t('profile.setup.usernamePlaceholder')}
                disabled={isSubmitting}
                autoFocus
              />
              {isTaken && (
                <label className="label">
                  <span className="label-text-alt text-error">
                    {t('profile.setup.usernameTakenHint')}
                  </span>
                </label>
              )}
              {usernameError && (
                <label className="label">
                  <span className="label-text-alt text-error">{t(usernameError)}</span>
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
              {t('profile.setup.continue')}
            </Button>
          </form>
        </div>
      </div>
    </div>
  );
}
