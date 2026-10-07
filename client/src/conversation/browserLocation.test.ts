import { describe, expect, it } from 'vitest';
import type { ToolInvokeEvent } from '../protocol/generated';
import { answerLocation, rounded } from './browserLocation';

const invoke: ToolInvokeEvent = {
  callId: 'c1',
  connectionId: 'tab',
  tool: 'location.read',
  arguments: {},
  expiresAt: '2099-01-01T00:00:00Z',
};

const at = (latitude: number, longitude: number) => () =>
  Promise.resolve({ coords: { latitude, longitude } } as GeolocationPosition);

describe('browser location', () => {
  it('rounds to about a kilometre', () => {
    expect(rounded(42.513763, -83.077597)).toBe('42.51,-83.08');
  });

  it('answers with rounded coordinates and tells the familiar not to read them out', async () => {
    const answer = await answerLocation(invoke, at(53.8197, -1.5763));
    expect(answer).toMatchObject({ callId: 'c1', ok: true, result: { coordinates: '53.82,-1.58' } });
    expect(String(answer.result?.note)).toContain("don't read them out");
  });

  it('says so when the browser is not allowed', async () => {
    const answer = await answerLocation(invoke, () => Promise.reject({ code: 1 }));
    expect(answer.ok).toBe(false);
    expect(answer.error).toContain('site settings');
  });

  it('says so when it cannot tell', async () => {
    const answer = await answerLocation(invoke, () => Promise.reject({ code: 3 }));
    expect(answer.error).toContain("couldn't tell");
  });
});
