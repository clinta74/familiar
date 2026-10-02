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
  `Channel<T>` for per-conversation pipelines, WebSockets, cancellation everywhere (barge-in is a
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
 Browser (React)                                 Server (.NET 10)                      Sidecars
 ┌──────────────────────┐   WebSocket /ws   ┌──────────────────────────────┐
 │ mic → VAD → opus/pcm ├──────────────────►│ ConversationSession (per WS) │──HTTP──► STT  (faster-whisper)
 │                      │                   │   ├ STT client               │
 │ audio player  ◄──────┤◄─ audio chunks ───┤   ├ AgentLoop (LLM + tools) ─┼──HTTP──► Ollama
 │ avatar (three-vrm) ◄─┤◄─ expression,     │   ├ EmotionTagParser          │
 │   visemes, gestures  │   viseme, state   │   ├ SentenceChunker → TTS ───┼──HTTP──► TTS  (Kokoro)
 │ chat transcript   ◄──┤◄─ text deltas ────┤   └ ContextBuilder           │
 └──────────────────────┘                   │                              │
          ▲  REST /api (auth, avatar,       │ Scheduler (hosted service)   │──► Web Push / FCM
          │  history, settings)             │ Tool registry + hooks        │──► CalDAV, webhooks, MCP
          └─────────────────────────────────┤ EF Core ─────────────────────┼──► Postgres 18 + pgvector
                                            └──────────────────────────────┘
```

### 3.1 Transport

**WebSocket** for the live conversation, **REST** for everything else.

muwbta uses SSE + POST because traffic is mostly server→client text. Voice is genuinely
bidirectional and continuous: mic audio streams up while TTS audio streams down, and the user
speaking over the avatar (barge-in) must cancel generation immediately. One socket makes that a
single cancellation token instead of a race between two channels.

- **Web:** same-origin HttpOnly cookie (Vite proxies `/api` and `/ws` in dev — same reasoning as
  muwbta's README "Why the client proxies").
- **Android:** personal access token as `Authorization: Bearer` on the WebSocket upgrade (OkHttp
  can set headers; browsers can't, which is why web keeps the cookie).

Message envelope (JSON text frames; audio as binary frames with a 1-byte type prefix):

| Direction | Type | Payload |
|---|---|---|
| ↑ | `audio` (binary) | 16 kHz mono PCM or Opus frames while VAD says speech |
| ↑ | `utterance.end` | VAD end-of-speech |
| ↑ | `text` | typed message |
| ↑ | `interrupt` | user started speaking / pressed stop |
| ↑ | `confirm` | `{ toolCallId, approved }` |
| ↑ | `hello` | `{ client: web\|android\|ios, version, capabilities[] }` — first frame |
| ↑ | `tool.result` | result of a device-side tool (§6.1) |
| ↓ | `state` | `listening` · `thinking` · `speaking` · `idle` · `using-tool` |
| ↓ | `transcript` | partial/final user transcript |
| ↓ | `reply.delta` | assistant text (tags stripped) |
| ↓ | `expression` | `{ emotion, intensity, at }` |
| ↓ | `gesture` | `{ name, at }` — nod, wave, shrug, think, … |
| ↓ | `audio` (binary) | TTS chunk, sequence-numbered |
| ↓ | `visemes` | `[{ v, t, d }]` aligned to the audio chunk (phase 3) |
| ↓ | `tool.confirm` | ask the user before a side-effecting tool runs |
| ↓ | `tool.invoke` | run a device-side tool on this client (§6.1) |
| ↓ | `notify` | timer fired, reminder due — may arrive outside a turn |

### 3.2 One turn, end to end

1. Client VAD (Silero via `@ricky0123/vad-web`) detects speech, streams audio up.
2. On `utterance.end`, server sends the buffer to STT → `transcript`.
3. `ContextBuilder` assembles the prompt (§4.3); `AgentLoop` streams from Ollama.
4. Tool calls are executed (or held for confirmation) and the loop continues.
5. Streamed text passes through `EmotionTagParser` → `expression`/`gesture` events, clean text.
6. `SentenceChunker` cuts at sentence boundaries; each sentence goes to TTS as soon as it's
   complete, so the avatar starts speaking after the **first sentence**, not the whole reply.
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

1. System: Familiar's base behaviour, tool rules, emotion-tag grammar (static, global)
2. Persona: this user's familiar — name, personality sliders rendered to prose (static per user)
3. Long-term memories relevant to this turn (pgvector top-k)
4. Situation block (§6.3): time, next events, active timers
5. Rolling conversation summary + last N turns
6. The user's message

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
A native client lists what it can do in its `hello` capabilities; the server offers those tools
to the model only for that session, and executes them by sending `tool.invoke` and awaiting
`tool.result` (with a timeout, so a backgrounded phone fails the call instead of hanging the
turn). The web client advertises few or none. Same risk classes, same confirmation flow, same
audit row — the model can't tell the difference.

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
    Familiar.Server/        ASP.NET Core: auth, REST, /ws, scheduler, push
  client/
    src/                    React app: chat, voice, settings, avatar studio
    packages/avatar/        framework-agnostic AvatarDriver + VRM renderer
    Dockerfile              nginx serving the built client, proxying /api and /ws
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

| Service | Image | Host port | Notes |
|---|---|---|---|
| postgres | `pgvector/pgvector:pg18` | 5433 | 5432 is muwbta's. Volume at `/var/lib/postgresql` (PG 18 layout). |
| adminer | `adminer` | 8081 | 8080 is muwbta's. |
| ollama | `ollama/ollama` | **11435** | Own container and model volume, `KEEP_ALIVE=-1`. |
| stt | Speaches (faster-whisper), CUDA tag | 8001 | OpenAI-compatible `/v1/audio/transcriptions`. |
| tts | Kokoro-FastAPI, GPU tag | 8002 | OpenAI-compatible `/v1/audio/speech`, plus word timestamps. |
| server | *(host)* `dotnet run` | 5280 | |
| client | *(host)* Vite | 5273 | Proxies `/api` and `/ws` — one origin, so the cookie behaves as in prod. |

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
  behind the NAS front proxy, as muwbta does), proxies `/api` and upgrades `/ws` to the server.
  Long `proxy_read_timeout` on `/ws`, or nginx closes idle voice sessions at 60 s.

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
| postgres | 127.0.0.1:5435 (optional) | Loopback only, like muwbta. |
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
front proxy stays possible later — it would need a real certificate and the WebSocket upgrade let
through — but nothing is built for it now.

The app takes a server URL at first launch, so the same build works against the NAS over the VPN
and against the dev box on the LAN during development.

---

## 11. Phases

**Status (2026-10-01):** Phase 0 scaffold is in: solution, auth (browser + device tokens), local
compose + GPU overlay, Dockerfiles, CI, NAS compose, backup sidecar, bake-off tool. Images are
on Docker Hub, and the dev-box bake-off chose `gemma4:12b`. Still open in Phase 0: NAS deploy,
bake-off and VRAM split on the 3060.

| Phase | Delivers | Done when |
|---|---|---|
| **0. Scaffold** | Solution, auth, local compose + GPU overlay, Dockerfiles, CI image build, NAS compose, model bake-off, NAS VRAM measurement | Register/login works locally **and** from the published images on the NAS; `ollama show` confirms tool capability on the chosen model |
| **1. Text + avatar** | WebSocket, streaming chat, persona, emotion tags, VRM avatar with idle + expressions | Typed chat makes the avatar visibly react with the right mood |
| **2. Voice** | Push-to-talk → STT; sentence-streamed TTS; amplitude lip-sync; interrupt button | Spoken round-trip under ~2 s on GPU |
| **3. Hands-free** | Client VAD, barge-in cancellation, viseme lip-sync from TTS timings | Talking over it stops it mid-sentence cleanly |
| **4. Tools & scheduler** | Timers, reminders, scheduler, Web Push, confirmation flow, audit | "Set a 10-minute timer" fires with the tab closed |
| **5. Awareness & memory** | SituationProvider, local + ICS calendars → CalDAV/Google writes, memory + memory UI | It brings up your next meeting unprompted, and remembers your name tomorrow |
| **6. Customisation studio** | Avatar studio: model upload, colours, voice preview, personality sliders; 2D renderer | A user makes a familiar that's clearly theirs |
| **7. Hooks** | Outbound webhooks, inbound triggers, MCP client | Home Assistant can make it announce something |
| **Android** | See [MOBILE.md](MOBILE.md). Server side: `hello` capabilities, device-side tools, FCM, device calendar sync | — |
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
