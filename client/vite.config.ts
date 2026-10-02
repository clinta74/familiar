/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The dev server proxies the API so the browser stays on one origin. The session cookie is
// SameSite=Lax and HttpOnly, and calling Kestrel cross-origin would need SameSite=None in dev only -
// which is how cookie bugs reach production. The conversation's event stream depends on it most:
// EventSource can't set an Authorization header, so the cookie is its only credential.
const server = process.env.FAMILIAR_SERVER ?? 'http://localhost:5280';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5273,
    strictPort: true,
    proxy: {
      '/api': server,
      '/health': server,
    },
  },
  build: {
    // three.js + three-vrm make the VRM renderer a ~750 kB chunk. It's loaded only when a VRM model
    // is present (src/avatar/createAvatar.ts), so the default build never downloads it.
    chunkSizeWarningLimit: 800,
  },
  test: {
    environment: 'jsdom',
  },
});
