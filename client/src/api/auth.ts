import type {
  AccountResponse,
  AccountRole,
  ChangePasswordRequest,
  DeviceSummary,
  LoginRequest,
  RegisterRequest,
} from '../protocol/generated';
import { api } from './client';

export type { AccountRole };

/** The signed-in account. */
export type Account = AccountResponse;

/** A signed-in phone. */
export type Device = DeviceSummary;

export const authApi = {
  me: () => api<Account>('/api/auth/me'),
  login: (username: string, password: string) => api<Account>('/api/auth/login', { body: { username, password } satisfies LoginRequest }),
  register: (username: string, email: string, password: string) =>
    api<Account>('/api/auth/register', { body: { username, email, password } satisfies RegisterRequest }),
  logout: () => api<void>('/api/auth/logout', { method: 'POST' }),
  changePassword: (currentPassword: string, newPassword: string) =>
    api<void>('/api/auth/password', { body: { currentPassword, newPassword } satisfies ChangePasswordRequest }),
  devices: () => api<Device[]>('/api/auth/devices'),
  signOutDevice: (id: string) => api<void>(`/api/auth/devices/${id}`, { method: 'DELETE' }),
};
