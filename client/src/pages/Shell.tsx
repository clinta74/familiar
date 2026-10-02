import type { ReactNode } from 'react';
import { NavLink } from 'react-router';
import { useAuth } from '../auth/AuthContext';
import { FamiliarMark } from '../components/FamiliarMark';

export function Shell({ children }: { children: ReactNode }) {
  const { account, logout } = useAuth();

  return (
    <div className="shell">
      <header className="topbar">
        <NavLink to="/" className="brand">
          <FamiliarMark size={28} />
          <span>Familiar</span>
        </NavLink>
        <nav>
          <NavLink to="/studio">Studio</NavLink>
          <NavLink to="/settings">Settings</NavLink>
          <button type="button" className="link" onClick={() => void logout()}>
            Sign out {account?.username}
          </button>
        </nav>
      </header>
      <main className="content">{children}</main>
    </div>
  );
}
