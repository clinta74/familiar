/** An error the server answered with, carrying its `{ error }` message when it sent one. */
export class ApiError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

const fallbackMessages: Record<number, string> = {
  401: 'Please sign in.',
  403: "You can't do that.",
  404: 'Not found.',
  429: 'Too many attempts. Wait a moment and try again.',
  // The proxy in front, saying the server behind it isn't answering (restarting, say).
  502: 'Familiar’s server isn’t answering. Try again in a moment.',
  503: 'Familiar’s server isn’t answering. Try again in a moment.',
  504: 'Familiar’s server isn’t answering. Try again in a moment.',
};

export async function errorFrom(response: Response): Promise<ApiError> {
  let message = fallbackMessages[response.status] ?? `Something went wrong (${response.status}).`;
  try {
    const body: unknown = await response.json();
    if (body && typeof body === 'object' && 'error' in body && typeof body.error === 'string') {
      message = body.error;
    }
  } catch {
    // No JSON body; the fallback stands.
  }
  return new ApiError(response.status, message);
}

/**
 * JSON over fetch, same-origin, with the session cookie. Resolves to undefined for 204.
 */
export async function api<T>(path: string, init: { method?: string; body?: unknown } = {}): Promise<T> {
  let response: Response;
  try {
    response = await fetch(path, {
      method: init.method ?? (init.body === undefined ? 'GET' : 'POST'),
      credentials: 'same-origin',
      headers: init.body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: init.body === undefined ? undefined : JSON.stringify(init.body),
    });
  } catch {
    // The browser's own wording ("Failed to fetch") means nothing to anyone; status 0, as fetch has none.
    throw new ApiError(0, 'Couldn’t reach Familiar. Check the connection and try again.');
  }

  if (!response.ok) {
    throw await errorFrom(response);
  }

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}
