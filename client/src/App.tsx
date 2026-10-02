import type { ReactNode } from 'react';
import { BrowserRouter, Navigate, Route, Routes } from 'react-router';
import { AuthProvider, useAuth } from './auth/AuthContext';
import { LoginPage } from './pages/LoginPage';
import { RegisterPage } from './pages/RegisterPage';
import { HomePage } from './pages/HomePage';
import { SettingsPage } from './pages/SettingsPage';
import { StudioPage } from './pages/StudioPage';

function RequireAccount({ children }: { children: ReactNode }) {
  const { account } = useAuth();
  if (account === undefined) return <div className="splash" aria-busy="true" />;
  if (account === null) return <Navigate to="/login" replace />;
  return children;
}

function SignedOutOnly({ children }: { children: ReactNode }) {
  const { account } = useAuth();
  if (account === undefined) return <div className="splash" aria-busy="true" />;
  if (account) return <Navigate to="/" replace />;
  return children;
}

export function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        <Routes>
          <Route path="/login" element={<SignedOutOnly><LoginPage /></SignedOutOnly>} />
          <Route path="/register" element={<SignedOutOnly><RegisterPage /></SignedOutOnly>} />
          <Route path="/" element={<RequireAccount><HomePage /></RequireAccount>} />
          <Route path="/settings" element={<RequireAccount><SettingsPage /></RequireAccount>} />
          <Route path="/studio" element={<RequireAccount><StudioPage /></RequireAccount>} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </BrowserRouter>
    </AuthProvider>
  );
}
