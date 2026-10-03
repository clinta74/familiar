/// <reference types="vitest/config" />
import { createReadStream, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { defineConfig, type Plugin } from 'vite';
import react from '@vitejs/plugin-react';

// The dev server proxies the API so the browser stays on one origin. The session cookie is
// SameSite=Lax and HttpOnly, and calling Kestrel cross-origin would need SameSite=None in dev only -
// which is how cookie bugs reach production. The conversation's event stream depends on it most:
// EventSource can't set an Authorization header, so the cookie is its only credential.
const server = process.env.FAMILIAR_SERVER ?? 'http://localhost:5280';

const vadDirectory = join(dirname(fileURLToPath(import.meta.url)), 'public');
const vadTypes: Record<string, string> = { '.mjs': 'text/javascript', '.js': 'text/javascript', '.wasm': 'application/wasm' };

/**
 * Serves the hands-free assets in public/vad/ as plain files in development. The ONNX runtime
 * dynamically imports its WebAssembly glue (ort-wasm-simd-threaded.mjs); Vite rewrites that request
 * to `...mjs?import` and then refuses it, because files in public/ may not be imported as modules.
 * Production is unaffected - nginx serves the built files as they are.
 */
function vadAssets(): Plugin {
  return {
    name: 'familiar-vad-assets',
    configureServer(dev) {
      dev.middlewares.use((req, res, next) => {
        const path = req.url?.split('?')[0];
        if (!path?.startsWith('/vad/')) return next();
        const file = join(vadDirectory, path);
        if (!file.startsWith(join(vadDirectory, 'vad')) || !existsSync(file)) return next();
        res.setHeader('Content-Type', vadTypes[file.slice(file.lastIndexOf('.'))] ?? 'application/octet-stream');
        createReadStream(file).pipe(res);
      });
    },
  };
}

export default defineConfig({
  plugins: [react(), vadAssets()],
  server: {
    port: 5273,
    strictPort: true,
    // The phone apps' avatar page builds into dist-embed/; it isn't the dev server's to reload for.
    watch: { ignored: ['**/dist-embed/**'] },
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
