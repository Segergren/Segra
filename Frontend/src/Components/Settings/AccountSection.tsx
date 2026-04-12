import React, { useState } from 'react';
import { TriangleAlert, LogOut, Ellipsis, Mail } from 'lucide-react';
import { DiscordIcon } from '../icons/BrandIcons';
import CloudBadge from '../CloudBadge';
import { useAuth } from '../../Hooks/useAuth';
import { useProfile } from '../../Hooks/useUserProfile';
import Button from '../Button';

type AuthTab = 'login' | 'register';

export default function AccountSection() {
  const {
    user,
    session,
    isAuthenticating,
    authError,
    clearAuthError,
    login,
    register,
    loginWithDiscord,
    signOut,
  } = useAuth();
  const { data: profile, error: profileError } = useProfile();
  const [error, setError] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [tab, setTab] = useState<AuthTab>('login');
  const [confirmEmailMessage, setConfirmEmailMessage] = useState('');

  const handleDiscordLogin = () => {
    setError('');
    clearAuthError();
    loginWithDiscord();
  };

  const handleEmailLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    clearAuthError();
    await login(email, password);
  };

  const handleRegister = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    clearAuthError();
    setConfirmEmailMessage('');

    if (password !== confirmPassword) {
      setError('Passwords do not match');
      return;
    }

    if (password.length < 6) {
      setError('Password must be at least 6 characters');
      return;
    }

    const result = await register(email, password);
    if (result?.confirmEmail) {
      setConfirmEmailMessage('Check your email to confirm your account, then log in.');
    }
  };

  const handleLogout = async () => {
    signOut();
  };

  const displayError = error || authError;

  if (!session) {
    return (
      <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
        <h2 className="text-xl font-semibold mb-4 flex items-center gap-2">
          Authentication <CloudBadge />
        </h2>

        {displayError && (
          <div className="alert alert-error mb-4" role="alert">
            <TriangleAlert className="w-5 h-5" />
            <span>{displayError}</span>
          </div>
        )}

        {confirmEmailMessage && (
          <div className="alert alert-success mb-4" role="alert">
            <span>{confirmEmailMessage}</span>
          </div>
        )}

        <div className="bg-base-200 p-6 rounded-lg space-y-4 border border-custom">
          <Button
            variant="primary"
            className="w-full gap-2 font-semibold text-white border-custom hover:border-custom"
            onClick={handleDiscordLogin}
            loading={isAuthenticating}
          >
            <DiscordIcon className="w-5 h-5" />
            {isAuthenticating ? 'Connecting...' : 'Continue with Discord'}
          </Button>

          <div className="divider">Or Use Email</div>

          {/* Tab toggle */}
          <div className="tabs tabs-boxed justify-center">
            <button
              className={`tab ${tab === 'login' ? 'tab-active' : ''}`}
              onClick={() => {
                setTab('login');
                setError('');
                setConfirmEmailMessage('');
              }}
            >
              Login
            </button>
            <button
              className={`tab ${tab === 'register' ? 'tab-active' : ''}`}
              onClick={() => {
                setTab('register');
                setError('');
                setConfirmEmailMessage('');
              }}
            >
              Register
            </button>
          </div>

          {tab === 'login' ? (
            <form onSubmit={handleEmailLogin} className="space-y-4">
              <div className="form-control">
                <div className="mb-2">Email</div>
                <input
                  type="email"
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  className="input input-bordered bg-base-200 w-full"
                  disabled={isAuthenticating}
                  placeholder="example@example.com"
                  required
                />
              </div>

              <div className="form-control">
                <div className="mb-2">Password</div>
                <input
                  type="password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  className="input input-bordered bg-base-200 w-full"
                  disabled={isAuthenticating}
                  placeholder="********"
                  required
                />
              </div>

              <Button
                type="submit"
                variant="primary"
                className="w-full font-semibold text-white border-custom hover:border-custom"
                loading={isAuthenticating}
              >
                <Mail size={20} />
                Sign In with Email
              </Button>
            </form>
          ) : (
            <form onSubmit={handleRegister} className="space-y-4">
              <div className="form-control">
                <div className="mb-2">Email</div>
                <input
                  type="email"
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  className="input input-bordered bg-base-200 w-full"
                  disabled={isAuthenticating}
                  placeholder="example@example.com"
                  required
                />
              </div>

              <div className="form-control">
                <div className="mb-2">Password</div>
                <input
                  type="password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  className="input input-bordered bg-base-200 w-full"
                  disabled={isAuthenticating}
                  placeholder="********"
                  required
                />
              </div>

              <div className="form-control">
                <div className="mb-2">Confirm Password</div>
                <input
                  type="password"
                  value={confirmPassword}
                  onChange={(e) => setConfirmPassword(e.target.value)}
                  className="input input-bordered bg-base-200 w-full"
                  disabled={isAuthenticating}
                  placeholder="********"
                  required
                />
              </div>

              <Button
                type="submit"
                variant="primary"
                className="w-full font-semibold text-white border-custom hover:border-custom"
                loading={isAuthenticating}
              >
                <Mail size={20} />
                Create Account
              </Button>
            </form>
          )}
        </div>
      </div>
    );
  }

  return (
    <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
      <h2 className="text-xl mb-4 flex items-center gap-2">
        <span className="font-semibold">Account</span> <CloudBadge />
      </h2>

      <div className="bg-base-200 p-4 rounded-lg border border-custom">
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
          <div className="alert alert-error mt-3" role="alert" aria-live="assertive">
            <TriangleAlert className="w-5 h-5" />
            <div>
              <h3 className="font-bold">Profile load failed!</h3>
              <div className="text-xs">{profileError.message || 'Unknown error occurred'}</div>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
