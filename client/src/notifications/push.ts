import { api } from '../api/client';
import type { PushKeyResponse, SubscribeRequest, UnsubscribeRequest } from '../protocol/generated';

// Web Push for timers and reminders with the tab closed (PLAN.md §6.2). The pushes carry nothing:
// public/sw.js asks the server what happened when one arrives.

export type PushStatus = 'unsupported' | 'insecure' | 'denied' | 'off' | 'on';

const supported = () =>
  typeof window !== 'undefined' && 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;

/** Registers the service worker. Harmless to call on every load. */
export function registerServiceWorker(): void {
  if (!supported() || !window.isSecureContext) return;
  navigator.serviceWorker.register('/sw.js').catch(() => undefined);
}

export async function pushStatus(): Promise<PushStatus> {
  if (!supported()) return 'unsupported';
  // Service workers need https (or localhost). A self-signed certificate the browser doesn't trust
  // counts as insecure for this, even when the page itself was allowed to load.
  if (!window.isSecureContext) return 'insecure';
  if (Notification.permission === 'denied') return 'denied';
  const registration = await navigator.serviceWorker.getRegistration();
  const subscription = await registration?.pushManager.getSubscription();
  return subscription && Notification.permission === 'granted' ? 'on' : 'off';
}

function keyBytes(base64url: string): Uint8Array<ArrayBuffer> {
  const base64 = base64url.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(base64url.length / 4) * 4, '=');
  const binary = atob(base64);
  const bytes = new Uint8Array(new ArrayBuffer(binary.length));
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

function sameBytes(a: Uint8Array, b: Uint8Array): boolean {
  return a.length === b.length && a.every((value, i) => value === b[i]);
}

/** Asks for permission (call it from a click) and subscribes this browser. */
export async function enablePush(): Promise<PushStatus> {
  if (!supported()) return 'unsupported';
  if (!window.isSecureContext) return 'insecure';

  const permission = await Notification.requestPermission();
  if (permission !== 'granted') return permission === 'denied' ? 'denied' : 'off';

  const registration = await navigator.serviceWorker.register('/sw.js');
  await navigator.serviceWorker.ready;
  const { publicKey } = await api<PushKeyResponse>('/api/push/key');
  const wanted = keyBytes(publicKey);

  let subscription = await registration.pushManager.getSubscription();
  const current = subscription?.options.applicationServerKey;
  if (subscription && (!current || !sameBytes(new Uint8Array(current), wanted))) {
    // Subscribed against a different server key (a reset database): start again.
    await subscription.unsubscribe();
    subscription = null;
  }

  subscription ??= await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: wanted });
  const json = subscription.toJSON();
  const keys = json.keys ? { p256dh: json.keys.p256dh ?? null, auth: json.keys.auth ?? null } : null;
  await api<void>('/api/push/subscriptions', { body: { endpoint: json.endpoint ?? null, keys } satisfies SubscribeRequest });
  return 'on';
}

export async function disablePush(): Promise<PushStatus> {
  const registration = await navigator.serviceWorker.getRegistration();
  const subscription = await registration?.pushManager.getSubscription();
  if (subscription) {
    await api<void>('/api/push/subscriptions', { method: 'DELETE', body: { endpoint: subscription.endpoint } satisfies UnsubscribeRequest })
      .catch(() => undefined);
    await subscription.unsubscribe();
  }
  return 'off';
}
