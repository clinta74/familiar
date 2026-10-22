import type {
  AccountResponse,
  AccountRole,
  ChangePasswordRequest,
  DeviceSummary,
  LoginRequest,
  RegisterRequest,
  RegistrationStatusView,
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
  register: (username: string, email: string, password: string, invite?: string | null) =>
    api<Account>('/api/auth/register', { body: { username, email, password, invite: invite ?? null } satisfies RegisterRequest }),
  /** Whether anyone may register here, and whether an invite's code is good. */
  registration: (invite?: string | null) =>
    api<RegistrationStatusView>(`/api/auth/registration${invite ? `?invite=${encodeURIComponent(invite)}` : ''}`),
  logout: () => api<void>('/api/auth/logout', { method: 'POST' }),
  changePassword: (currentPassword: string, newPassword: string) =>
    api<void>('/api/auth/password', { body: { currentPassword, newPassword } satisfies ChangePasswordRequest }),
  devices: () => api<Device[]>('/api/auth/devices'),
  signOutDevice: (id: string) => api<void>(`/api/auth/devices/${id}`, { method: 'DELETE' }),
};
