import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { useSearchParams } from 'react-router';
import { api } from '../api/client';
import { Accordion } from '../components/Accordion';

// Hooks (PLAN.md §6.5): Home Assistant's devices for the familiar to work (BACKLOG item 29), other
// systems making the familiar speak (hook tokens), the familiar telling other systems what happened
// (webhooks), and other servers' tools for it to use (MCP).

import type {
  HomeAssistantRequest,
  HomeAssistantView,
  HookTokenView as HookToken,
  McpServerRequest,
  McpServerView as McpServer,
  McpSignInView,
  McpToolsRequest,
  NameRequest,
  NewHookTokenView,
  NewWebhookView,
  WebhookRequest,
  WebhookTestResult,
  WebhookView as Webhook,
} from '../protocol/generated';

const webhookEvents: { id: string; label: string }[] = [
  { id: 'timer.done', label: 'A timer finishes' },
  { id: 'reminder.due', label: 'A reminder is due' },
  { id: 'reply', label: 'It replies' },
  { id: 'tool.used', label: 'It uses a tool' },
];

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });
const message = (e: unknown, fallback: string) => (e instanceof Error ? e.message : fallback);

/** Shown once: a secret the server won't show again. */
function Reveal({ label, value, children }: { label: string; value: string; children?: React.ReactNode }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="reveal" role="status">
      <p><strong>{label}</strong> — copy it now; it won’t be shown again.</p>
      <div className="reveal-value">
        <code>{value}</code>
        <button
          type="button"
          className="secondary"
          onClick={() => void navigator.clipboard?.writeText(value).then(() => setCopied(true)).catch(() => undefined)}
        >
          {copied ? 'Copied' : 'Copy'}
        </button>
      </div>
      {children}
    </div>
  );
}

function HookTokens() {
  const [tokens, setTokens] = useState<HookToken[]>([]);
  const [name, setName] = useState('Home Assistant');
  const [created, setCreated] = useState<{ name: string; token: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => void api<HookToken[]>('/api/hooks/tokens').then(setTokens).catch(() => undefined), []);
  useEffect(load, [load]);

  async function create(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const result = await api<NewHookTokenView>('/api/hooks/tokens', { body: { name } satisfies NameRequest });
      setCreated(result);
      load();
    } catch (e) {
      setError(message(e, 'Couldn’t create a token.'));
    } finally {
      setBusy(false);
    }
  }

  const origin = window.location.origin;

  return (
    <div className="stack">
      <h3>Let other things make it speak</h3>
      <p className="muted">
        A hook token lets something else — Home Assistant, a script — have your familiar announce a message. It can’t do anything else.
      </p>
      {tokens.length > 0 && (
        <ul className="device-list">
          {tokens.map((t) => (
            <li key={t.id}>
              <div>
                <strong>{t.name}</strong>
                <span className="muted">{t.lastUsedAt ? `last used ${dateFormat.format(new Date(t.lastUsedAt))}` : 'not used yet'}</span>
              </div>
              <button type="button" className="secondary" onClick={() => void api<void>(`/api/hooks/tokens/${t.id}`, { method: 'DELETE' }).then(load, (e: unknown) => setError(message(e, 'Couldn’t revoke that token.')))}>Revoke</button>
            </li>
          ))}
        </ul>
      )}
      {created && (
        <Reveal label={`Token for ${created.name}`} value={created.token}>
          <p className="muted small">In Home Assistant’s <code>configuration.yaml</code>:</p>
          <pre className="snippet">{`rest_command:
  familiar_say:
    url: "${origin}/api/hooks/announce"
    method: post
    headers:
      authorization: "Bearer ${created.token}"
    content_type: "application/json"
    payload: '{"message": "{{ message }}"}'`}</pre>
          <p className="muted small">
            Then call <code>rest_command.familiar_say</code> with a <code>message</code> from any automation. Add{' '}
            <code>"style": "tell"</code> to have it put the message in its own words.
          </p>
        </Reveal>
      )}
      <form className="memory-edit" onSubmit={create}>
        <input value={name} onChange={(e) => setName(e.target.value)} maxLength={64} aria-label="What will use it" />
        <button type="submit" disabled={busy || !name.trim()} aria-busy={busy}>Create token</button>
      </form>
      {error && <p className="error" role="alert">{error}</p>}
    </div>
  );
}

function Webhooks() {
  const [hooks, setHooks] = useState<Webhook[]>([]);
  const [name, setName] = useState('');
  const [url, setUrl] = useState('');
  const [events, setEvents] = useState<string[]>(['timer.done', 'reminder.due']);
  const [secret, setSecret] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [adding, setAdding] = useState(false);

  const load = useCallback(() => void api<Webhook[]>('/api/webhooks').then(setHooks).catch(() => undefined), []);
  useEffect(load, [load]);

  async function create(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setAdding(true);
    try {
      const result = await api<NewWebhookView>('/api/webhooks', { body: { name, url, events } satisfies WebhookRequest });
      setSecret(result.secret);
      setName('');
      setUrl('');
      load();
    } catch (e) {
      setError(message(e, 'Couldn’t add that webhook.'));
    } finally {
      setAdding(false);
    }
  }

  async function test(id: string) {
    setBusy(true);
    try {
      await api<WebhookTestResult>(`/api/webhooks/${id}/test`, { method: 'POST' });
    } finally {
      setBusy(false);
      load();
    }
  }

  const toggle = (id: string) => setEvents((list) => (list.includes(id) ? list.filter((e) => e !== id) : [...list, id]));

  return (
    <div className="stack">
      <h3>Tell other things what happened</h3>
      <p className="muted">
        A webhook gets a signed POST when something happens — to start a Home Assistant automation, say. Check the{' '}
        <code>X-Familiar-Signature</code> header (HMAC-SHA256 of the body) with the webhook’s secret.
      </p>
      {hooks.length > 0 && (
        <ul className="device-list">
          {hooks.map((h) => (
            <li key={h.id}>
              <div>
                <strong>{h.name}</strong>
                <span className="muted">
                  {h.urlHost} · {h.events.join(', ')}
                  {h.lastResult && ` · last: ${h.lastResult}`}
                </span>
              </div>
              <span className="row-actions">
                <button type="button" className="link" disabled={busy} onClick={() => void test(h.id)}>Send a test</button>
                <button type="button" className="secondary" onClick={() => void api<void>(`/api/webhooks/${h.id}`, { method: 'DELETE' }).then(load, (e: unknown) => setError(message(e, 'Couldn’t remove that webhook.')))}>Remove</button>
              </span>
            </li>
          ))}
        </ul>
      )}
      {secret && <Reveal label="Signing secret" value={secret} />}
      <form className="stack" onSubmit={create}>
        <label>
          Name
          <input value={name} onChange={(e) => setName(e.target.value)} maxLength={64} placeholder="Home Assistant" required />
        </label>
        <label>
          Address
          <input value={url} onChange={(e) => setUrl(e.target.value)} type="url" placeholder="http://homeassistant.local:8123/api/webhook/…" required />
        </label>
        <fieldset className="check-group">
          <legend>Send when</legend>
          {webhookEvents.map((e) => (
            <label key={e.id} className="check">
              <input type="checkbox" checked={events.includes(e.id)} onChange={() => toggle(e.id)} />
              <span>{e.label}</span>
            </label>
          ))}
        </fieldset>
        <button type="submit" disabled={adding || !name.trim() || !url.trim() || events.length === 0} aria-busy={adding}>Add webhook</button>
      </form>
      {error && <p className="error" role="alert">{error}</p>}
    </div>
  );
}

/** Sends the browser to a server's sign-in page; it comes back to this page, which says how it went. */
async function signIn(server: McpServer) {
  const { authorizationUrl } = await api<McpSignInView>(`/api/mcp/${server.id}/sign-in`, { method: 'POST' });
  window.location.assign(authorizationUrl);
}

function McpServers() {
  const [servers, setServers] = useState<McpServer[]>([]);
  const [name, setName] = useState('');
  const [url, setUrl] = useState('');
  const [token, setToken] = useState('');
  const [clientId, setClientId] = useState('');
  const [clientSecret, setClientSecret] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Back from a sign-in: the server's name, and what went wrong if anything did. Said once, then
  // taken off the address so a reload doesn't say it again.
  const [params, setParams] = useSearchParams();
  const [returned] = useState(() => ({ name: params.get('mcp'), error: params.get('mcpError') }));
  useEffect(() => {
    if (params.has('mcp') || params.has('mcpError')) setParams({}, { replace: true });
  }, [params, setParams]);

  const load = useCallback(() => void api<McpServer[]>('/api/mcp').then(setServers).catch(() => undefined), []);
  useEffect(load, [load]);

  async function add(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const body = { name, url, token: token || null, clientId: clientId || null, clientSecret: clientSecret || null } satisfies McpServerRequest;
      const server = await api<McpServer>('/api/mcp', { body });
      setName('');
      setUrl('');
      setToken('');
      setClientId('');
      setClientSecret('');
      load();
      // It wants a sign-in: straight there, rather than one more button.
      if (server.signInNeeded) await signIn(server);
      else if (server.lastError) setError(server.lastError);
    } catch (e) {
      setError(message(e, 'Couldn’t add that server.'));
    } finally {
      setBusy(false);
    }
  }

  async function reconnect(server: McpServer) {
    setBusy(true);
    setError(null);
    try {
      await signIn(server);
    } catch (e) {
      setError(message(e, `Couldn’t start signing in to ${server.name}.`));
      setBusy(false);
    }
  }

  async function setTool(server: McpServer, tool: string, on: boolean) {
    const enabled = server.tools.filter((t) => (t.name === tool ? on : t.enabled)).map((t) => t.name);
    const updated = await api<McpServer>(`/api/mcp/${server.id}/tools`, { method: 'PUT', body: { enabled } satisfies McpToolsRequest });
    setServers((list) => list.map((s) => (s.id === updated.id ? updated : s)));
  }

  async function act(action: Promise<unknown>, failed: string) {
    setBusy(true);
    setError(null);
    try {
      await action;
    } catch (e) {
      setError(message(e, failed));
    } finally {
      setBusy(false);
      load();
    }
  }

  return (
    <div className="stack">
      <h3>Tools from MCP servers</h3>
      <p className="muted">
        Connect an MCP server (streamable HTTP) and your familiar can use its tools. Anything that isn’t marked read-only
        asks you first. Switch on as many tools as you like: each message is offered the ones that fit it best. Servers
        you sign in to — Notion, Linear,
        Atlassian, Asana — send you to their sign-in page and back here.
      </p>
      {returned.error && <p className="error" role="alert">{returned.name ? `${returned.name}: ` : ''}{returned.error}</p>}
      {returned.name && !returned.error && <p className="success" role="status">Signed in to {returned.name}.</p>}
      {servers.map((s) => (
        <div key={s.id} className="mcp-server">
          <div className="model-row">
            <div>
              <strong>{s.name}</strong>
              <span className={s.lastError ? 'error small' : 'muted small'}>
                {' '}· {s.urlHost}{s.auth === 'signIn' && !s.signInNeeded ? ' · signed in' : ''}
                {s.lastError ? ` · ${s.lastError}` : ''}
              </span>
            </div>
            <span className="row-actions">
              {s.auth === 'signIn' && (
                <button type="button" className={s.signInNeeded ? undefined : 'link'} disabled={busy} onClick={() => void reconnect(s)}>
                  {s.signInNeeded ? 'Sign in' : 'Sign in again'}
                </button>
              )}
              <button type="button" className="link" disabled={busy} onClick={() => void act(api(`/api/mcp/${s.id}/refresh`, { method: 'POST' }), `Couldn’t refresh ${s.name}.`)}>Refresh</button>
              <button type="button" className="secondary" disabled={busy} onClick={() => void act(api(`/api/mcp/${s.id}`, { method: 'DELETE' }), `Couldn’t remove ${s.name}.`)}>Remove</button>
            </span>
          </div>
          {/* Closed by default: a hosted server can offer fifty tools with paragraph-long descriptions. */}
          {s.tools.length > 0 && (
            <Accordion title="Tools" detail={`${s.tools.filter((t) => t.enabled).length} of ${s.tools.length} on`}>
              <ul className="tool-list">
                {s.tools.map((t) => (
                  <li key={t.name}>
                    <label className="check">
                      <input type="checkbox" checked={t.enabled} onChange={(e) => void setTool(s, t.name, e.target.checked)} />
                      <span className="tool-line" title={t.description ?? undefined}>
                        <code>{t.name}</code> <span className={`risk ${t.readOnly ? 'read' : 'write'}`}>{t.readOnly ? 'reads' : 'asks first'}</span>
                        {t.description && <span className="muted small"> — {t.description}</span>}
                      </span>
                    </label>
                  </li>
                ))}
              </ul>
            </Accordion>
          )}
        </div>
      ))}
      <form className="stack" onSubmit={add}>
        <label>
          Name <span className="muted small">(lower-case, it prefixes the tool names)</span>
          <input value={name} onChange={(e) => setName(e.target.value.toLowerCase())} maxLength={16} placeholder="homeassistant" pattern="[a-z][a-z0-9]*" required />
        </label>
        <label>
          Address
          <input value={url} onChange={(e) => setUrl(e.target.value)} type="url" placeholder="https://mcp.notion.com/mcp" required />
        </label>
        <label>
          Access token <span className="muted small">(optional; sent as a bearer token. Leave it empty for servers you sign in to.)</span>
          <input value={token} onChange={(e) => setToken(e.target.value)} type="password" autoComplete="off" />
        </label>
        <details>
          <summary className="muted small">Its sign-in needs an app you register yourself (GitHub, say)</summary>
          <div className="stack">
            <p className="muted small">
              Make an OAuth app in the service’s developer settings, with <code>{window.location.origin}/api/mcp/oauth/callback</code> as
              its callback address, and give its client ID here.
            </p>
            <label>
              Client ID
              <input value={clientId} onChange={(e) => setClientId(e.target.value)} autoComplete="off" />
            </label>
            <label>
              Client secret <span className="muted small">(if it has one)</span>
              <input value={clientSecret} onChange={(e) => setClientSecret(e.target.value)} type="password" autoComplete="off" />
            </label>
          </div>
        </details>
        <button type="submit" disabled={busy || !name || !url} aria-busy={busy}>{busy ? 'Connecting…' : 'Connect'}</button>
      </form>
      {error && <p className="error" role="alert">{error}</p>}
    </div>
  );
}

const notConnected: HomeAssistantView = { connected: false, urlHost: null, devices: [], refreshedAt: null, lastError: null };

function HomeAssistant() {
  const [home, setHome] = useState<HomeAssistantView | null>(null);
  const [url, setUrl] = useState('');
  const [token, setToken] = useState('');
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => void api<HomeAssistantView>('/api/home-assistant').then(setHome).catch(() => undefined), []);

  async function run(action: () => Promise<HomeAssistantView>, failed: string) {
    setBusy(true);
    setError(null);
    try {
      const updated = await action();
      setHome(updated);
      setEditing(false);
      setToken('');
    } catch (e) {
      setError(message(e, failed));
    } finally {
      setBusy(false);
    }
  }

  function connect(event: FormEvent) {
    event.preventDefault();
    void run(
      () => api<HomeAssistantView>('/api/home-assistant', { method: 'PUT', body: { url, token: token || null } satisfies HomeAssistantRequest }),
      'Couldn’t connect to Home Assistant.',
    );
  }

  const rooms = new Map<string, string[]>();
  for (const d of home?.devices ?? []) rooms.set(d.area ?? 'No room', [...(rooms.get(d.area ?? 'No room') ?? []), d.name]);
  const connected = home?.connected === true;

  return (
    <div className="stack">
      <h3>Home Assistant</h3>
      <p className="muted">
        Your familiar can work the devices you’ve exposed to Assist in Home Assistant — “turn off the kitchen light”,
        “is the garage door open?”. Change which devices in Home Assistant: Settings → Voice assistants → Expose, then
        refresh here. Unlocking doors and opening the garage ask you first.
      </p>
      {connected && home && (
        <div className="mcp-server">
          <div className="model-row">
            <div>
              <strong>{home.urlHost}</strong>
              <span className={home.lastError ? 'error small' : 'muted small'}>
                {home.lastError ? ` · ${home.lastError}` : ''}
              </span>
            </div>
            <span className="row-actions">
              <button type="button" className="link" disabled={busy}
                onClick={() => void run(() => api<HomeAssistantView>('/api/home-assistant/refresh', { method: 'POST' }), 'Couldn’t refresh.')}>Refresh</button>
              <button type="button" className="link" disabled={busy} onClick={() => setEditing((e) => !e)}>Change</button>
              <button type="button" className="secondary" disabled={busy}
                onClick={() => void run(async () => { await api('/api/home-assistant', { method: 'DELETE' }); return notConnected; }, 'Couldn’t disconnect.')}>Disconnect</button>
            </span>
          </div>
          {rooms.size > 0 && (
            <Accordion
              title="Devices"
              detail={`${home.devices.length} in ${rooms.size} ${rooms.size === 1 ? 'room' : 'rooms'}`}
            >
              <ul className="tool-list">
                {[...rooms].map(([room, names]) => (
                  <li key={room}><strong>{room}</strong><span className="muted small"> — {names.join(', ')}</span></li>
                ))}
              </ul>
            </Accordion>
          )}
        </div>
      )}
      {(!connected || editing) && (
        <form className="stack" onSubmit={connect}>
          <label>
            Address
            <input value={url} onChange={(e) => setUrl(e.target.value)} type="url" placeholder="http://homeassistant.local:8123" required />
          </label>
          <label>
            Long-lived access token <span className="muted small">(your Home Assistant profile → Security{connected ? '; leave empty to keep the current one' : ''})</span>
            <input value={token} onChange={(e) => setToken(e.target.value)} type="password" autoComplete="off" required={!connected} />
          </label>
          <button type="submit" disabled={busy || !url} aria-busy={busy}>{busy ? 'Connecting…' : 'Connect'}</button>
        </form>
      )}
      {error && <p className="error" role="alert">{error}</p>}
    </div>
  );
}

export function ConnectionsSettings() {
  return (
    <div className="stack connections">
      <HomeAssistant />
      <HookTokens />
      <Webhooks />
      <McpServers />
    </div>
  );
}
