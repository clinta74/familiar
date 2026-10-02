import { api } from '../api/client';
import type { MessageView } from './events';

const timeZone = () => Intl.DateTimeFormat().resolvedOptions().timeZone;

export const conversationApi = {
  history: (limit = 50) => api<MessageView[]>(`/api/conversation/messages?limit=${limit}`),
  send: (text: string) => api<MessageView>('/api/conversation/messages', { body: { text, timeZone: timeZone() } }),
  interrupt: () => api<void>('/api/conversation/interrupt', { method: 'POST' }),
  startOver: () => api<void>('/api/conversation/new', { method: 'POST' }),
};
