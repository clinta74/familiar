# Familiar — Plan (web + server)

A voice-first AI companion, voiced by an expressive 2D/3D avatar, running against a local LLM.
Web first, then native Android, then iOS — one server for all three. This document covers the
**server** and the **web client**. The mobile apps are in [MOBILE.md](MOBILE.md); the server is
designed for them from day one.

---

## 1. Goals

1. **Talk to it.** Hands-free voice conversation with sub-2-second voice-to-voice latency on the
   GPU box, and text chat as a first-class fallback.
2. **It looks like it's listening.** The avatar's expression, gaze, and gestures follow the
   conversation — not just lip-sync, but mood.
3. **It does things.** Timers, reminders, calendar, and situation-aware answers ("you've got a
   meeting in 20 minutes, so the short version is…"), through a tool/hook system that can grow.
4. **It is local.** Inference, speech, accounts, and memory all on our hardware. No cloud
   dependency is required to run it.
5. **It is yours.** Each user customises their familiar's look, voice, name, and personality.

Non-goals for v1: multi-user shared rooms, public hosting, cloud LLM fallback, wake-word.

---

## 2. Stack decision

| Layer | Choice | Why |
|---|---|---|
| Core server | **C# / .NET 10, ASP.NET Core** | See §2.1. |
| Speech (STT/TTS) | **Python containers, off the shelf** | The speech ecosystem is Python. We consume it over HTTP, we don't write it. |
| Database | **PostgreSQL 18 + pgvector** | Accounts, conversations, scheduler, and vector memory in one place. Image: `pgvector/pgvector:pg18`. |
| LLM | **Ollama**, Familiar's own container (§4.2) | Same image and GPU overlays muwbta already runs on the dev box (5070 Ti) and NAS (3060 12 GB). |
| Web client | **React 19 + Vite + TypeScript** | Same as muwbta's client. |
| Avatar renderer | **three.js + `@pixiv/three-vrm`** (3D), 2D renderer later behind the same interface | See §5. |

### 2.1 Why .NET over Python or a TypeScript server

- **The hard, boring parts are a known pattern on this stack.** muwbta has already worked out —
  and found the bugs in — cookie + personal access token auth, PBKDF2 via `IPasswordHasher`,
  constant-time login (decoy hash for unknown users), login throttling, rate limiting, live
  principal revalidation (role/ban changes land on open sessions), EF Core + Npgsql with
  snake_case naming, migrations at startup, and Testcontainers integration tests. Familiar
  **re-implements the same pattern fresh** (no code copied), and doing that on the same framework
  and libraries is direct; in Python it's a redesign with its own new bugs.
- **The Ollama approach carries over the same way.** Native API rather than the OpenAI-compatible
  one (for `num_ctx` / `keep_alive`), a derived model with the window baked in, and the
  prefix-caching discipline from muwbta's `tools/ollama/README.md` (§4.3).
- **Long-running, stateful work is .NET's strength**: hosted services for the scheduler,
  `Channel<T>` for per-conversation pipelines, streaming responses, cancellation everywhere (barge-in is a
  cancellation problem).
- **Microsoft.Extensions.AI** gives a provider-neutral chat + tool-calling abstraction with an
  Ollama implementation, so tools are plain C# methods with attributes.
- Python's real advantage — speech and ML libraries — is captured by running those as sidecar
  containers. We'd be using Python as a service, not as the application.
- A TypeScript server would share types with the client, but we'd give up the known pattern for
  a gain that OpenAPI-generated client types mostly recover.

---

## 3. Architecture

```
 Browser / phone                                Server (.NET 10)                      Sidecars
 ┌──────────────────────┐ GET  /api/conversation/stream (SSE)
 │ event stream      ◄──┤◄── state, deltas, ──┐┌──────────────────────────────┐
 │                      │    expression, ...  ││ ConversationHub (per account)│
 │ mic → VAD → utterance├─── POST .../audio ──►│   ├ STT client               │──HTTP──► STT  (faster-whisper)
 │ typed text           ├─── POST .../messages►│   ├ AgentLoop (LLM + tools) ─┼──HTTP──► Ollama
 │ stop / barge-in      ├─── POST .../interrupt►  ├ EmotionTagParser          │
 │ audio player     ◄───┤◄── GET  .../audio/{n}  ├ SentenceChunker → TTS ───┼──HTTP──► TTS  (Kokoro)
 │ avatar (VRM / face)  │                     │└   └ ContextBuilder           │
 └──────────────────────┘                     │  Scheduler (hosted service)   │──► Web Push / FCM / APNs
          REST /api (auth, familiar,          │  Tool registry + hooks        │──► CalDAV, webhooks, MCP
          history, settings)                  └─ EF Core ─────────────────────┼──► Postgres 18 + pgvector
                                                └──────────────────────────────┘
```

### 3.1 Transport — Server-Sent Events + POST

**Decided (2026-10-01): SSE for everything the server pushes, POST for everything the client
sends.** The first draft of this plan used a WebSocket; it was dropped before any code was
written, because nothing in this design needs a continuous upstream channel:

- **Speech-to-text runs per utterance.** Client-side VAD decides when you've stopped talking
  (§3.2), and the whole utterance is one POST — tens of KB, milliseconds on the LAN or VPN.
  faster-whisper doesn't produce useful live partials anyway, so streaming mic audio up a socket
  would buy nothing.
- **Barge-in is a POST** that cancels the running turn's token. No socket required.
- **Reconnection is built in.** `EventSource` reconnects on its own and sends `Last-Event-ID`;
  the server replays from a per-account ring buffer, or tells the client to resync history if
  it fell too far behind. Phones drop connections constantly; with a socket that resume logic
  would be ours to write.
- **One account, every device.** A turn's events go to every open stream of the account, so
  the phone and the browser show the same conversation live.
- **It's plain HTTP:** the auth, rate limits, forwarded headers and nginx config that already
  exist apply unchanged, with no upgrade handling and no cross-site WebSocket hijacking to guard.
  muwbta runs this pattern in production.

The cost is that SSE is text: TTS audio (phase 2) is fetched per sentence by URL, announced by an
event, rather than pushed. If true streaming STT is ever wanted, a socket can be added for that
alone.

- **Web:** same-origin HttpOnly cookie. `EventSource` can't set headers, which is the reason the
  browser uses the cookie (Vite and nginx proxy `/api` so it's one origin).
- **Android / iOS:** `Authorization: Bearer` device token on every request, the stream included
  (OkHttp and URLSession can set headers).

| Direction | Endpoint / event | Payload |
|---|---|---|
| ↑ | `POST /api/conversation/messages` | `{ text, timeZone }` — a typed message; starts a turn |
| ↑ | `POST /api/conversation/audio` | an utterance (Opus/WebM or WAV) after VAD end-of-speech (phase 2) |
| ↑ | `POST /api/conversation/interrupt` | user started speaking / pressed stop |
| ↑ | `POST /api/conversation/confirm` | `{ toolCallId, approved }` (phase 4) |
| ↑ | `POST /api/conversation/tool-result` | `{ callId, ok, result?, error? }` — a phone's answer to `tool.invoke` (§6.1) |
| ↑ | `PUT /api/calendars/device` | a phone's calendars for the next two weeks, replacing its last upload (§6.4) |
| ↓ | `ready` | first event: the familiar's name and whether the model is reachable |
| ↓ | `state` | `idle` · `thinking` · `speaking` · `resting` (model unreachable) · `listening` · `using-tool` |
| ↓ | `message` | a user message as stored (so other devices see it) |
| ↓ | `reply.start` / `reply.delta` / `reply.end` | assistant text, tags stripped; end says if interrupted |
| ↓ | `expression` | `{ emotion, intensity }` |
| ↓ | `gesture` | `{ name }` — nod, shake, tilt, wave, shrug |
| ↓ | `audio` | `{ seq, url, visemes? }` — a TTS sentence is ready (phase 2–3) |
| ↓ | `tool.confirm` / `tool.invoke` | ask the user; run a device-side tool (phase 4) |
| ↓ | `turn.error` | `{ code, message }` — e.g. `resting` when the model is unreachable. Not `error`: EventSource uses that name for its own connection failures |
| ↓ | `notify` | timer fired, reminder due — may arrive outside a turn |
| ↓ | `resync` | the client missed more than the replay buffer holds; reload history |

A native client says what it can do (`client`, `capabilities[]`) as query parameters on the
stream request, in place of the socket's `hello`.

### 3.2 One turn, end to end

1. Client VAD (Silero via `@ricky0123/vad-web`) detects speech and records the utterance.
2. On end of speech the client POSTs it; the server sends it to STT and emits the transcript as a
   `message`.
3. `ContextBuilder` assembles the prompt (§4.3); `AgentLoop` streams from Ollama.
4. Tool calls are executed (or held for confirmation) and the loop continues.
5. Streamed text passes through `EmotionTagParser` → `expression`/`gesture` events, clean text.
6. `SentenceChunker` cuts at sentence boundaries; each sentence goes to TTS as soon as it's
   complete, and an `audio` event announces it, so the avatar starts speaking after the **first
   sentence**, not the whole reply.
7. `interrupt` at any point cancels the turn's token: Ollama stream, pending TTS, and queued audio.
   The partial reply is saved as interrupted so the model knows it was cut off.

Latency budget on the 3060 (Gemma-class 12B, warm prefix, from muwbta's measurements):
STT ~0.3 s + TTFT ~0.3 s + first sentence (~20 tok @ 34 tok/s) ~0.6 s + first TTS chunk ~0.2 s
≈ **1.4 s voice-to-voice**. CPU-only is not a viable voice target (0.9 tok/s); text still works.

---

## 4. LLM

### 4.1 Model

Reuse the muwbta Ollama deployment pattern: a **derived model** with `num_ctx` baked in
(`tools/ollama/Modelfile.familiar`, built by a `create-models.sh` like muwbta's).

The base needs **native tool calling**. Gemma 3 12B (muwbta's `muwbta-builder` base) is not
tagged for tools in Ollama.

**Decided by the Phase 0 bake-off (2026-10-01): `gemma4:12b`** — 12/12 tool calls, no tool
leakage into speech, fitting emotion tags; ~395 ms to first token and ~37 tok/s on the 5070 Ti;
~8.9 GB VRAM at 8k context. The Qwen models answered tool requests in speech ("I've started a
timer!") without calling anything once the emotion-tag instruction was present; llama3.1:8b
called tools perfectly but conversed poorly. Full results: `tools/ollama/README.md`.

Two consequences for phase 1: send `think: false` (gemma4 is a thinking model), and keep the
system prompt's tag instruction scoped to spoken replies — and re-run the bake-off whenever that
prompt changes materially, since that's what broke the Qwen models.

### 4.2 Own Ollama container — **decided**

muwbta runs Ollama with `OLLAMA_NUM_PARALLEL=1` and `KEEP_ALIVE=-1` specifically so the builder's
~7k-token canon prefix stays in the one KV slot. Familiar chat traffic through the same instance
would evict that cache on every turn. So Familiar gets **its own Ollama container**
(`familiar-ollama`), tuned for conversation rather than for one big cached prefix:

- Host port **11435** in dev (muwbta's holds 11434); by service name on the compose network.
- `KEEP_ALIVE=-1` (voice can't wait for a model load), `FLASH_ATTENTION=1`.
- `NUM_PARALLEL` and `num_ctx` sized together in the Phase 0 bake-off: one slot gives the best
  prefix reuse for a single user; two lets a background job (memory extraction, summaries) run
  without blocking a live turn. Measure, don't guess.
- Generation + embedding model resident (`MAX_LOADED_MODELS=2`).

VRAM is the constraint: two LLMs resident on the NAS 3060 (12 GB) means ~8.4 GB for muwbta's 12B
plus Familiar's model plus speech won't fit; §10.3 has the measured numbers and the options. The
dev box's 16 GB has more room.

### 4.3 Prompt shape (prefix-cache discipline, from muwbta)

Built in one order, always — stable first, volatile last — so the KV cache is reused:

1–2. One system message: the persona (name, personality sliders rendered to fixed prose) and then
   the rules (speech style, emotion-tag grammar, later tool rules). Static per user. Identity comes
   first because models follow it better that way; the cost is that the cache isn't shared across
   accounts, which on a household server is a few hundred tokens.
3. Rolling conversation summary + last N turns
4. Long-term memories relevant to this turn (pgvector top-k)
5. Situation block (§6.3): time, next events, active timers
6. The user's message

Memories and situation sit after the history, not before it: they change every turn, and anything
after a changed token is re-evaluated, so putting them early would throw away the cached history
on every turn. Phase 1 does this with the time, prefixed to the user's message
(`PromptBuilder`).

### 4.4 Expressiveness

The model emits inline tags the server strips before TTS:

```
[joy:0.7] Oh, nice — you finished it! [gesture:nod] Want me to set a reminder for the follow-up?
```

- Emotion set maps 1:1 to VRM preset expressions: `joy`, `sad`, `angry`, `surprised`, `relaxed`,
  plus `neutral`, `thinking` (custom). Intensity 0–1.
- Server-side fallback: if a reply has no tags, a cheap rule/sentiment pass picks one, so a model
  that forgets the grammar degrades to "less expressive", not "broken".
- Non-verbal state is server-driven: `thinking` while waiting for TTFT, `using-tool` with a
  gesture while a tool runs, attentive idle while `listening`.

---

## 5. Avatar

### 5.0 The default: a plush llama

The first familiar is a **stuffed-toy llama**, drawn procedurally in SVG
(`client/src/avatar/PlushLlamaAvatar.ts`): a white fleece cloud (outlined puffs with stitched
curls, fuzzy-edged by an SVG displacement filter) framing a soft taupe face, a fringe down to the
brows, tall ears with taupe insides, lashed button eyes with felt lids, a rounded snout with a
Y-mouth, pink cheeks, sitting on a plump fleece body with its arms down at its sides, hooves out in
front and curls on its chest. The body breathes, slumps when it's sad and leans a little into a
tilt; the head bobs and turns on top of it. No assets, so it loads instantly, works offline and has
no licence to track.

Its ears carry much of the emotion — perked for joy and surprise, drooping for sadness, pinned
back when cross, flicking now and then while idle — on top of the shared expression parameters
(eyes, lids, brows, mouth, blush, tear, gaze) every renderer blends between. Gestures: nod,
shake, tilt, shrug (arms out), and a wave with its right arm.

**In 3D (2026-10-03, prototype).** `style: "3d"` in the appearance (the studio's Style switch)
draws the llama with three.js instead (`client/src/avatar/PlushLlama3D.ts`): a whole plush toy
sitting on the stage floor with a contact shadow - neck, chest, round bottom, folded hind legs,
forelegs standing in front - in shell fur (12 offset layers per furry shape, strands cut from a
tufted noise texture, darker at the roots, drooping slightly), over lumpy base shapes so the
outline is fleece. The face is the 2D llama's in depth: button eyes with hemispherical lids (the
upper closes for blinks and winks, a big smile swaps the buttons for stitched arches), thread
brows, and the mouth and cheeks painted onto front-projected textures from the 2D drawing. It
reuses `PlushCharacter`, so every expression, gesture and the lip-sync behave as in 2D, and its
body moves as the flat sitting bodies do (breathing, a sad slump, leaning into a tilt, a shrug,
legs kicking with a hop); the head also turns to look where the eyes would, and the right foreleg
waves. Same colours and
accessories. About 120 draw calls and 310k triangles; three.js loads only for 3D or VRM. No WebGL
falls back to the flat llama. The other characters get 3D versions next, in the same style.

### 5.1 3D: VRM

**VRM** (glTF-based, open spec) via `@pixiv/three-vrm`. It standardises exactly what we need:
expression presets (happy/angry/sad/relaxed/surprised), viseme blendshapes (`aa ih ou ee oh`),
blink, look-at, and humanoid bones for gestures. Users can make their own in **VRoid Studio**
(free) and upload them. Note that individual VRM models carry their own licence metadata — we
display it and respect it for presets we ship.

### 5.2 2D

Later, behind the same `AvatarDriver` interface. Avoid Live2D Cubism for the core (proprietary SDK
licence). Candidates: **Rive** (state machines driven by inputs — emotion, viseme, talking) or a
layered sprite "PNGtuber" renderer for the cheapest path.

### 5.3 The driver interface

`client/packages/avatar` is a **framework-agnostic TS package** (no React) so the Android app can
host the same renderer in a WebView if we go that way:

```ts
interface AvatarDriver {
  load(asset: AvatarAsset): Promise<void>;
  setState(s: 'idle' | 'listening' | 'thinking' | 'speaking' | 'using-tool'): void;
  setExpression(e: Emotion, intensity: number, blendMs?: number): void;
  playGesture(g: Gesture): void;
  setVisemes(track: VisemeFrame[], audioStartTime: number): void; // phase 3
  setMouthOpen(level: number): void;                              // amplitude fallback
  lookAt(target: 'user' | 'away' | Vec3): void;
}
```

### 5.4 Lip-sync

- **Phase 2:** amplitude-driven mouth from a WebAudio `AnalyserNode` on the TTS output. Works
  with any TTS engine, good enough to ship.
- **Phase 3:** phoneme/word timings from TTS → viseme track, scheduled against
  `AudioContext.currentTime`.

### 5.5 Customisation (per user, stored server-side)

| | |
|---|---|
| Model | Preset VRMs or upload (size-limited, validated as VRM 0.x/1.0) |
| Look | Material colour overrides, outfit variants where the model supports them |
| Name | What it's called, and what it calls you |
| Voice | TTS voice, speed, pitch; preview button |
| Personality | Sliders → persona prose: warmth, playfulness, verbosity, formality |
| Expressiveness | Intensity multiplier; gesture frequency; "calm mode" |
| Idle | Breathing, blink rate, idle-glance frequency |

---

## 6. Agentic features

### 6.1 Tool registry

Tools are C# methods registered with Microsoft.Extensions.AI, each declaring a **risk class**:

| Class | Behaviour | Examples |
|---|---|---|
| `read` | Runs immediately | get time, list timers, read calendar, weather |
| `write-own` | Runs immediately, announced, undoable | set/cancel timer, create reminder, save note |
| `write-external` | **Asks first** (`tool.confirm`, voice "yes" or tap) | create/move calendar events, send a webhook |

Every invocation lands in `tool_invocations` (args, result, who approved, duration).

**Where a tool runs.** Most tools run on the server. Some can only run on the device: set a
system alarm, read the phone's calendar, current location, Do Not Disturb state, launch an app.
A native client lists what it can do in its stream capabilities (§3.1); the server offers those tools
to the model only for that client, and executes them by sending `tool.invoke` and awaiting
`tool.result` (with a timeout, so a backgrounded phone fails the call instead of hanging the
turn). The web client advertises few or none. Same risk classes, same confirmation flow, same
audit row — the model can't tell the difference.

*Built (2026-10-03):* a phone opens the stream with `?capabilities=calendar.write` (device tokens
only; browsers are ignored) and gets a `connectionId` in `ready`. `create_event` asks the most
recently connected such phone first: `tool.invoke` `{ callId, connectionId, tool: "calendar.add",
arguments, expiresAt }`, answered by `POST /api/conversation/tool-result`. No answer within
`Devices:ToolTimeoutSeconds` (15) or a failure, and the event goes into Familiar's own calendar
instead, with the model told why. The tool keeps one name for the model either way.

### 6.2 Scheduler (timers & reminders)

Postgres-backed, no extra infrastructure: a `scheduled_jobs` table, a hosted service that sleeps
until the next due time (woken early by an in-process signal when a nearer job is inserted), and
claims due rows with `SELECT … FOR UPDATE SKIP LOCKED`. Survives restarts; a job due while the
server was down fires on startup marked late.

On fire: if the user has a live socket → `notify` and the avatar speaks it ("Your pasta timer's
done!"). Otherwise → push: **Web Push** (VAPID) for browsers, **FCM** for Android, **APNs** for
iOS, and optionally **UnifiedPush via a self-hosted ntfy** for Android phones without Google
services. Push necessarily leaves the house (Google/Apple relay it), so payloads are
**content-free wake-ups** — "job 123 is due" — and the app fetches the text over its own
authenticated connection.

On phones, a timer the user sets should also exist **on the device** (an exact alarm on Android,
AlarmKit on iOS), so it rings with no network at all. The server's job remains the record and the
announcement; MOBILE.md covers the device side.
Recurring reminders via RRULE (same grammar as calendar).

### 6.3 Situation awareness

A `SituationProvider` assembles a small block each turn from providers that each opt in:
local time/timezone and day part, next 1–3 calendar events, running timers and due reminders,
how long since the last conversation, device type (phone vs desk), optional weather, optional
location (device-supplied, opt-in, never stored beyond the session by default).

Also proactive: on connect, the familiar may open with something relevant ("Morning — your
dentist is at 10"). Rate-limited and toggleable.

### 6.4 Calendar

One server-side `ICalendarProvider` interface; the LLM sees **one normalised set of calendar
tools** whatever the provider (fewer, consistent tools matter for an 8B model's accuracy). Events
are synced into `calendar_events` so the situation block and proactive reminders don't depend on
the LLM deciding to call a tool.

| Order | Provider | Covers | Access |
|---|---|---|---|
| 1 | **Local** (Postgres) | Familiar's own events, offline, tests | read/write |
| 2 | **ICS subscription** (secret iCal URL) | Google, Outlook/M365, Outlook.com, iCloud, almost anything | **read-only**, polled |
| 3 | **CalDAV** | iCloud, Fastmail, Nextcloud, Radicale, Zoho, Yahoo, self-hosted | read/write |
| 4 | **Google Calendar API** (OAuth) | Google, and so the default Android phone calendar | read/write |
| ★ | **Device calendar** (Android `CalendarContract`, iOS EventKit) | Whatever accounts the phone already syncs: Google, Exchange/Outlook, iCloud, CalDAV | read/write, via device-side tools |
| — | Microsoft Graph | Outlook/M365 | only if needed; no CalDAV support there |

ICS-first gives every user situation awareness on day one with no OAuth. Writes go to the local
calendar, a CalDAV calendar, or Google.

**The native apps change the breadth question.** The phone's calendar store already aggregates
every account the user has signed into, including Outlook, which has no CalDAV. With the user's
permission the app uploads a rolling window of upcoming events (say 14 days) for the situation
block, and calendar writes go through a device-side tool. For phone users this is the broadest
option with the least setup, and it means the server-side Google/Graph integrations may never be
needed.

**MCP is the extension path, not the backbone.** Official calendar MCP servers exist (Google's
remote `calendarmcp.googleapis.com`, in developer preview; Microsoft's Work IQ Calendar, which
requires an M365 Copilot licence and an Entra tenant), plus community CalDAV ones. But the
scheduler and situation block need events *without* an LLM in the loop, each MCP server has its
own tool shapes, and preview APIs move. The Phase 7 MCP client can still add any of these for
users who want them, classified by the MCP tool annotations (`readOnlyHint` / `destructiveHint`)
into the risk classes in §6.1.

Credentials for external providers are encrypted at rest with ASP.NET Data Protection.

### 6.5 Hooks & extensibility

- **Outbound webhooks:** user-defined, fired by events (timer done, reminder, a tool) — the
  bridge to Home Assistant, n8n, etc.
- **Inbound triggers:** an authenticated endpoint that lets an external system push a
  `notify` ("the washer finished") for the familiar to say.
- **MCP client:** register MCP servers as tool sources, so new capabilities don't need a server
  build. muwbta already has `tools/Muwbta.Mcp` as prior art.

### 6.6 Memory

`memories` table with a pgvector embedding (`nomic-embed-text`, already pulled by muwbta's
`create-models.sh`). After each conversation, a background pass extracts durable facts
("prefers tea", "daughter's name is …"). Users can view, edit, and delete what it remembers —
a settings page, and by voice ("forget that").

---

## 7. Data model (first cut)

```
accounts            id, username(citext), email, password_hash, role, security_stamp, created_at
access_tokens       id, account_id, name, hash, last_used_at, expires_at      -- Android, scripts
devices             id, account_id, kind(web|android), push_endpoint/fcm_token, keys
familiars           id, account_id, name, user_display_name, persona(jsonb), voice(jsonb),
                    expressiveness(jsonb), avatar_asset_id
avatar_assets       id, owner_id null=preset, kind(vrm|rive|sprite), path, licence(jsonb), sha256
conversations       id, account_id, started_at, summary, summary_upto_message_id
messages            id(uuidv7), conversation_id, role, content, emotion, interrupted, created_at
tool_invocations    id, message_id, tool, args(jsonb), result(jsonb), status, approved_by, ms
scheduled_jobs      id, account_id, kind(timer|reminder|hook), due_at, rrule, payload(jsonb),
                    status, claimed_at, fired_at
calendar_sources    id, account_id, kind(local|caldav|google), config(encrypted)
calendar_events     id, source_id, external_id, starts_at, ends_at, title, rrule, ...
memories            id, account_id, text, embedding vector(768), source_message_id, created_at
webhooks            id, account_id, event, url, secret(encrypted), enabled
```

Conventions carried over from muwbta: snake_case enforced in code, `uuidv7()` keys, explicit
migrations applied at startup, warnings as errors, central package versions, `[LoggerMessage]`
logging, an `IClock` abstraction so the scheduler is testable with a manual clock.

---

## 8. Repository layout

```
familiar/
  src/
    Familiar.Domain/        entities, risk classes, emotion/gesture enums. No deps.
    Familiar.Agent/         AgentLoop, ContextBuilder, EmotionTagParser, SentenceChunker,
                            tool registry, SituationProvider, memory extraction
    Familiar.Speech/        STT / TTS clients (HTTP), behind ISpeechToText / ITextToSpeech
    Familiar.Persistence/   EF Core 10 + Npgsql + pgvector, migrations
    Familiar.Server/        ASP.NET Core: auth, REST, SSE conversation stream, scheduler, push
  client/
    src/                    React app: chat, voice, settings, avatar studio
    packages/avatar/        framework-agnostic AvatarDriver + VRM renderer
    Dockerfile              nginx serving the built client, proxying /api
  mobile/                   Kotlin Multiplatform: Android + iOS (MOBILE.md)
  tests/                    xUnit + Testcontainers; vitest on the client
  tools/ollama/             Modelfile.familiar, create-models.sh
  tools/backup/             backup.sh (dump → test-restore → keep)
  Dockerfile                the server image
  docker-compose.yml        local dev: postgres(pgvector), ollama, stt, tts, adminer
  docker-compose.gpu.yml    local dev GPU overlay (WSL /dev/dxg)
  deploy/
    docker-compose.nas.yml      TrueNAS stack, published images only
    docker-compose.nas.gpu.yml  NVIDIA toolkit reservation overlay
    .env.example
  .github/workflows/docker-build.yml
  docs/
```

See §10 for how these are used.

Speech sidecars, both OpenAI-compatible HTTP so they're swappable:
- **STT:** `faster-whisper` server (e.g. Speaches) — `small`/`distil` model on GPU.
- **TTS:** **Kokoro** (Kokoro-FastAPI) — good quality, fast, can return word timestamps for
  phase-3 lip-sync. **Piper** as the CPU-friendly fallback.

VRAM budget on the 3060 (12 GB): LLM 8B Q4 ~5–6 GB + whisper small ~1 GB + Kokoro ~0.5 GB —
fits, leaving room only if muwbta's model is *not* also resident (§4.2).

---

## 9. Security & privacy

- Auth follows muwbta's pattern (§2.1), written fresh, including the first-registered-account-is-
  admin bootstrap.
- Voice audio is **not stored** by default — transcripts only. Opt-in to keep audio.
- All memory and history user-visible and deletable; account deletion cascades.
- Tool confirmation for anything that leaves the box (§6.1). Prompt-injection posture: content
  from calendars, webhooks, and MCP tools is wrapped as data in the prompt and can't escalate a
  `write-external` tool past confirmation.
- Uploaded VRMs: size limit, validate glTF/VRM structure, serve from a separate path with
  `Content-Type` locked, never executed.
- Mic access needs a secure context: localhost in dev, TLS via the front proxy on the NAS
  (as muwbta does).

---

## 10. Deployment

Two targets, one set of images. Same split as muwbta: **local runs the code on the host, the NAS
runs only published images.**

### 10.1 Local (dev box, Windows + Rancher Desktop, RTX 5070 Ti)

`docker-compose.yml` runs the infrastructure; the server and client run on the host with
`dotnet run` and `npm run dev` for fast iteration and debugging.

**Port mappings never name a host address** — no `127.0.0.1:` or `localhost:` prefix, on any
compose file. A loopback-bound mapping listens on IPv4 only, and .NET on Windows resolves
`localhost` to `::1` first: every new connection waited ~2 s for the fallback (2,095 ms vs 4 ms,
measured), which pushed a spoken turn from 1.5 s to over 4. A port that shouldn't be reachable is
not published at all. The server's own connection strings use `127.0.0.1`.

| Service | Image | Host port | Notes |
|---|---|---|---|
| postgres | `pgvector/pgvector:pg18` | 5433 | 5432 is muwbta's. Volume at `/var/lib/postgresql` (PG 18 layout). |
| adminer | `adminer` | 8081 | 8080 is muwbta's. |
| ollama | `ollama/ollama` | **11435** | Own container and model volume, `KEEP_ALIVE=-1`. |
| stt | Speaches (faster-whisper), CUDA tag | 8001 | OpenAI-compatible `/v1/audio/transcriptions`. |
| tts | Kokoro-FastAPI, GPU tag | 8002 | OpenAI-compatible `/v1/audio/speech`, plus word timestamps. |
| server | *(host)* `dotnet run` | 5280 | |
| client | *(host)* Vite | 5273 | Proxies `/api` — one origin, so the cookie behaves as in prod. |

`docker-compose.gpu.yml` hands the GPU to ollama, stt, and tts via WSL's `/dev/dxg` +
`/usr/lib/wsl` mount + `LD_LIBRARY_PATH`, the mechanism muwbta's overlay already proved on this
box. Ports are all variables in `.env` so either project can move.

Mic access needs a secure context; `localhost` counts, so dev needs no certificate. Testing from
a phone on the LAN does — use the NAS stack or a dev cert for that.

### 10.2 Images

GitHub Actions builds and pushes on `main` and on `v*` tags (same shape as muwbta's
`docker-build.yml`): `candrews74/familiar-server` and `candrews74/familiar-client`, tagged
`latest`, branch, semver, and short SHA. Version comes from `git describe`. PRs build but
don't push.

- **Server image:** multi-stage, `sdk:10.0` build → `aspnet:10.0` runtime, non-root, `/health`
  and `/health/ready`, migrations applied at startup. Uploaded avatars on a mounted volume.
- **Client image:** Vite build → nginx, TLS with a self-signed cert at start (re-encrypted
  behind the NAS front proxy, as muwbta does), proxies `/api` to the server with buffering off,
  so the SSE stream flows; the server pings it well inside nginx's read timeout.

Speech and Ollama use their upstream images; nothing of ours to build. Pin their tags rather
than `latest` once Phase 0 settles versions, so a NAS pull can't change voice or model behaviour
underneath us.

### 10.3 NAS (TrueNAS SCALE, RTX 3060 12 GB)

`deploy/docker-compose.nas.yml` — published images only, no repo checkout needed except the
backup script and Modelfile:

| Service | Exposure | Notes |
|---|---|---|
| client | **7280:443** | muwbta has 7180. Front proxy terminates public TLS. |
| server | compose network only | Reached by client nginx. `mem_limit` set so the GC sizes to it. |
| postgres | not published | Network only; `docker exec` for psql. Never a loopback-only port mapping: it listens on IPv4 only and stalls clients that try `::1` first. |
| ollama | network only | Models on the **SSD pool** (mmap'd weights), `KEEP_ALIVE=-1`. |
| stt, tts | network only | |
| backup | — | Nightly dump, test restore, keep 30 — muwbta's backup pattern. |

`deploy/docker-compose.nas.gpu.yml` adds `deploy.resources.reservations.devices` (NVIDIA
toolkit, `count: 1`) to ollama, stt, and tts. A reservation isn't exclusive, so all three
share the card alongside muwbta's ollama — which makes VRAM the real budget:

| | VRAM |
|---|---|
| muwbta-builder (Gemma 3 12B Q4, 16k) | ~8.4 GB |
| Familiar LLM, `familiar-chat` (gemma4:12b, 8k) | ~8.9 GB (measured on the dev box) |
| whisper small/distil + Kokoro | ~1.5 GB |
| **Total** | **~18.8 GB on a 12 GB card — does not fit** |

**Decided (2026-10-01): muwbta's Ollama is turned off, and Familiar gets the 3060.** muwbta's
server already treats the assist as optional (it answers "not configured" with
`Assist__Enabled=false`), so nothing else in muwbta changes. That leaves:

| | VRAM |
|---|---|
| `familiar-chat` (gemma4:12b, 8k) | ~8.9 GB |
| whisper small/distil + Kokoro | ~1.5 GB |
| **Total** | **~10.4 GB of ~11.5 GB usable** |

About 1 GB of headroom, so measure on the real card before relying on it; if it's tighter than
the dev box suggests, speech moves to CPU first (Kokoro and whisper-small are tolerable there; the
LLM isn't). The overlay is written so each service's GPU access can be toggled independently.

Nothing depends on ollama, stt, or tts being up: the server starts without them and degrades to
text-only / "the familiar is resting", the same rule muwbta applies to its assist.

### 10.4 Reaching the NAS from a phone — **decided: VPN**

Phones leave the house; they reach the NAS over a **VPN** (Tailscale or WireGuard). Nothing new is
exposed to the internet, and the app just uses the NAS's VPN address. Public exposure through the
front proxy stays possible later — it would need a real certificate and the SSE stream let
through unbuffered — but nothing is built for it now.

The app takes a server URL at first launch, so the same build works against the NAS over the VPN
and against the dev box on the LAN during development.

---

## 11. Phases

**Status (2026-10-01):** Phase 0 scaffold is in: solution, auth (browser + device tokens), local
compose + GPU overlay, Dockerfiles, CI, NAS compose, backup sidecar, bake-off tool. Images are
on Docker Hub, and the dev-box bake-off chose `gemma4:12b`. Still open in Phase 0: NAS deploy,
bake-off and VRAM split on the 3060.

**Phase 1 (2026-10-01):** built. SSE + POST conversation (§3.1), streamed replies from
`familiar-chat`, persona from the profile sliders, emotion and gesture tags parsed out of the
stream, and the built-in **plush llama** (SVG, asset-free) reacting to them, ears and all; a VRM renderer behind the same driver loads
when a model is dropped at `client/public/avatars/familiar.vrm`. Two deviations from this plan:
the situation (time) rides on the last user message, not in the system prompt (§4.3, so the cached
prefix survives), and the avatar lives in `client/src/avatar/` (framework-free) until the phone
apps need it as a package. The VRM path is untested until there's a model to test it with.

**Phase 2 (2026-10-01):** built, and the "done when" is met. Push-to-talk records an utterance and
POSTs it; Speaches (faster-distil-whisper-small.en) transcribes it; the reply is cut into sentences
as it streams and each goes to Kokoro, announced by an `audio` event and fetched by URL; the client
plays the clips in order through Web Audio and drives the llama's mouth from the measured
loudness. Pressing talk interrupts the familiar. Voice and speed are per-familiar settings with a
preview; "Voice on/off" is per device. Measured on the 5070 Ti, end of speech to first spoken
sentence ready: **1.3–1.5 s warm** (STT ~250 ms, first words ~1 s, TTS ~70 ms per sentence).
Findings: Kokoro needs its cu128 image on Blackwell; Docker ports must not be bound to loopback
(§10.1); the persona has to say it's a llama or the model denies it. The STT model downloads itself
at startup (`SpeechWarmUp`). The microphone UI itself is untested by automation (no mic in the
test browser); the spoken path was tested with a synthesised WAV through the same endpoint.

**Phase 3 (2026-10-01):** built. *Hands-free* runs Silero VAD (v5, `@ricky0123/vad-web`) in the
browser and sends each utterance as WAV when the user stops talking; the model and ONNX runtime
(~17 MB, copied to `public/vad/` at build) load only when it's switched on. *Barge-in*: speech
while the familiar talks stops playback at once and interrupts with what the user actually heard
(finished sentences plus the words of the current one, from Kokoro's timings); the server cuts the
stored reply to that and broadcasts `reply.truncated` — because the model has usually finished
*writing* long before the familiar finishes *saying*, an interrupt that only cancelled generation
would leave a reply the user never heard. While the familiar talks the VAD needs firmer speech,
and the server drops a transcript that is mostly the familiar's own last reply (echo through the
speakers). *Mouth shapes*: Kokoro's `/dev/captioned_speech` gives per-word timings at no extra
cost (~75 ms per sentence either way); the client maps the letter being spoken to one of seven
shapes (aa ih ou ee oh closed teeth) scaled by loudness — VRM's presets directly, the llama's
mouth by opening/width. Verified: live barge-in truncation; visemes on real Kokoro audio in a
browser (all seven shapes, 39 distinct mouth forms in one sentence); VAD on synthesised speech
(start 0.4 s in, end 0.55 s after). Not verified with a real microphone (none in the test
browser) — that's the thing to try first.

**Phase 4 (2026-10-02):** built. *Tools*: Ollama's native tool calling; the turn runs a tool round
loop (at most four rounds, the last offered no tools so the model has to answer) and anything said
before a call stays part of the reply. Four tools: `set_timer`, `create_reminder`,
`list_scheduled`, `cancel_scheduled` (matched by label; asks "which one?" when ambiguous). Every
call is audited in `tool_invocations`; `WriteExternal` tools ask first (`tool.confirm`, answered
from any device, 60 s then no) — none exist yet, the flow is tested with a fake. The client shows
tool chips on the reply ("Timer: pasta, 10 minutes") and a live timers panel under the familiar
with countdowns and cancel. *Scheduler*: jobs in Postgres, claimed with `FOR UPDATE SKIP LOCKED`;
the loop sleeps until the next due job (≤ 30 s, woken early by new ones), and a job that came due
while the server was down fires on startup and says it's late. Firing is an ordinary reply from the
familiar (stored, spoken, gesture, a chime) plus a *content-free* Web Push: VAPID only, no payload,
so the vendor's push service learns nothing; the service worker fetches
`/api/notifications/recent` and stays quiet if a familiar window is visible. Findings: worded
politely, the tools rule got list/cancel calls but **0 of 15** "set a timer" requests — the model
just said "I've set your timer"; naming the tools as the only way anything happens fixed it (15/15,
chit-chat still calls nothing). Given "23:55" the model misread the time, so tool results speak a
12-hour clock and timers report only their length. Verified live with familiar-chat: timer set by
voice-style request, chip, countdown, announced on time. **Not yet verified:** a real push to a
closed tab — the test browser denies notifications; try it in Chrome over `localhost` or trusted
https (a self-signed certificate the browser doesn't trust blocks service workers).

**Phase 5 (2026-10-02):** built, and the "done when" is met live with familiar-chat: a test user
said "I'm Sam… my daughter Maya is eight" and asked for a dentist appointment tomorrow; four minutes
later the memory pass had stored their name, job and daughter; with the clock "a day on", opening
the app got *"Hey Sam, it is so good to see you again! I know you have that dentist appointment
tomorrow morning at ten."* *Memory*: `memories` with pgvector embeddings (nomic-embed-text); a
background pass reads a conversation once it's been quiet 3 minutes (structured JSON output) and
stores facts unless already known — near-duplicates are cosine ≥ 0.88, measured; `remember` /
`forget` tools (forget matches words, then meaning with a margin, else asks which); Settings lists,
edits and forgets. Memories sit at the *end* of the system message, so a new one doesn't cost the
cached persona and rules. *Calendars*: Familiar's own (read/write) and subscribed iCal feeds
(read-only, Ical.Net expands RRULE/EXDATE/time zones 60 days ahead, refreshed every 30 min; the
URL is encrypted with Data Protection and never sent back). CalDAV/Google writes are deferred:
§12 item 3 is still open, and the phone apps' device calendar may make them unnecessary.
*Device calendars* (2026-10-03): a phone uploads its next 14 days to `PUT /api/calendars/device`
(one source per signed-in device, replaced wholesale, left out of the situation after 3 days
without a sync, deleted with the device), and new events go to the phone while it's connected
(§6.1).
*Situation*: each turn's notes carry the next events, running timers, near reminders, and how long
since you last talked. *Greeting*: a fresh connection after 3 quiet hours gets a hello first, once
per gap across devices, switchable per familiar. Findings: the model does weekday arithmetic badly
("Tuesday" from a Thursday came back as Monday's date), so tools take "Tuesday 15:00" / "next
Wednesday" and the server works out the date — `create_event` takes `day` and `time` separately for
the same reason. Weather and location (§6.3) aren't done: they need a provider and the phone apps.

**Phase 6 (2026-10-02):** built. A **Studio** page with a live preview (buttons to try every
expression and gesture) next to the controls. The plush llama takes five colours — fleece, face,
ears, cheeks, accessory — from six presets or colour pickers, with outlines, stitching and nose
derived so any choice reads (dark fleece gets lighter outlines), and wears a bow (on an ear, so it
flops with it), scarf, glasses or flower. Or upload a **VRM model**: validated as binary glTF with
the VRM 0.x/1.0 extension, its title, authors and licence read and shown, stored in Postgres (so
backups have it; ≤ 40 MB, 5 per account), served only to its owner; deleting the model in use puts
the llama back. Appearance is JSON on the profile, whitelisted server-side (colours must be
`#rrggbb`: they go into CSS). This replaces Phase 1's "drop a file at
`client/public/avatars/familiar.vrm`". Verified in a browser: presets, pickers, every accessory,
save, and the saved look on the home page. **Not done:** the VRM path has still never rendered a
real model (none to hand — the upload/validation side is tested with synthetic GLBs); a 2D
Rive/sprite renderer (§5.2) — the llama already is the 2D renderer, and Rive needs art; voice
pitch (Kokoro has none); idle tuning (blink rate, calm mode).

*More characters (2026-10-02):* a **fox** (swivelling pointed ears, a bushy tail that swishes when
happy), a **hedgehog** (two layers of prickles that bristle when startled or cross and lie flat when
sad), a **fuzzy monster** (googly eyes, stubby horns, fur that stands on end) and the **spot** — no
face at all: it stays where it is and shows feeling with shape (swelling, sagging, spikes, lobes),
surface (glossy or matte, smooth or frayed, a glow) and colour (the user's colour tinted by mood:
warm when happy, blue when sad, red when cross, violet when thinking); speech makes it pulse, and the
gestures are done in place (a wave is a pseudopod). The llama's animation became a shared engine
(`client/src/avatar/plush/`) that all of them run on; each character is just its drawing. Each has
its own presets, colour labels and accessories (the spot wears none), and the persona says what the
user picked — asked what it is, the hedgehog said "a little plush hedgehog with soft prickles".

*Sitting (2026-10-02):* the four plush characters now sit, whole, like the toys they're after: a
torso, hind feet out in front, arms at the sides (the right one waves; both go out for a shrug),
the fox's tail curling up from its hip, the hedgehog a ball on two little feet. A shared body
(`plush/parts.ts`: `buildBody`, `drawBody`) breathes with the head's bob, slumps when sad, leans a
little into a tilt and kicks its feet up with each hop of joy. The stage no longer fades its bottom
edge; the spot is unchanged.

**Phase 7 (2026-10-02):** built, and the "done when" is met live: a Home Assistant-style
`rest_command` POST with a hook token made the familiar announce "The washing machine has
finished." on the open stream (spoken, with the chime); with `"style": "tell"` it passed on "Someone
is at the front door" in its own words. *Inbound*: announce-only hook tokens (`fhk_…`, hashed like
device tokens, revocable); Settings shows the token once with a ready-to-paste `rest_command`.
Announcements also push-notify, through an in-memory recent list the service worker reads. "Tell"
quotes and attributes the message and offers no tools, so a message can only be passed on, never
acted on. *Outbound*: webhooks for `timer.done`, `reminder.due`, `reply`, `tool.used` — queued,
retried twice, signed `X-Familiar-Signature: sha256=<HMAC>`, URL and secret encrypted; last result
and a test send in Settings. *MCP client*: the official C# SDK over streamable HTTP; a server's
tools are listed, switched on per tool, and offered as `<server>_<tool>` (at most 8 across servers,
for the small model's sake); anything not marked read-only asks first. Tools are now per account.
Verified against a real MCP server (the SDK's own, in-process): read tool runs, write tool asks,
switched-off tool isn't offered. **Not verified:** against Home Assistant's own MCP server (none to
hand); outbound URLs may be on the LAN by design (Home Assistant lives there), with loopback and
link-local refused.

| Phase | Delivers | Done when |
|---|---|---|
| **0. Scaffold** | Solution, auth, local compose + GPU overlay, Dockerfiles, CI image build, NAS compose, model bake-off, NAS VRAM measurement | Register/login works locally **and** from the published images on the NAS; `ollama show` confirms tool capability on the chosen model |
| **1. Text + avatar** | SSE stream + POST, streaming chat, persona, emotion tags, VRM avatar with idle + expressions | Typed chat makes the avatar visibly react with the right mood |
| **2. Voice** | Push-to-talk → STT; sentence-streamed TTS; amplitude lip-sync; interrupt button | Spoken round-trip under ~2 s on GPU |
| **3. Hands-free** | Client VAD, barge-in cancellation, viseme lip-sync from TTS timings | Talking over it stops it mid-sentence cleanly |
| **4. Tools & scheduler** | Timers, reminders, scheduler, Web Push, confirmation flow, audit | "Set a 10-minute timer" fires with the tab closed |
| **5. Awareness & memory** | SituationProvider, local + ICS calendars → CalDAV/Google writes, memory + memory UI | It brings up your next meeting unprompted, and remembers your name tomorrow |
| **6. Customisation studio** | Avatar studio: model upload, colours, voice preview, personality sliders; 2D renderer | A user makes a familiar that's clearly theirs |
| **7. Hooks** | Outbound webhooks, inbound triggers, MCP client | Home Assistant can make it announce something |
| **Android** | See [MOBILE.md](MOBILE.md). Server side: stream capabilities, device-side tools, FCM, device calendar sync | — |
| **iOS** | See [MOBILE.md](MOBILE.md). Server side: APNs | — |

---

## 12. Open decisions

1. ~~Ollama sharing with muwbta~~ — **decided: separate container** (§4.2).
2. ~~Model choice~~ — **decided: `gemma4:12b`** (§4.1). On the NAS, depends on item 5.
3. **Calendar write provider** — ICS read is first regardless (§6.4); CalDAV or Google API
   for writes depends on which calendars users actually keep.
4. ~~Deployment target~~ — **decided: local dev + published images on the NAS** (§10).
5. ~~NAS VRAM split~~ — **decided: muwbta's Ollama off, Familiar owns the 3060** (§10.3).
   Still to measure on the card itself.
6. **Licence** — no muwbta code is copied (§2.1), so it's an open choice. AGPL-3.0 to match
   muwbta, or something permissive.
7. ~~Android approach~~ — **decided: native** (MOBILE.md).
8. ~~Remote access~~ — **decided: VPN** (§10.4).
