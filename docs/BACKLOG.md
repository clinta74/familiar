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

### 4. No flash of the default llama while the real look loads

On every load, the stage shows the default 2D llama until the profile (and, for 3D or VRM, the
model) has loaded, then swaps — so someone with a 3D fox sees a flat llama first. Instead:

- Don't build any avatar until the appearance is known; show an empty stage with a soft
  placeholder (a gentle pulsing glow in the accent colour, or the character's silhouette) and fade
  the real one in when it's ready.
- Remember the last appearance in `localStorage`, so a returning visit can build the right
  character straight away while the profile is re-checked.
- Same in the Android app: it currently starts from the page's default look until `/api/familiar`
  answers.

### 5. Warm the prompt, not just the model

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

---

## Avatars

### 6. ~~The 3D model pipeline~~ — done (2026-10-03)

Option A below: the characters are scripted in Blender (`tools/models/`), the runtime loads the
GLBs. Still open:

- **2D spot splitting:** the 3D spot splits and reforms for a shake, a laugh or excitement; the flat
  one doesn't yet.
- **Flat characters' arms** still hang with paws on the tummy; the 3D ones now stand to the ground
  like a sitting dog's. Decide whether the flat ones should follow.
- A local image-to-3D model (TRELLIS / Hunyuan3D) as a reference for harder shapes: not needed so far.

### 7. ~~An otter and a giraffe~~ — done (2026-10-03)

Both, flat and 3D, in the studio with presets, accepted by the server, with a persona line.

### 8. Check 3D on a real phone

All five 3D characters were reviewed in a desktop browser only. Try them on the Pixel Fold in the
Android app's WebView: frame rate, heat, battery over ten minutes. If it's too much, the levers are
fewer fur shells on phones, a lower pixel ratio, and pausing rendering when nothing moves.

---

## Android (docs/MOBILE.md)

### 9. Push-to-talk (the rest of M1)

Record with `AudioRecord` (RECORD_AUDIO, asked the first time), send to `POST /api/conversation/audio`
as WAV; a hold-to-talk button that interrupts the familiar if it's speaking.

---

## Blender, or AI-generated models? (for item 6)

The 3D characters are built in code from ellipsoids and swept spheres, with shell fur on top. That
got us soft, expressive toys quickly, but the shapes are limited to what primitives blend into:
seams where a muzzle meets a face, no real sculpted forms. Three ways to get better models, and
what each would take.

### A. Blender, scripted (recommended)

Blender has a complete Python API and runs headless (`blender -b -P build_otter.py`), so models
can be **made by scripts** — written and iterated by Claude, committed to the repo, and rebuilt by
anyone. Subdivision-surface modelling gives continuous organic shapes (a head that flows into its
muzzle, a body that flows into its legs) that ellipsoids can't. Each script:

- builds the character as named parts with their pivots (`head`, `ear.L`, `arm.L.upper`,
  `arm.L.fore`, `tail`, `torso` …) and materials named by colour role (`wool`, `face`, `ear`);
- exports a small GLB (100–300 KB) into the client;
- renders preview images (front, three-quarter, side) so the result can be reviewed and iterated
  without anyone opening Blender.

At runtime, `Plush3D` loads the GLB instead of building primitives, and keeps doing what it does
now: shell fur over each part, the palette, the painted face, eyes and lids, the pose. Blender's own
hair doesn't export to the web, so fur stays ours.

**Needs:** a current Blender (this machine has 2.91, from 2020; its glTF exporter and Python API are
too old — install 4.5 LTS or newer). **Effort:** the pipeline (shared modelling helpers, export,
preview renders) and the GLB loader in `Plush3D` are about a day; then each character is a script
and a few review rounds. **Limits:** scripted modelling is still modelling-by-description; it beats
primitives clearly, but it isn't a sculptor's hand.

### B. Blender driven live over MCP

The open-source *blender-mcp* add-on lets an AI session drive a running Blender: create and edit
objects, run Python, take screenshots, pull assets from Poly Haven, generate models through Hyper3D
Rodin. Same modelling power as A, but interactive — you can watch, tweak by hand, and Claude picks
up from there. Good for review sessions; save the resulting script so it stays reproducible. Needs
the add-on installed in Blender and the MCP server added to Claude Code.

### C. AI image-to-3D

Generate a picture of each plush (an image model), then turn it into a textured mesh:

- **Locally, on this machine's RTX 5070 Ti (16 GB):** Microsoft TRELLIS (MIT licence; ~16 GB, tight),
  Tencent Hunyuan3D-2 (smaller variants fit; its licence excludes the EU, UK and South Korea),
  Stability's SPAR3D / Stable Fast 3D (community licence).
- **Hosted:** Meshy, Tripo, Hyper3D Rodin — paid credits, commercial use on paid plans, HTTP APIs
  (and MCP servers for some).

This gives the most natural shapes and textures fastest, **but** the output is one fused mesh with
the colours and face baked in: no separate ears, arms or tail to move, a face that can't make our
expressions, colours that can't be changed in the studio, and messy topology. Making one usable is
Blender work anyway — split the parts, rebuild the face area, re-UV.

Claude can't call another AI model by itself; it can use one connected as an MCP server, or call a
hosted API from a script with an API key you provide.

### Recommendation

**A, with C as an optional reference.** Script the characters in Blender (the scripts are the
source; the GLBs are built from them), keep fur, face and motion in the runtime, and if a shape is
hard to get right by description, generate a reference mesh with TRELLIS or Hunyuan3D locally and
model the clean, part-split version over it (Blender's shrinkwrap). B is worth adding for live
review sessions once A exists. Start with the llama (to compare against today's) and then build
the otter and giraffe this way.

**Decisions needed:** install a current Blender here (and later wherever the build runs, if GLBs are
rebuilt in CI rather than committed); whether to set up a local image-to-3D model.
