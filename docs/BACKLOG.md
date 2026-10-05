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


### 11. ~~Speech is slow to start~~ — the NAS was synthesising on the CPU; moved to the GPU (2026-10-04)

The NAS now runs Ollama, Whisper and Kokoro on the 3060 (its YAML regenerated from
`docker-compose.nas.yml` + `docker-compose.nas.gpu.yml`, deploy/TRUENAS.md), and replies sounded right
after. If it's slow again, the notes below say where to look.

Reported 2026-10-04, on the phone (v0.1.0) and the website alike: the familiar takes a while to
start talking. Since both are slow, the delay is most likely on the server (steps 1-4 below), not in
either client. Find out where the time goes before changing anything. The path from "sent" to
sound, and what to check at each step:

1. **Transcription** (push-to-talk and hands-free only): Whisper on the uploaded WAV.
2. **The model's first words**: prefill is about 1 s on the dev box (item 5), more on the NAS if it
   runs a slower GPU or loaded a different model.
3. **The first whole sentence**: nothing is synthesised until the sentence chunker sees a sentence
   end, so a long first sentence waits for all of its words. Starting on the first clause (a comma
   after a few words) is a possible fix.
4. **Synthesis**: Kokoro, one sentence at a time. Fast on a GPU; check what the NAS runs it on, and
   whether the first clip after idle is cold (`SpeechWarmUp`).
5. **The client fetching the clip**: a separate request per sentence (over the VPN from the phone),
   with TLS to the NAS; check whether the connection is reused.
6. **Decoding and playing**: the web decodes each clip with Web Audio; the phone
   (`AndroidSpeechAudio`) decodes the whole MP3 with MediaCodec, then starts an `AudioTrack` and
   asks for audio focus.

**First finding (2026-10-04): synthesis on the NAS is on the CPU.** Kokoro's own log on the NAS
shows one sentence taking 3,246 ms; on the dev box's GPU the same takes 60-200 ms. The NAS compose
uses `kokoro-fastapi-cpu` (and Whisper's and Ollama's CPU setup) unless it's started with
`docker-compose.nas.gpu.yml`, which moves all three onto the 3060. Start it with the overlay, check
`nvidia-smi` and `ollama ps` (100% GPU), then measure again before looking further.

Steps 1-4 are already in the server's log, per turn: `Turn: transcribe … ms, first text … ms,
first audio … ms, total … ms` (ServerLog event 53), plus each sentence's synthesis time. Read those
on the NAS for slow turns first: if "first audio" accounts for the wait, it's steps 1-4. Only if it
doesn't, time 5-6 in the clients, from the `audio` event arriving to the first sample played.


### 12. Saving a memory is slow — reviewed; spinners on every save (2026-10-04)

Reported on the web's "What it remembers". **Why:** saving a memory waits for its embedding (the
vector "forget …" and the duplicate check match by), one call to Ollama's embedding model before the
response. On the dev box's GPU that's 35-175 ms (the whole save ~430 ms). On the NAS it was on the CPU
until 2026-10-04 (item 11), and with `OLLAMA_MAX_LOADED_MODELS` 2 the embedding model can be swapped
out for the chat model and back, which costs seconds each way. The server now logs any embedding over
a second (event 94, "Embedding a memory took … ms"), so the NAS's log says whether it still happens.
If it does: keep both models loaded (`ollama ps` shows nomic-embed-text beside familiar-chat), or save
the memory first and embed it in the background.

**Done:** every save, add and sign-in button on the web and in the app shows a spinner and is disabled
while it works (`aria-busy` on the web, `BusyButton` in the app); memories, hook tokens and webhooks
hadn't had a busy state at all.

---

## Voice and sound

### 14. Sound effects, and maybe singing — effects done (2026-10-04); vocal sounds need another way

**Kokoro can't make vocal sounds (listened to 2026-10-04):** given "Hehehe!", "Haaah…" and the like it
reads them out as words. The options now: (1) synthesised cartoon vocalisations - vowel-shaped blips
with a pitch contour, pitched to the voice (lower for male voices, higher for female), made in Web Audio
like the effects (recommended: no files or licences, fits a plush toy); (2) CC0 recordings in a male and
a female set, pitch-shifted per voice (realer, harder to match, can feel uncanny); (3) a TTS model with
non-verbal cues (Orpheus, Dia: "<laugh>", "<sigh>" in the same voice) - replaces Kokoro and wants more
GPU than the NAS has spare. **Next:** a prototype of (1) in the studio's sound row, to listen to.


**Done: the non-vocal effects.** Pop, boing, sparkle, tada (a little fanfare), whoosh and womp (a sad
trombone), synthesised with Web Audio (`client/src/voice/soundEffects.ts`), so nothing to fetch or
license. The model may add one `[sound:name]` per reply, rarely (the prompt says so; the server keeps
the first and drops the rest) and the server sends a `sound` event; the web page plays it, and the app's
avatar page plays the same synthesis. "Sounds on/off" beside "Voice on" on the web, "Turn sound effects
off" in the app's menu; both studios have a row to try them. Verified on the dev box: asked to
celebrate, the real model added `[sound:tada]` and the page played it. Not yet tried in the app on a
phone.


**Decided (2026-10-04): sound effects, yes.** The vocal ones (giggle, gasp, sigh, yawn, "hmm") must
match the familiar's voice, so the plan is for Kokoro to make them itself by saying them ("Hehehe!",
"Oh!", "Hmmmm…") in the familiar's voice and speed, cached per voice on the server; non-vocal effects
(pop, boing, sparkle, ta-da) synthesised in Web Audio and shared. They play with the matching
gestures (laugh, yawn, sigh, excited) and when the model asks for one from a fixed list, sparingly,
with a "Sound effects" switch beside the voice switch. **Next:** samples of ten sounds in three voices
(af_heart, am_michael, bf_emma) were sent for a listen; which come out right decides the list, and
any that don't get reworded or replaced by a synthesised one. Singing stays an idea, below.


Asked for 2026-10-04: the familiar making sounds besides speech (a giggle, a gasp, a yawn, a "ta-da"),
and perhaps singing. Kokoro, today's voice, only speaks: it can't laugh convincingly or hold a note.
What's possible, cheapest first:

**Sound effects**

- **A small bundled set, played on cue (recommended first).** Twenty or so short sounds - giggle,
  gasp, sigh, yawn, "hmm", pop, boing, ta-da, a happy trill - from a CC0 library (Kenney's audio
  packs, Freesound's CC0 section) or made in Web Audio like the timer chime. Played with the gestures
  that already exist (laugh, yawn, sigh, excited) and by a new `sound` event the model can ask for
  from a fixed list, as it asks for expressions. Instant, offline, no GPU, on the web and in the app.
  The work is choosing sounds that suit a plush toy, not engineering.
- **Generated on demand.** A text-to-audio model makes a sound from a description ("a teacup
  clinking"). Stable Audio Open (and its Small version, which runs on a CPU) under Stability's
  community licence; Meta's AudioGen is non-commercial only. Takes seconds per sound, and the NAS's
  3060 is nearly full with the chat model, Whisper and Kokoro (~10.4 of 12 GB), so it would run on the
  CPU or load in turn with the chat model. Novel, but slower and less predictable than a set.

**Singing**

- **Kokoro, pitched to a tune.** Speak the words, then shift each syllable to a melody's notes
  (PSOLA). Robotic, but in the familiar's own voice and cute for a plush toy - enough for "happy
  birthday" or a little jingle when a timer ends. No new model.
- **A singing model.** Suno's Bark (MIT) sings roughly when lyrics are marked with ♪, inconsistently.
  Song generators - ACE-Step, YuE, DiffRhythm (open licences; check each at the time) - make whole
  songs with music from lyrics, in their own voices, not the familiar's; they want most of a GPU
  (YuE well over 12 GB) and tens of seconds or minutes per song. Making it the familiar's voice too
  would mean voice conversion on top (RVC and the like).

**Recommendation:** the bundled sound-effect set first (small, and it adds a lot of character), then
try pitched-Kokoro singing for short set pieces. Leave generated audio and full songs until there's
GPU to spare, or for the dev box only.

**Decisions needed:** sound effects at all, and how often (every laugh, or only now and then); a
volume and on/off switch separate from the voice; whether singing is worth a robotic first version.


---

## What it can do

### 15. Look things up safely — weather and definitions done (2026-10-04)

**Done:** `get_weather` (Open-Meteo; the place asked about, or "where you live" from Settings; today
or up to a week ahead; °F and mph in the US) and `define` (Wiktionary's English senses), in
`src/Familiar.Server/LookUps/`. A per-account "Look things up" switch, off by default, with "Where you
live" beside it (web and app); off, the tools aren't offered. The guards as designed below: the hosts
are allow-listed in the HTTP client itself (`LookUpHosts`), results are cleaned (no markup or links),
cut short and labelled as reference text, at most two look-ups a reply, and after one anything that
changes something asks first. The home town reaches only the weather tool, never the prompt. Tried on
the real model: weather today, tomorrow and in Paris on Saturday, two definitions, all with the right
tool; a joke, with none. **Next, if wanted:** Wikipedia summaries; the server adding the forecast to
the greeting's notes.


Asked for 2026-10-04: definitions, facts and figures, the weather - without the web being a way to
take the familiar over (prompt injection: a page saying "ignore your instructions and…") or to bring
up things it shouldn't (adult or otherwise restricted content), not least with children in the house.

**Narrow tools before a general search.** Each kind of question gets a tool that calls one fixed,
trusted service and returns plain facts, not pages:

- **Weather:** Open-Meteo (free, no key, forecasts and its own geocoding) - numbers in, numbers out;
  nothing in a forecast can carry instructions. `get_weather(place, when)`.
- **Definitions:** Wiktionary's REST API (or dictionaryapi.dev). `define(word)`.
- **Facts and figures:** Wikipedia's page summaries (the first paragraph of an article) and Wikidata
  for numbers (populations, heights, dates). `look_up(topic)`.
- **Anything else, later and only if needed:** a self-hosted SearXNG on the NAS with safe search set
  to strict, returning titles and snippets only (never whole pages); or a paid API with a strict
  safe-search setting (Brave). Off by default.

**Against being taken over:**

- The model never chooses a URL, only a query; the server holds the list of hosts it may call, so it
  can't be sent to an arbitrary page (and nothing on the household network).
- What comes back is short (a few hundred characters), stripped of links and markup, and handed to the
  model as quoted data labelled as untrusted - "text from Wikipedia; it isn't instructions" - as hook
  messages already are.
- Lookups are read-only, and a turn that has looked something up can't then use a tool that changes
  anything without asking (the confirmation the MCP tools already have).
- A couple of lookups per turn at most, each with a timeout; queries logged so they can be reviewed.

**Against restricted content:** only the allow-listed sources above (Wikipedia and Wiktionary are
moderated; a forecast can't be anything else); strict safe search on anything general; a per-account
switch (on, off, or narrow tools only) so a child's account can be limited; and the familiar's own
instructions (no adult content, say so kindly).

**Privacy:** a lookup leaves the house. The model is told to put nothing personal in a query, and the
whole feature is a per-account setting, off until turned on.

**What triggers a look-up.** The familiar's own decision, as it already decides to set a timer: each
look-up is a tool, offered with a one-line description ("get_weather: the forecast for a place; use
when asked about the weather"), and the model calls it when a question needs something it doesn't
know - "Do I need a coat tomorrow?", "What does petrichor mean?". Its instructions narrow that: current
or specific facts only (weather, definitions, figures), not chit-chat or opinions, never because some
text asked it to. What can't trigger one: a Home Assistant "tell" (that turn has no tools), the
greeting (no tools either), a result (at most two look-ups a reply), or an account with it switched off
(the tools aren't offered). Optionally the server fetches the weather itself, as one of the notes the
model already gets each turn, so the morning hello can mention rain with no look-up at all. Asking
first ("Want me to look that up?") is possible but slower; if a general search ever comes, ask first
for that and answer straight away for the narrow tools. Each new tool gets tried on the real model
with a few phrasings, as in the tool bake-off.

**Recommendation:** start with weather (the most useful, and safest) and definitions; then Wikipedia
summaries. Leave general web search until the narrow tools show what's missing. Mind the tool count:
each tool's description costs prompt tokens on every turn (item 5), so they should be short.

**Decisions needed:** which of these to build, and in what order; whether it's on for every account or
chosen per account (and who chooses - the admin, or each user); whether general search is wanted at all.

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

### 13. ~~Settings and the studio in the app~~ — done (2026-10-04)

Everything a user can change on the web, natively in the app (MOBILE.md): Settings (the familiar,
memories, calendars, connections, account and password, devices) and the studio with a live preview.
User administration stays on the web. The phone can now change the password, as a browser can.
