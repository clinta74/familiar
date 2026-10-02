import { api } from './client';

export type AccountRole = 'user' | 'admin';

export interface Account {
  id: string;
  username: string;
  email: string;
  role: AccountRole;
}

export interface Device {
  id: string;
  name: string;
  createdAt: string;
  expiresAt: string;
  lastUsedAt: string | null;
  isCurrent: boolean;
}

export const authApi = {
  me: () => api<Account>('/api/auth/me'),
  login: (username: string, password: string) => api<Account>('/api/auth/login', { body: { username, password } }),
  register: (username: string, email: string, password: string) =>
    api<Account>('/api/auth/register', { body: { username, email, password } }),
  logout: () => api<void>('/api/auth/logout', { method: 'POST' }),
  changePassword: (currentPassword: string, newPassword: string) =>
    api<void>('/api/auth/password', { body: { currentPassword, newPassword } }),
  devices: () => api<Device[]>('/api/auth/devices'),
  signOutDevice: (id: string) => api<void>(`/api/auth/devices/${id}`, { method: 'DELETE' }),
};
