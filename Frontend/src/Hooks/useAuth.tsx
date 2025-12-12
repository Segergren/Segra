import { useState, useEffect, createContext, useContext, ReactNode } from 'react';
import { Session, User } from '@supabase/supabase-js';
import { supabase } from '../lib/supabase/client';
import { sendMessageToBackend } from '../Utils/MessageUtils';

// Create a context to store authentication state
interface AuthContextType {
  user: User | null;
  session: Session | null;
  authError: string | null;
  isAuthenticating: boolean;
  clearAuthError: () => void;
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthContextType | null>(null);

// Provider component that wraps the app
export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(null);
  const [user, setUser] = useState<User | null>(null);
  const [authError, setAuthError] = useState<string | null>(null);
  const [isAuthenticating, setIsAuthenticating] = useState(false);
  const [hasInitialized, setHasInitialized] = useState(false);

  // Custom signOut function that ensures UI is updated
  const handleSignOut = async () => {
    try {
      // Sign out from Supabase
      const { error } = await supabase.auth.signOut({ scope: 'local' });

      if (error) throw error;

      // Manually update state since local signOut might not trigger the event
      setSession(null);
      setUser(null);

      // Notify backend about logout
      sendMessageToBackend('Logout');

      console.log('User signed out manually');
    } catch (err) {
      console.error('Sign out error:', err);
      setAuthError(err instanceof Error ? err.message : 'Sign out failed');
    }
  };

  // Handle OAuth callback
  useEffect(() => {
    const handleAuthCallback = async () => {
      try {
        const urlParams = new URLSearchParams(window.location.search);
        const code = urlParams.get('code');

        if (code) {
          setIsAuthenticating(true);
          const {
            data: { session },
            error,
          } = await supabase.auth.exchangeCodeForSession(code);

          if (error) throw error;

          const { error: profileError } = await supabase
            .from('profiles')
            .select('username, avatar_url')
            .eq('id', session?.user?.id)
            .single();

          if (profileError && profileError.code === 'PGRST116') {
            console.error('Profile error, user may not have a profile:', profileError);
            handleSignOut();
            setAuthError('Please register at Segra.tv before logging into the app.');
          }

          // Clean URL after successful login
          window.history.replaceState({}, document.title, window.location.pathname);
        }
      } catch (err) {
        setAuthError(err instanceof Error ? err.message : 'Authentication failed');
      } finally {
        setIsAuthenticating(false);
      }
    };

    handleAuthCallback();
  }, []);

  // Listen for auth state changes
  useEffect(() => {
    const { data: authListener } = supabase.auth.onAuthStateChange((event, currentSession) => {
      console.log('Auth state changed:', event, !!currentSession);
      setSession(currentSession);
      setUser(currentSession?.user ?? null);

      if (event === 'SIGNED_IN' && currentSession) {
        console.log('User signed in, sending login to backend');
        sendMessageToBackend('Login', {
          accessToken: currentSession.access_token,
          refreshToken: currentSession.refresh_token,
        });
      } else if (event === 'SIGNED_OUT') {
        console.log('User signed out, sending logout to backend');
        sendMessageToBackend('Logout');
      }
    });

    return () => {
      authListener?.subscription.unsubscribe();
    };
  }, []);

  // Get initial session once on mount
  useEffect(() => {
    if (hasInitialized) return;

    supabase.auth.getSession().then(({ data: { session: initialSession } }) => {
      console.log('Initial session retrieved:', !!initialSession);
      setSession(initialSession);
      setUser(initialSession?.user ?? null);

      if (initialSession) {
        console.log('Sending initial login credentials to backend');
        sendMessageToBackend('Login', {
          accessToken: initialSession.access_token,
          refreshToken: initialSession.refresh_token,
        });
      }

      setHasInitialized(true);
    });
  }, [hasInitialized]);

  const value = {
    user,
    session,
    authError,
    isAuthenticating,
    clearAuthError: () => setAuthError(null),
    signOut: handleSignOut,
  };

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

// Hook for components to get authentication context
export function useAuth() {
  const context = useContext(AuthContext);
  if (context === null) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return context;
}
