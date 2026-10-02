// Familiar's service worker: shows timer and reminder notifications when the tab is closed
// (PLAN.md §6.2). It does nothing else - no caching, no fetch handler.
//
// Pushes carry no content, so the browser vendor's push service never sees a reminder's text. On
// each push this asks the server, over the user's own session, what just went off.

const SHOWN = 'familiar-notifications-shown';

async function shownIds() {
  const cache = await caches.open(SHOWN);
  const response = await cache.match('/ids');
  return new Set(response ? await response.json() : []);
}

async function remember(ids) {
  const cache = await caches.open(SHOWN);
  await cache.put('/ids', new Response(JSON.stringify([...ids].slice(-50))));
}

async function onPush() {
  const response = await fetch('/api/notifications/recent', { credentials: 'same-origin' });
  if (!response.ok) return;
  const recent = await response.json();

  const shown = await shownIds();
  const fresh = recent.filter((n) => !shown.has(n.id));
  fresh.forEach((n) => shown.add(n.id));
  await remember(shown);

  // A familiar window the user is looking at has already said it aloud.
  const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
  if (windows.some((w) => w.visibilityState === 'visible')) return;

  for (const n of fresh) {
    await self.registration.showNotification(n.title, {
      body: n.body,
      tag: n.id,
      icon: '/favicon.svg',
      requireInteraction: n.kind === 'timer',
      data: { url: '/' },
    });
  }
}

self.addEventListener('push', (event) => {
  event.waitUntil(onPush().catch(() => undefined));
});

self.addEventListener('notificationclick', (event) => {
  event.notification.close();
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    const open = windows.find((w) => new URL(w.url).origin === self.location.origin);
    if (open) return open.focus();
    return self.clients.openWindow(event.notification.data?.url ?? '/');
  })());
});

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));
