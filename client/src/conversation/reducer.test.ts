import { describe, expect, it } from 'vitest';
import type { MessageView } from './events';
import { conversationReducer, initialConversation, type ConversationAction, type ConversationState } from './reducer';

// Ids sort by time, as the server's UUID v7 ids do: u1 is older than u2.
const user = (id: string, text: string): MessageView => ({
  id,
  role: 'user',
  text,
  emotion: null,
  intensity: null,
  interrupted: false,
  createdAt: '2026-10-01T00:00:00Z',
});

const run = (...actions: ConversationAction[]): ConversationState =>
  actions.reduce(conversationReducer, initialConversation);

describe('conversationReducer', () => {
  it('builds a streamed reply from deltas and settles it on end', () => {
    const state = run(
      { type: 'message', message: user('u1', 'Hi') },
      { type: 'replyStart', id: 'a1' },
      { type: 'replyDelta', id: 'a1', text: 'Hello' },
      { type: 'replyDelta', id: 'a1', text: ' there!' },
    );
    expect(state.messages[1]).toMatchObject({ text: 'Hello there!', streaming: true });

    const ended = conversationReducer(state, {
      type: 'replyEnd',
      end: { id: 'a1', text: 'Hello there!', emotion: 'joy', intensity: 0.8, interrupted: false },
    });
    expect(ended.messages[1]).toEqual({ id: 'a1', role: 'assistant', text: 'Hello there!', emotion: 'joy', interrupted: undefined });
  });

  it('ignores events it has already applied, as a reconnect replays them', () => {
    const once = run({ type: 'message', message: user('u1', 'Hi') }, { type: 'replyStart', id: 'a1' });
    const twice = run(
      { type: 'message', message: user('u1', 'Hi') },
      { type: 'replyStart', id: 'a1' },
      { type: 'message', message: user('u1', 'Hi') },
      { type: 'replyStart', id: 'a1' },
    );
    expect(twice.messages).toEqual(once.messages);
  });

  it('keeps live messages that history did not include yet', () => {
    const state = run(
      { type: 'message', message: user('u2', 'Live') },
      { type: 'history', messages: [user('u1', 'Old')] },
    );
    expect(state.messages.map((m) => m.text)).toEqual(['Old', 'Live']);
  });

  it('drops what a reload has moved past, rather than putting it at the bottom', () => {
    const state = run(
      { type: 'history', messages: [user('u1', 'First'), user('u2', 'Second')] },
      { type: 'history', messages: [user('u2', 'Second'), user('u3', 'Third')] },
    );
    expect(state.messages.map((m) => m.text)).toEqual(['Second', 'Third']);
  });

  it('marks the model unavailable while resting and available again once it speaks', () => {
    const resting = run({ type: 'state', state: 'resting' });
    expect(resting.modelAvailable).toBe(false);
    expect(conversationReducer(resting, { type: 'state', state: 'speaking' }).modelAvailable).toBe(true);
  });

  it('clears the transcript for a new conversation', () => {
    expect(run({ type: 'message', message: user('u1', 'Hi') }, { type: 'cleared' }).messages).toEqual([]);
  });
});
