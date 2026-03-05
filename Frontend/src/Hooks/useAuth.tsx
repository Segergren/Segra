import {
  useState,
  useEffect,
  useRef,
  createContext,
  useContext,
  useCallback,
  ReactNode,
} from 'react';
import { api } from '../lib/api';
import { sendMessageToBackend } from '../Utils/MessageUtils';

interface AuthUser {
  id: string;
  email: string;
  user_metadata?: Record<string, unknown>;
}

interface AuthSession {
  access_token: string;
  refresh_token: string;
}

interface AuthContextType {
  user: AuthUser | null;
  session: AuthSession | null;
  authError: string | null;
  isAuthenticating: boolean;
  clearAuthError: () => void;
  login: (email: string, password: string) => Promise<void>;
  register: (email: string, password: string) => Promise<{ confirmEmail?: boolean }>;
  loginWithDiscord: () => void;
  signOut: () => void;
}

const AuthContext = createContext<AuthContextType | null>(null);

// Sign-out callbacks that external code can register (e.g. queryClient.clear())
const signOutCallbacks: Array<() => void> = [];
export function onSignOut(cb: () => void) {
  signOutCallbacks.push(cb);
}

function parseJwt(token: string): Record<string, unknown> | null {
  try {
    const payload = token.split('.')[1];
    const json = atob(payload.replace(/-/g, '+').replace(/_/g, '/'));
    return JSON.parse(json);
  } catch {
    return null;
  }
}

function getJwtExp(token: string): number {
  return (parseJwt(token)?.exp as number) ?? 0;
}

function getUserFromJwt(token: string): AuthUser | null {
  const data = parseJwt(token);
  if (!data?.sub) return null;
  return { id: data.sub as string, email: (data.email as string) ?? '' };
}

function migrateOldSupabaseSession(): AuthSession | null {
  try {
    const raw = localStorage.getItem('sb-supabase-auth-token');
    if (!raw) return null;
    const data = JSON.parse(raw);
    if (data.access_token && data.refresh_token) {
      const session = { access_token: data.access_token, refresh_token: data.refresh_token };
      saveSession(session);
      localStorage.removeItem('sb-supabase-auth-token');
      return session;
    }
  } catch {
    /* ignore */
  }
  return null;
}

function loadSession(): AuthSession | null {
  const access_token = localStorage.getItem('segra_access_token');
  const refresh_token = localStorage.getItem('segra_refresh_token');
  if (access_token && refresh_token) return { access_token, refresh_token };
  return migrateOldSupabaseSession();
}

function saveSession(session: AuthSession) {
  localStorage.setItem('segra_access_token', session.access_token);
  localStorage.setItem('segra_refresh_token', session.refresh_token);
}

function clearAuth() {
  localStorage.removeItem('segra_access_token');
  localStorage.removeItem('segra_refresh_token');
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<AuthSession | null>(null);
  const [user, setUser] = useState<AuthUser | null>(null);
  const [authError, setAuthError] = useState<string | null>(null);
  const [isAuthenticating, setIsAuthenticating] = useState(false);
  const refreshTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const sessionGenRef = useRef(0);

  const handleSignOut = useCallback(() => {
    sessionGenRef.current++;
    if (refreshTimerRef.current) {
      clearTimeout(refreshTimerRef.current);
      refreshTimerRef.current = null;
    }
    setSession(null);
    setUser(null);
    clearAuth();
    sendMessageToBackend('Logout');
    signOutCallbacks.forEach((cb) => cb());
  }, []);

  // Refresh the token and update all state + push to backend
  const refreshSession = useCallback(
    async (currentSession: AuthSession) => {
      const gen = sessionGenRef.current;
      try {
        const data = await api.refreshToken(currentSession.refresh_token);
        if (gen !== sessionGenRef.current) return; // session changed, discard stale refresh
        if (data.error) {
          console.error('Token refresh failed:', data.error);
          handleSignOut();
          return;
        }
        const newSession: AuthSession = {
          access_token: data.access_token,
          refresh_token: data.refresh_token,
        };
        const newUser = getUserFromJwt(newSession.access_token);
        setSession(newSession);
        setUser(newUser);
        saveSession(newSession);
        sendMessageToBackend('Login', {
          accessToken: newSession.access_token,
          refreshToken: newSession.refresh_token,
        });
      } catch {
        if (gen !== sessionGenRef.current) return;
        console.error('Token refresh failed');
        handleSignOut();
      }
    },
    [handleSignOut],
  );

  // Schedule auto-refresh whenever session changes
  useEffect(() => {
    if (refreshTimerRef.current) {
      clearTimeout(refreshTimerRef.current);
      refreshTimerRef.current = null;
    }
    if (!session) return;

    const exp = getJwtExp(session.access_token);
    // Refresh 30 seconds before expiry
    const msUntilRefresh = (exp - 30) * 1000 - Date.now();

    if (msUntilRefresh <= 0) {
      // Already expired, refresh now
      refreshSession(session);
    } else {
      refreshTimerRef.current = setTimeout(() => refreshSession(session), msUntilRefresh);
    }

    return () => {
      if (refreshTimerRef.current) clearTimeout(refreshTimerRef.current);
    };
  }, [session, refreshSession]);

  // On mount: check URL params for Discord callback tokens, else load from localStorage
  useEffect(() => {
    const urlParams = new URLSearchParams(window.location.search);
    const accessToken = urlParams.get('access_token');
    const refreshToken = urlParams.get('refresh_token');

    if (accessToken && refreshToken) {
      // Discord OAuth callback with tokens
      const newSession = { access_token: accessToken, refresh_token: refreshToken };
      const authUser = getUserFromJwt(accessToken);

      if (!authUser) {
        setAuthError('Failed to authenticate. Please try again.');
        return;
      }

      setSession(newSession);
      setUser(authUser);
      saveSession(newSession);
      sendMessageToBackend('Login', {
        accessToken: newSession.access_token,
        refreshToken: newSession.refresh_token,
      });

      // Clean URL
      window.history.replaceState({}, document.title, window.location.pathname);
      return;
    }

    // Load from localStorage
    const stored = loadSession();
    if (stored) {
      const storedUser = getUserFromJwt(stored.access_token);
      if (storedUser) {
        setSession(stored);
        setUser(storedUser);
        sendMessageToBackend('Login', {
          accessToken: stored.access_token,
          refreshToken: stored.refresh_token,
        });
      }
    }
  }, []);

  const login = useCallback(async (email: string, password: string) => {
    sessionGenRef.current++;
    setIsAuthenticating(true);
    setAuthError(null);
    try {
      const data = await api.login(email, password);
      if (data.error) {
        setAuthError(data.error);
        return;
      }
      if (!data.session) {
        setAuthError('Login failed - no session returned');
        return;
      }
      const newSession: AuthSession = data.session;
      const authUser = getUserFromJwt(newSession.access_token);
      setSession(newSession);
      setUser(authUser);
      saveSession(newSession);
      sendMessageToBackend('Login', {
        accessToken: newSession.access_token,
        refreshToken: newSession.refresh_token,
      });
    } catch {
      setAuthError('Login failed. Please try again.');
    } finally {
      setIsAuthenticating(false);
    }
  }, []);

  const register = useCallback(async (email: string, password: string) => {
    sessionGenRef.current++;
    setIsAuthenticating(true);
    setAuthError(null);
    try {
      const data = await api.register(email, password);
      if (data.error) {
        setAuthError(data.error);
        return { confirmEmail: false };
      }
      if (data.confirmEmail) {
        return { confirmEmail: true };
      }
      // Auto-confirmed: log in automatically
      if (data.session) {
        const newSession: AuthSession = data.session;
        const authUser = getUserFromJwt(newSession.access_token);
        setSession(newSession);
        setUser(authUser);
        saveSession(newSession);
        sendMessageToBackend('Login', {
          accessToken: newSession.access_token,
          refreshToken: newSession.refresh_token,
        });
      }
      return { confirmEmail: false };
    } catch {
      setAuthError('Registration failed. Please try again.');
      return { confirmEmail: false };
    } finally {
      setIsAuthenticating(false);
    }
  }, []);

  const loginWithDiscord = useCallback(() => {
    const desktopRedirect = window.location.origin;
    window.location.href = api.getDiscordLoginUrl(desktopRedirect);
  }, []);

  const value: AuthContextType = {
    user,
    session,
    authError,
    isAuthenticating,
    clearAuthError: () => setAuthError(null),
    login,
    register,
    loginWithDiscord,
    signOut: handleSignOut,
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (context === null) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return context;
}
