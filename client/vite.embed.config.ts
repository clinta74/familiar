import { defineConfig } from 'vite';

/**
 * The avatar alone (avatar.html), built for the phone apps to bundle (MOBILE.md §1.1): relative
 * paths, so it loads from the app's assets, and output outside dist/ so the web image doesn't
 * ship it. mobile/androidApp builds it as part of the app.
 */
export default defineConfig({
  base: './',
  // Not public/: the service worker and the browser's voice-detection files have no place here.
  publicDir: false,
  build: {
    outDir: 'dist-embed',
    emptyOutDir: true,
    // three-vrm, loaded only for an uploaded VRM model (src/avatar/createAvatar.ts).
    chunkSizeWarningLimit: 800,
    rollupOptions: { input: 'avatar.html' },
  },
});
