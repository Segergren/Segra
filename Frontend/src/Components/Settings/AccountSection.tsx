import React, { useEffect, useState } from 'react';
import { TriangleAlert, LogOut, Ellipsis, Mail, History } from 'lucide-react';
import { DiscordIcon } from '../icons/BrandIcons';
import discordLoading from '../../assets/discord-loading.webp';
import { useAuth } from '../../Hooks/useAuth';
import { useProfile } from '../../Hooks/useUserProfile';
import Button from '../Button';

type AuthTab = 'login' | 'register';

// Plain-language versions of the auth service's messages
const friendlyAuthError = (message: string) => {
  const m = message.toLowerCase();
  if (m.includes('invalid login credentials')) return 'Wrong email or password.';
  if (m.includes('email not confirmed'))
    return 'Confirm your email first. Check your inbox for the link.';
  if (m.includes('already registered'))
    return 'An account with this email already exists. Try signing in instead.';
  return message;
};

function Banner({ tone, children }: { tone: 'error' | 'success'; children: React.ReactNode }) {
  const colors =
    tone === 'error'
      ? 'border-error bg-error/10 text-error'
      : 'border-success bg-success/10 text-success';
  return (
    <div
      role="alert"
      className={`flex items-center gap-2 rounded-lg border px-3 py-2 text-sm ${colors}`}
    >
      {tone === 'error' && <TriangleAlert className="w-4 h-4 shrink-0" />}
      <div className="min-w-0">{children}</div>
    </div>
  );
}

export default function AccountSection() {
  const {
    user,
    session,
    isAuthenticating,
    isWaitingForDiscord,
    authError,
    clearAuthError,
    login,
    register,
    loginWithDiscord,
    cancelDiscordLogin,
    signOut,
  } = useAuth();
  const { data: profile, error: profileError } = useProfile();
  const [error, setError] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [tab, setTab] = useState<AuthTab>('login');
  const [confirmEmailMessage, setConfirmEmailMessage] = useState('');
  const [lastAttempt, setLastAttempt] = useState<'discord' | 'email'>('email');
  const [lastUsedMethod, setLastUsedMethod] = useState<'discord' | 'email' | null>(null);

  useEffect(() => {
    const lastMethod = localStorage.getItem('lastLoginMethod') as 'discord' | 'email' | null;
    setLastUsedMethod(lastMethod);
  }, []);

  const handleDiscordLogin = () => {
    setLastAttempt('discord');
    setError('');
    clearAuthError();
    localStorage.setItem('lastLoginMethod', 'discord');
    loginWithDiscord();
  };

  const handleEmailLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setLastAttempt('email');
    setError('');
    clearAuthError();
    localStorage.setItem('lastLoginMethod', 'email');
    await login(email, password);
  };

  const handleRegister = async (e: React.FormEvent) => {
    e.preventDefault();
    setLastAttempt('email');
    setError('');
    clearAuthError();
    setConfirmEmailMessage('');

    if (password !== confirmPassword) {
      setError('Passwords do not match.');
      return;
    }

    if (password.length < 6) {
      setError('Password must be at least 6 characters.');
      return;
    }

    const result = await register(email, password);
    if (result?.confirmEmail) {
      setConfirmEmailMessage('Check your email to confirm your account, then log in.');
    }
  };

  const switchTab = (next: AuthTab) => {
    setTab(next);
    setError('');
    clearAuthError();
    setConfirmEmailMessage('');
  };

  const handleLogout = async () => {
    signOut();
  };

  const displayError = error || (authError ? friendlyAuthError(authError) : '');
  const clearErrors = () => {
    setError('');
    clearAuthError();
  };
  const errorBanner = displayError ? <Banner tone="error">{displayError}</Banner> : null;

  if (!session) {
    return (
      <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400 space-y-4">
        <h2 className="text-xl font-semibold">Sign In</h2>
        {confirmEmailMessage && <Banner tone="success">{confirmEmailMessage}</Banner>}

        <div className="space-y-4">
          <div className="relative">
            {lastUsedMethod === 'discord' && (
              <div
                className={`absolute -top-3 right-2 bg-base-300 px-2 py-0.5 rounded-full border border-custom shadow-sm transition-all duration-300 ${isAuthenticating || isWaitingForDiscord ? 'opacity-0 scale-75' : 'opacity-100 scale-100'}`}
              >
                <div className="flex items-center gap-1 text-xs font-medium text-warning">
                  <History className="w-3 h-3" />
                  Last used
                </div>
              </div>
            )}
            <Button
              variant="primary"
              className="w-full gap-2 font-semibold"
              onClick={handleDiscordLogin}
              loading={isAuthenticating || isWaitingForDiscord}
              loadingIcon={
                isWaitingForDiscord ? (
                  // 30px, not 20px: the frame fits the full swirl, so the resting logo fills ~2/3.
                  <img
                    src={discordLoading}
                    alt=""
                    aria-hidden="true"
                    className="w-[30px] shrink-0"
                  />
                ) : undefined
              }
            >
              {!isWaitingForDiscord && <DiscordIcon className="w-5 h-5" />}
              {isWaitingForDiscord
                ? 'Waiting for your browser...'
                : isAuthenticating
                  ? 'Connecting...'
                  : 'Continue with Discord'}
            </Button>
          </div>

          {isWaitingForDiscord && (
            <p className="-mt-2 text-center text-xs opacity-70">
              Finish signing in with Discord in your browser.{' '}
              <button
                type="button"
                className="underline underline-offset-2 hover:text-base-content"
                onClick={cancelDiscordLogin}
              >
                Cancel
              </button>
            </p>
          )}

          {lastAttempt === 'discord' && errorBanner}

          <div className="divider my-0 text-sm opacity-70">Or Use Email</div>

          {tab === 'login' ? (
            <form onSubmit={handleEmailLogin} className="space-y-4">
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Email</span>
                </label>
                <input
                  type="email"
                  value={email}
                  onChange={(e) => {
                    setEmail(e.target.value);
                    clearErrors();
                  }}
                  className="input input-bordered bg-base-200 w-full outline-none focus:border-base-400"
                  disabled={isAuthenticating}
                  placeholder="example@example.com"
                  required
                />
              </div>

              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Password</span>
                </label>
                <input
                  type="password"
                  value={password}
                  onChange={(e) => {
                    setPassword(e.target.value);
                    clearErrors();
                  }}
                  className="input input-bordered bg-base-200 w-full outline-none focus:border-base-400"
                  disabled={isAuthenticating}
                  placeholder="********"
                  required
                />
              </div>

              {lastAttempt === 'email' && errorBanner}

              <div className="relative">
                {lastUsedMethod === 'email' && (
                  <div
                    className={`absolute -top-3 right-2 bg-base-300 px-2 py-0.5 rounded-full border border-custom shadow-sm transition-all duration-300 ${isAuthenticating ? 'opacity-0 scale-75' : 'opacity-100 scale-100'}`}
                  >
                    <div className="flex items-center gap-1 text-xs font-medium text-warning">
                      <History className="w-3 h-3" />
                      Last used
                    </div>
                  </div>
                )}
                <Button
                  type="submit"
                  variant="primary"
                  className="w-full font-semibold"
                  loading={isAuthenticating}
                >
                  <Mail size={20} />
                  Sign In with Email
                </Button>
              </div>

              <p className="text-center text-sm">
                <span className="opacity-70">Don&apos;t have an account?</span>{' '}
                <button
                  type="button"
                  className="text-primary font-medium hover:underline underline-offset-2 cursor-pointer"
                  onClick={() => switchTab('register')}
                >
                  Create one
                </button>
              </p>
            </form>
          ) : (
            <form onSubmit={handleRegister} className="space-y-4">
              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Email</span>
                </label>
                <input
                  type="email"
                  value={email}
                  onChange={(e) => {
                    setEmail(e.target.value);
                    clearErrors();
                  }}
                  className="input input-bordered bg-base-200 w-full outline-none focus:border-base-400"
                  disabled={isAuthenticating}
                  placeholder="example@example.com"
                  required
                />
              </div>

              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Password</span>
                </label>
                <input
                  type="password"
                  value={password}
                  onChange={(e) => {
                    setPassword(e.target.value);
                    clearErrors();
                  }}
                  className="input input-bordered bg-base-200 w-full outline-none focus:border-base-400"
                  disabled={isAuthenticating}
                  placeholder="********"
                  required
                />
              </div>

              <div className="form-control">
                <label className="label">
                  <span className="label-text text-base-content">Confirm Password</span>
                </label>
                <input
                  type="password"
                  value={confirmPassword}
                  onChange={(e) => {
                    setConfirmPassword(e.target.value);
                    clearErrors();
                  }}
                  className="input input-bordered bg-base-200 w-full outline-none focus:border-base-400"
                  disabled={isAuthenticating}
                  placeholder="********"
                  required
                />
              </div>

              {lastAttempt === 'email' && errorBanner}

              <Button
                type="submit"
                variant="primary"
                className="w-full font-semibold"
                loading={isAuthenticating}
              >
                <Mail size={20} />
                Create Account
              </Button>

              <p className="text-center text-sm">
                <span className="opacity-70">Already have an account?</span>{' '}
                <button
                  type="button"
                  className="text-primary font-medium hover:underline underline-offset-2 cursor-pointer"
                  onClick={() => switchTab('login')}
                >
                  Sign in
                </button>
              </p>
            </form>
          )}
        </div>
      </div>
    );
  }

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-base-400">
      <div className="flex items-center justify-between flex-wrap gap-4">
        <div className="flex items-center gap-4 min-w-0">
          {/* Avatar Container */}
          <div className="relative w-16 h-16">
            <div className="w-full h-full rounded-full overflow-hidden bg-base-200 ring-2 ring-base-300">
              {profile?.avatar_url ? (
                <img
                  src={profile.avatar_url}
                  alt={`${profile.username}'s avatar`}
                  className="w-full h-full object-cover"
                  onError={(e) => {
                    (e.target as HTMLImageElement).src = '/default-avatar.png';
                  }}
                />
              ) : (
                <div
                  className="w-full h-full bg-base-300 flex items-center justify-center"
                  aria-hidden="true"
                >
                  <span className="text-2xl"></span>
                </div>
              )}
            </div>
          </div>

          {/* Profile Info */}
          <div className="min-w-0 flex-1">
            <h3 className="font-bold truncate">
              {profile?.username && !profile.username.startsWith('user_') ? (
                profile.username
              ) : (
                <div className="skeleton h-[24px] w-24"></div>
              )}
            </h3>
            <p className="text-sm opacity-70 truncate">{user?.email || 'Authenticated User'}</p>
          </div>

          {/* More Options Dropdown */}
          <div className="dropdown">
            <label
              tabIndex={0}
              className="btn btn-ghost btn-sm btn-circle hover:bg-white/10 active:bg-white/10"
            >
              <Ellipsis size={24} />
            </label>
            <ul
              tabIndex={0}
              className="dropdown-content menu bg-base-300 border border-base-400 rounded-box z-999 w-52 p-2"
            >
              <li>
                <Button
                  variant="menuDanger"
                  onClick={() => {
                    (document.activeElement as HTMLElement).blur();
                    handleLogout();
                  }}
                >
                  <LogOut size={20} />
                  <span>Logout</span>
                </Button>
              </li>
            </ul>
          </div>
        </div>
      </div>

      {/* Error State */}
      {profileError && (
        <div className="mt-3">
          <Banner tone="error">
            Couldn&apos;t load your profile. {profileError.message || 'Unknown error.'}
          </Banner>
        </div>
      )}
    </div>
  );
}
