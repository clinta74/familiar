import { api, errorFrom } from '../api/client';
import type { ConfirmRequest, InterruptRequest, SendMessageRequest, ToolResultRequest } from '../protocol/generated';
import type { MessageView } from './events';

const timeZone = () => Intl.DateTimeFormat().resolvedOptions().timeZone;

export const conversationApi = {
  history: (limit = 50) => api<MessageView[]>(`/api/conversation/messages?limit=${limit}`),

  /** A typed message. `speak` asks for the reply to be spoken as well. */
  send: (text: string, speak: boolean) =>
    api<MessageView>('/api/conversation/messages', { body: { text, timeZone: timeZone(), speak } satisfies SendMessageRequest }),

  /**
   * A spoken message: the recording itself is the body. Resolves to the transcribed message, or
   * null if nothing was heard.
   */
  sendSpoken: async (recording: Blob): Promise<MessageView | null> => {
    const response = await fetch(`/api/conversation/audio?timeZone=${encodeURIComponent(timeZone())}`, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': recording.type || 'audio/webm' },
      body: recording,
    });
    if (!response.ok) throw await errorFrom(response);
    return response.status === 204 ? null : ((await response.json()) as MessageView);
  },

  /** Stop. `heard` cuts the stored reply to what the user heard of it before talking over it. */
  interrupt: (heard?: { replyId: string; text: string }) =>
    api<void>('/api/conversation/interrupt', heard
      ? { body: { replyId: heard.replyId, heard: heard.text } satisfies InterruptRequest }
      : { method: 'POST' }),
  startOver: () => api<void>('/api/conversation/new', { method: 'POST' }),

  cancelJob: (id: string) => api<void>(`/api/jobs/${encodeURIComponent(id)}`, { method: 'DELETE' }),

  /** What this browser did when the server asked it to (`tool.invoke`): where it is. */
  toolResult: (result: ToolResultRequest) => api<void>('/api/conversation/tool-result', { body: result }),

  /** Allow, or don't, a tool the familiar asked about. */
  confirm: (callId: string, approve: boolean) => api<void>('/api/conversation/confirm', { body: { callId, approve } satisfies ConfirmRequest }),
};
