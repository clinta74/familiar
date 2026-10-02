/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The dev server proxies the API so the browser stays on one origin. The session cookie is
// SameSite=Lax and HttpOnly, and calling Kestrel cross-origin would need SameSite=None in dev only -
// which is how cookie bugs reach production. /ws is proxied too, because a browser WebSocket can't
// set an Authorization header and relies on that same cookie.
const server = process.env.FAMILIAR_SERVER ?? 'http://localhost:5280';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5273,
    strictPort: true,
    proxy: {
      '/api': server,
      '/health': server,
      '/ws': { target: server, ws: true },
    },
  },
  test: {
    environment: 'jsdom',
  },
});
