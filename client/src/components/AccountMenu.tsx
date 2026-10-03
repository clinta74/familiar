import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react';
import { useNavigate } from 'react-router';
import { useAuth } from '../auth/AuthContext';

interface Item {
  label: string;
  /** Where it goes, or what it does. */
  to?: string;
  act?: () => void;
}

/**
 * The account button at the right of the top bar: who's signed in, and a menu with Settings, this
 * account's devices, Users (admins), and Sign out. The menu-button pattern: arrows move between the
 * items, Escape closes it and puts focus back on the button, and so does a click anywhere else.
 */
export function AccountMenu() {
  const { account, logout } = useAuth();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const items = useRef<(HTMLButtonElement | null)[]>([]);
  const menuId = useId();

  const entries: Item[] = [
    { label: 'Settings', to: '/settings' },
    { label: 'Devices', to: '/settings#devices' },
    ...(account?.role === 'admin' ? [{ label: 'Users', to: '/admin' }] : []),
    { label: 'Sign out', act: () => void logout() },
  ];

  // Closes on a click or tap anywhere outside it.
  useEffect(() => {
    if (!open) return;
    const away = (event: PointerEvent) => {
      if (!root.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener('pointerdown', away);
    return () => document.removeEventListener('pointerdown', away);
  }, [open]);

  // Opening puts focus on the first item.
  useEffect(() => {
    if (open) items.current[0]?.focus();
  }, [open]);

  function close(refocus = true) {
    setOpen(false);
    if (refocus) button.current?.focus();
  }

  function choose(item: Item) {
    close(false);
    if (item.to) navigate(item.to);
    item.act?.();
  }

  function onMenuKey(event: KeyboardEvent) {
    const list = items.current.filter((item): item is HTMLButtonElement => !!item);
    const at = list.indexOf(document.activeElement as HTMLButtonElement);
    const focus = (i: number) => list[(i + list.length) % list.length]?.focus();
    switch (event.key) {
      case 'ArrowDown': focus(at + 1); break;
      case 'ArrowUp': focus(at - 1); break;
      case 'Home': focus(0); break;
      case 'End': focus(list.length - 1); break;
      case 'Escape': close(); break;
      case 'Tab': close(false); return;
      default: return;
    }
    event.preventDefault();
  }

  function onButtonKey(event: KeyboardEvent) {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      setOpen(true);
    }
  }

  if (!account) return null;
  const initial = account.username.slice(0, 1).toUpperCase();

  return (
    <div className="account-menu" ref={root}>
      <button
        ref={button}
        type="button"
        className="account-button"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        aria-label={`Account: ${account.username}`}
        onClick={() => setOpen((o) => !o)}
        onKeyDown={onButtonKey}
      >
        <span className="account-initial" aria-hidden="true">{initial}</span>
        <span className="account-name">{account.username}</span>
        <svg className="account-caret" width="10" height="10" viewBox="0 0 10 10" aria-hidden="true">
          <path d="M2 3.5l3 3 3-3" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>
      {open && (
        <div className="account-popup" id={menuId} role="menu" aria-label="Account" onKeyDown={onMenuKey}>
          <div className="account-who" role="presentation">
            <strong>{account.username}</strong>
            <span className="muted">{account.email}</span>
            {account.role === 'admin' && <span className="muted">Administrator</span>}
          </div>
          {entries.map((item, i) => (
            <button
              key={item.label}
              ref={(el) => {
                items.current[i] = el;
              }}
              type="button"
              role="menuitem"
              tabIndex={-1}
              className={item.act ? 'account-item sign-out' : 'account-item'}
              onClick={() => choose(item)}
            >
              {item.label}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
