import React, { useState } from 'react';
import { FaDiscord } from 'react-icons/fa';
import { MdWarning, MdOutlineLogout, MdOutlineMoreHoriz } from 'react-icons/md';
import CloudBadge from '../CloudBadge';
import { supabase } from '../../lib/supabase/client';
import { useAuth } from '../../Hooks/useAuth';
import { useProfile } from '../../Hooks/useUserProfile';

export default function AccountSection() {
  const { session, isAuthenticating, clearAuthError, signOut } = useAuth();
  const { data: profile, error: profileError } = useProfile();
  const [error, setError] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');

  const handleDiscordLogin = async () => {
    setError('');
    clearAuthError();

    const { error } = await supabase.auth.signInWithOAuth({
      provider: 'discord',
      options: {
        redirectTo: window.location.origin,
      },
    });

    if (error) {
      setError(error.message);
    }
  };

  const handleEmailLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    clearAuthError();

    const { error } = await supabase.auth.signInWithPassword({
      email,
      password,
    });

    if (error) {
      setError(error.message);
    }
  };

  const handleLogout = async () => {
    await signOut();
  };

  if (!session) {
    return (
      <div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
        <h2 className="text-xl font-semibold mb-4 flex items-center gap-2">
          Authentication <CloudBadge />
        </h2>

        {error && (
          <div className="alert alert-error mb-4" role="alert">
            <MdWarning className="w-5 h-5" />
            <span>{error}</span>
          </div>
        )}

        <div className="bg-base-200 p-6 rounded-lg space-y-4 border border-custom">
          <button
            onClick={handleDiscordLogin}
            disabled={isAuthenticating}
            className={`btn btn-secondary w-full gap-2 font-semibold text-white border border-custom hover:border-custom ${isAuthenticating ? 'btn-loading' : ''}`}
          >
            <FaDiscord className="w-5 h-5" />
            {isAuthenticating ? 'Connecting...' : 'Continue with Discord'}
          </button>

          <div className="divider">Or Use Email</div>

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

            <button
              type="submit"
              disabled={isAuthenticating}
              className={`btn btn-secondary w-full font-semibold text-white border border-custom hover:border-custom ${isAuthenticating ? 'btn-loading' : ''}`}
            >
              Sign In With Email
            </button>
          </form>
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
                {profile?.username ? (
                  profile.username
                ) : (
                  <div className="skeleton h-[24px] w-24"></div>
                )}
              </h3>
              <p className="text-sm opacity-70 truncate">
                {session?.user?.email || 'Authenticated User'}
              </p>
            </div>

            {/* More Options Dropdown */}
            <div className="dropdown">
              <label
                tabIndex={0}
                className="btn btn-ghost btn-sm btn-circle hover:bg-white/10 active:bg-white/10"
              >
                <MdOutlineMoreHoriz size="28" />
              </label>
              <ul
                tabIndex={0}
                className="dropdown-content menu bg-base-300 border border-base-400 rounded-box z-999 w-52 p-2"
              >
                <li>
                  <a
                    className="flex w-full items-center gap-2 px-4 py-3 text-error hover:bg-error/10 active:bg-error/20 rounded-lg transition-all duration-200 hover:pl-5 outline-none"
                    onClick={() => {
                      (document.activeElement as HTMLElement).blur();
                      handleLogout();
                    }}
                  >
                    <MdOutlineLogout size="20" />
                    <span>Logout</span>
                  </a>
                </li>
              </ul>
            </div>
          </div>
        </div>

        {/* Error State */}
        {profileError && (
          <div className="alert alert-error mt-3" role="alert" aria-live="assertive">
            <MdWarning className="w-5 h-5" />
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
