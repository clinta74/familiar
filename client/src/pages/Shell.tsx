import type { ReactNode } from 'react';
import { NavLink } from 'react-router';
import { AccountMenu } from '../components/AccountMenu';
import { FamiliarMark } from '../components/FamiliarMark';

export function Shell({ children }: { children: ReactNode }) {
  return (
    <div className="shell">
      <header className="topbar">
        <NavLink to="/" className="brand" aria-label="Familiar - chat">
          <FamiliarMark size={28} />
          <span>Familiar</span>
        </NavLink>
        <nav aria-label="Main">
          <NavLink to="/" end>Chat</NavLink>
          <NavLink to="/studio">Studio</NavLink>
          <NavLink to="/settings">Settings</NavLink>
        </nav>
        <AccountMenu />
      </header>
      <main className="content">{children}</main>
    </div>
  );
}
