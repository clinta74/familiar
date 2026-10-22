import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { authApi, type Account } from '../api/auth';

interface AuthState {
  /** undefined while the session is being checked; null when signed out. */
  account: Account | null | undefined;
  login: (username: string, password: string) => Promise<void>;
  register: (username: string, email: string, password: string, invite?: string | null) => Promise<void>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [account, setAccount] = useState<Account | null | undefined>(undefined);

  useEffect(() => {
    let cancelled = false;
    authApi
      .me()
      .then((me) => !cancelled && setAccount(me))
      // 401 is the normal signed-out answer. Anything else (server down) also lands on the login
      // page, where signing in will surface the real error.
      .catch(() => !cancelled && setAccount(null));
    return () => {
      cancelled = true;
    };
  }, []);

  const login = useCallback(async (username: string, password: string) => {
    setAccount(await authApi.login(username, password));
  }, []);

  const register = useCallback(async (username: string, email: string, password: string, invite?: string | null) => {
    setAccount(await authApi.register(username, email, password, invite));
  }, []);

  const logout = useCallback(async () => {
    await authApi.logout();
    setAccount(null);
  }, []);

  const value = useMemo(() => ({ account, login, register, logout }), [account, login, register, logout]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const state = useContext(AuthContext);
  if (!state) throw new Error('useAuth outside AuthProvider');
  return state;
}
