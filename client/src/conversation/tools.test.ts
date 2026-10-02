import { describe, expect, it } from 'vitest';
import { countdown } from '../components/TimersPanel';
import { conversationReducer, initialConversation, type ConversationAction, type ConversationState } from './reducer';

const run = (...actions: ConversationAction[]): ConversationState =>
  actions.reduce(conversationReducer, initialConversation);

const started: ConversationAction = { type: 'toolStarted', event: { replyId: 'r1', callId: 'c1', tool: 'set_timer' } };
const finished: ConversationAction = {
  type: 'toolFinished',
  event: { replyId: 'r1', callId: 'c1', tool: 'set_timer', ok: true, summary: 'Timer: pasta, 10 minutes' },
};

describe('tool chips', () => {
  it('appear on the reply before it says anything, and survive its end', () => {
    const state = run(
      started,
      { type: 'replyStart', id: 'r1' },
      finished,
      { type: 'replyDelta', id: 'r1', text: 'Pasta timer is on!' },
      { type: 'replyEnd', end: { id: 'r1', text: 'Pasta timer is on!', emotion: 'joy', intensity: 0.8, interrupted: false } },
    );

    expect(state.messages).toHaveLength(1);
    expect(state.messages[0]?.text).toBe('Pasta timer is on!');
    expect(state.messages[0]?.tools).toEqual([
      { callId: 'c1', tool: 'set_timer', running: false, ok: true, summary: 'Timer: pasta, 10 minutes' },
    ]);
  });

  it('are idempotent across a replay', () => {
    const state = run(started, started, finished, finished);
    expect(state.messages[0]?.tools).toHaveLength(1);
  });

  it('settle a reply that only ran a tool once the familiar is idle', () => {
    const state = run(started, finished, { type: 'state', state: 'idle' });
    expect(state.messages[0]?.streaming).toBe(false);
  });

  it('clear a confirmation when it is answered anywhere', () => {
    const confirm: ConversationAction = {
      type: 'confirm',
      event: { replyId: 'r1', callId: 'c9', tool: 'send', description: 'Send it', expiresAt: '2026-10-01T00:01:00Z' },
    };
    expect(run(confirm).confirm?.callId).toBe('c9');
    expect(run(confirm, { type: 'confirmed', callId: 'other' }).confirm).not.toBeNull();
    expect(run(confirm, { type: 'confirmed', callId: 'c9' }).confirm).toBeNull();
  });
});

describe('countdown', () => {
  it.each([
    [0, '0:00'],
    [999, '0:01'],
    [65_000, '1:05'],
    [600_000, '10:00'],
    [3_725_000, '1:02:05'],
    [-5_000, '0:00'],
  ])('%i ms is %s', (ms, text) => expect(countdown(ms)).toBe(text));
});
