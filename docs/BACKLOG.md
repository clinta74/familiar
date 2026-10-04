# Familiar — Backlog

Work that's agreed but not started, roughly in priority order within each section. Bigger designs
live in [PLAN.md](PLAN.md) and [MOBILE.md](MOBILE.md); this is the list of what's next. Move an
item out (or delete it) when it ships, and say where in the commit.

---

## Web app

### 1–3. ~~Header, account menu, admin page~~ — done (2026-10-03)

The header has Chat, Studio and Settings (the current page underlined) and an account button whose
menu has Settings, Devices, Users (admins) and Sign out; user administration is its own page,
`/admin`.

### 4. ~~No flash of the default llama~~ — done (2026-10-03)

Nothing is built until the look is known; a pulsing glow holds the place and the character fades
in. The last look is remembered per account (`localStorage` on the web, the secure store on
Android), so a returning visit builds the right one straight away.

### 5. ~~Warm the prompt, not just the model~~ — measured; left as it is (2026-10-03)

Decided: the ~1 s of prefill on the first turn is acceptable, so neither a shorter prompt, another
model nor llama-server is worth it for now. The notes stay for if it starts to matter.

`LlmWarmUp` / `OllamaWarmUp` load the chat model at startup and keep it loaded, but the first turn
after a restart still pays to **prefill the whole system prompt** (persona, rules, look). Muwbta's
`AssistWarmUp` shows the fix: send the stable prefix once with `num_predict: 1` so Ollama's KV cache
holds it, and re-send only when it changes.

- **What to warm:** each familiar's system prompt up to where it starts varying. Memories already sit
  at the end of the system message for this reason (PLAN.md, phase 5 notes); the situation notes are per
  turn. Put anything shared by every familiar (the rules) first so the shared prefix is as long as
  possible.
- **Between API restarts it's already cheap:** the KV cache lives in the Ollama process, not ours. If
  the prompt hasn't changed, re-sending it after our server restarts reuses the cached prefix and
  costs almost nothing, so warming on every start is fine. Keep a hash of what was last warmed per
  familiar only to skip pointless re-sends while running (e.g. re-warm when a persona is edited).
- **Across Ollama restarts it can't be kept:** Ollama has no way to save and restore a KV cache to
  disk (llama.cpp's own `llama-server` has slot save/restore; Ollama doesn't expose it). After an
  Ollama restart the warm-up simply runs again: model load (~11 s on the dev box) then prefill.
- **Several users:** with `OLLAMA_NUM_PARALLEL=1` there's one cache slot, so two people chatting in
  turn evict each other's persona prefix. Measure prefill time per turn on the NAS first; if it
  matters, raise parallelism (costs VRAM per slot) or warm only the most recently active familiar.
- Measure: log `prompt_eval_count` / `prompt_eval_duration` from the first real turn before and after.

**Measured first (2026-10-03, dev box, Ollama 0.35.0, familiar-chat on gemma4:12b): there's no prefix
cache to warm.** Identical requests sent back to back each re-evaluate the whole prompt -
`prompt_eval_count` 761 of 761 tokens without tools, 1,240 of 1,240 with eight tool definitions,
0.7-1.0 s each - and changing only the end of the system message or the user message costs the same
as changing nothing. So a warm-up request would leave nothing behind for the next turn to reuse, and
it hasn't been built. (Likely gemma4's sliding-window attention or the draft model the derived model
uses: Ollama reuses the cache for other model families. Not confirmed - only gemma4 is installed.)

What would actually cut the ~1 s of prefill before the first word:

- **Fewer prompt tokens.** The tool definitions are about 480 of ~1,250; shorter descriptions, or
  only offering tools when the message could need one, would save the most.
- **A model whose cache Ollama reuses** (a Qwen or Llama family model): then this item applies as
  written above. Needs the bake-off re-run (tool calling, tags).
- **llama.cpp's `llama-server`** with `cache_prompt`, which also has slot save/restore across
  restarts.


### 5a. ~~Open the chat at its latest message~~ — done (2026-10-03)

A conversation that arrives all at once (restored, reloaded after a resync, a new one) opens at its
latest message, on the web and Android; messages after that still don't pull someone reading back.

---

## Avatars

### 6. ~~The 3D model pipeline~~ — done (2026-10-03)

The characters are scripted in Blender (`tools/models/`: the scripts are the source, the GLBs are
built from them and committed); fur, face and motion stay in the runtime (`Plush3D`). The flat
characters keep their paws on the tummy (decided 2026-10-03); only the 3D ones sit like a dog. A
local image-to-3D model (TRELLIS / Hunyuan3D) as a reference for harder shapes hasn't been needed.

### 7. ~~An otter and a giraffe~~ — done (2026-10-03)

Both, flat and 3D, in the studio with presets, accepted by the server, with a persona line.

### 7a. ~~Call the llama just "Llama"~~ — done (2026-10-03)

The studio and the gallery say "Llama". Descriptions stay "a plush llama" as the others' do ("a plush
fox"), and "Fuzzy monster" is unchanged.

### 8. ~~Check 3D on a real phone~~ — measured on the Fold; fine as it is (2026-10-04)

Measured on the Pixel 10 Pro Fold (folded, Chrome 154; the app's WebView is the same engine) with the
app's own avatar page (the production build), at the app's size: the full width and 40% of the
height, a 786×604 canvas (pixel ratio capped at 2 of 2.75).

- **Found on the way: the app had never shown 3D.** The page's Content Security Policy blocked
  WebAssembly, which the compressed GLBs need, so every 3D character but Spot quietly fell back to the
  flat drawing. Fixed with `'wasm-unsafe-eval'` (compiling WebAssembly only, no `eval`), and a test
  keeps it (`client/src/embed/avatarPage.test.ts`). Seen in the app itself on the Fold with v0.1.0: the 3D fox.
- **Frame rate:** every character a steady 60 fps, no frame over 25 ms, for 16 minutes of the
  giraffe (the heaviest) in a row. The page's own work per frame is 3-4.5 ms (the flat ones 0.3 ms).
- **CPU:** Chrome's processes together use about half of one core for a 3D character (48-53%),
  against a third (33-35%) for a flat one; Spot in 3D, 38%.
- **Heat:** with the giraffe, skin temperature rose from 34.6 °C and levelled off at about 38.5 °C
  after ten minutes; the flat llama held the same temperature, about 1 °C cooler at the chip. No
  thermal throttling at any point. (The phone was charging over USB throughout, which warms it too.)
- **Battery:** not measured directly - the phone was on USB power and its energy counters aren't
  readable without root. The CPU difference above is the best proxy: about a sixth of a core more.

So none of the levers (fewer fur shells, a lower pixel ratio, pausing when still) is needed. If
battery turns out to matter in long hands-free sessions, pausing rendering while the screen is off is
already the case (the WebView stops drawing), and capping the frame rate at 30 when idle would be next.

---

## Android (docs/MOBILE.md)

### 9. ~~Push-to-talk (the rest of M1)~~ — done (2026-10-03)

Hold the mic button to talk, let go to send (MOBILE.md, M1). Still to do on a real phone, with item 8.

### 10. ~~Publish the APK as a release~~ — done (2026-10-03)

A version tag (`v0.2.0`) publishes a GitHub release with the APK signed by the release key, versioned
from the tag, alongside the Docker images ([RELEASING.md](RELEASING.md)). **Needs, once:** the release
key made and its four `ANDROID_*` secrets added (RELEASING.md, "The release key"); until then the
release job fails and says so.
