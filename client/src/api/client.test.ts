import { afterEach, describe, expect, it, vi } from 'vitest';
import { api, ApiError } from './client';

function respond(status: number, body?: unknown) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      body === undefined ? new Response(null, { status }) : new Response(JSON.stringify(body), { status })),
  );
}

afterEach(() => vi.unstubAllGlobals());

describe('api', () => {
  it('returns the parsed body', async () => {
    respond(200, { username: 'alice' });
    await expect(api('/api/auth/me')).resolves.toEqual({ username: 'alice' });
  });

  it('resolves 204 to undefined', async () => {
    respond(204);
    await expect(api('/api/auth/logout', { method: 'POST' })).resolves.toBeUndefined();
  });

  it("surfaces the server's error message", async () => {
    respond(401, { error: 'Wrong username or password.' });
    await expect(api('/api/auth/login', { body: {} })).rejects.toEqual(new ApiError(401, 'Wrong username or password.'));
  });

  it('falls back to a readable message when there is no JSON body', async () => {
    respond(429);
    const error = await api('/x').catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).message).toMatch(/too many/i);
  });

  it('sends JSON bodies as POST by default', async () => {
    respond(200, {});
    await api('/api/auth/login', { body: { username: 'a' } });
    const [, init] = (fetch as unknown as ReturnType<typeof vi.fn>).mock.calls[0] as [string, RequestInit];
    expect(init.method).toBe('POST');
    expect(init.body).toBe('{"username":"a"}');
  });
});
