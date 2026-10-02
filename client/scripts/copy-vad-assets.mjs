// Copies what hands-free listening loads at runtime into public/vad/ (gitignored): the Silero VAD
// model and audio worklet from @ricky0123/vad-web, and the ONNX runtime's WebAssembly build.
// They're fetched by URL, not imported, so the bundler can't see them - hence a copy, run before
// `dev` and `build` (and so inside the Docker build too).
import { copyFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const target = join(root, 'public', 'vad');

const files = [
  ['node_modules/@ricky0123/vad-web/dist/vad.worklet.bundle.min.js', 'vad.worklet.bundle.min.js'],
  ['node_modules/@ricky0123/vad-web/dist/silero_vad_v5.onnx', 'silero_vad_v5.onnx'],
  ['node_modules/onnxruntime-web/dist/ort-wasm-simd-threaded.wasm', 'ort-wasm-simd-threaded.wasm'],
  ['node_modules/onnxruntime-web/dist/ort-wasm-simd-threaded.mjs', 'ort-wasm-simd-threaded.mjs'],
];

mkdirSync(target, { recursive: true });
for (const [from, to] of files) copyFileSync(join(root, from), join(target, to));
console.log(`vad: copied ${files.length} assets to public/vad`);
