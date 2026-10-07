# Familiar — Mobile plan (Android + iOS)

Native apps, because the point of being on the phone is the phone: alarms that ring offline, the
calendar the user already has, a hands-free session that survives the screen turning off, being
the thing that answers when you long-press home. The server side of all this is in
[PLAN.md](PLAN.md) — chiefly device-side tools (§6.1) and push (§6.2).

Android first. iOS follows, built and shipped from CI because there is no Mac (§5).

---

## 1. Stack

| Layer | Choice | Why |
|---|---|---|
| Shared code | **Kotlin Multiplatform** (`mobile/shared`) | Protocol, networking, auth, conversation state written once for both platforms. |
| Networking | **Ktor client** (SSE + REST), `kotlinx.serialization` | Multiplatform; bearer token on every request, the event stream included. |
| UI | **Compose Multiplatform** | One UI, previewed and debugged on Android, runs on iOS. This matters most *because* there's no Mac — see §5. |
| Platform services | Kotlin on Android, **Swift** on iOS, behind `expect`/`actual` interfaces | Audio, alarms, calendar, notifications, assistant entry points are where "native" earns its keep. |
| Avatar | **WebView/WKWebView hosting `client/packages/avatar`** | One renderer across web, Android, iOS. Native VRM rendering doesn't exist in usable form on either platform; Unity-as-a-library is hundreds of MB to draw a face. |
| Local storage | SQLDelight | Offline transcript cache, pending timers, settings. |
| Secrets | Android Keystore / iOS Keychain | Access token never in plain prefs. |

Considered and rejected: **Flutter** (same Mac requirement for iOS, weaker platform-service
story, no shared code with anything we have); **.NET MAUI** (would share C# DTOs with the
server, but lags on new Android/iOS APIs and still needs a Mac to build iOS); **separate
Kotlin and Swift apps** (best iOS fidelity, double the work, and the Swift half can't be
developed without a Mac).

### 1.1 Avatar in a WebView, audio native

Audio is played **natively** (audio focus, Bluetooth, ducking, background playback, the system
volume stream) and the WebView only draws. The bridge carries:

- `setState`, `setExpression`, `playGesture` — straight from the SSE events.
- Viseme track + the native player's start time, re-based onto the WebView's clock once per
  utterance, so lip-sync is scheduled locally rather than streamed at 60 Hz across the bridge.
- Amplitude fallback: native player's RMS level at ~30 Hz → `setMouthOpen`.

The avatar package is loaded from the app bundle, not the server, so it renders offline and
can't be swapped by a compromised server. The WebView gets no network access and no bridge
beyond the driver calls.

*Built (M1, 2026-10-03):* `client/avatar.html` + `src/embed/avatarEmbed.ts` is the page: the web
client's avatar code (and `src/avatar/avatar.css`, now shared) with `window.familiarAvatar` —
`look`, `state`, `expression`, `gesture`, `text`, `clip(words, elapsedMs)`, `level`, `silence`.
`npm run build:embed` writes `client/dist-embed/`; the Android build runs it and bundles the
result as `assets/avatar/`. The WebView serves it at `https://appassets.androidplatform.net/avatar/`
from assets and answers every request itself: the page's own files, an uploaded VRM model (fetched
natively with the device token, the only server request the page can cause), and 403 for anything
else. No JavaScript interface: the app calls in with `evaluateJavascript`, and replays the look and
state when the page (re)loads. Debug builds forward the page's console to logcat (`FamiliarAvatar`)
and allow Chrome DevTools.

---

## 2. Android integrations

| Feature | API | Notes |
|---|---|---|
| Hands-free conversation | **Foreground service**, type `microphone` | Required to keep the mic open with the screen off; must be started while the app is visible (Android 14+). Persistent notification with stop. |
| Be the assistant | **`RoleManager.ROLE_ASSISTANT`** + `VoiceInteractionService` | User can set Familiar as default digital assistant: long-press home / power, assist gesture. |
| Timers & alarms | `AlarmManager.setAlarmClock` / exact alarms | Rings offline. Exact alarms need `SCHEDULE_EXACT_ALARM` (user-granted on 14+) — or fall back to handing off via `AlarmClock.ACTION_SET_TIMER` to the system clock app. |
| Calendar | `CalendarContract` | Reads/writes every synced account (Google, Exchange, DAVx⁵ CalDAV). Uploads a rolling window for situation awareness (PLAN.md §6.4). |
| Notifications | FCM (or UnifiedPush/ntfy) + notification channels | Content-free wake → fetch (PLAN.md §6.2). Full-screen intent for an alarm ringing. |
| Situation | Location (coarse, opt-in), DND state, battery, connectivity, headset connected, activity recognition | Each is a stream capability (PLAN.md §3.1) and a separate permission; none required. |
| Quick access | App shortcuts, Quick Settings tile, home-screen widget (next reminder, talk button) | |
| Wake word *(later)* | On-device (e.g. openWakeWord) inside the foreground service | A v1 non-goal; native makes it possible. Battery cost to measure. |
| Wear OS *(later)* | Timers/reminders on the watch | |

Speech-to-text stays on the server (one model, consistent quality). On-device VAD (Silero ONNX)
decides when to stream, so the mic isn't shipping silence over LTE.

---

## 3. iOS integrations

| Feature | API | Notes |
|---|---|---|
| Conversation audio | `AVAudioSession` (`playAndRecord`, voice processing for echo cancellation) | iOS won't keep a hands-free mic session alive in the background the way an Android foreground service does. Plan for foreground-only hands-free on iOS. |
| Timers & alarms | **AlarmKit** (iOS 26+) | Real alarms that ring on the lock screen and in silent mode. Fallback for older iOS: time-sensitive local notifications. |
| Timer display | Live Activities / Dynamic Island | Countdown on the lock screen. |
| Calendar | **EventKit** | Same role as `CalendarContract`. |
| Notifications | APNs | Content-free wake → fetch. |
| Siri / Shortcuts | **App Intents** | "Ask Familiar…", "Start a Familiar timer"; also exposes actions to Shortcuts automations. iOS doesn't allow replacing Siri as the system assistant. |
| Quick access | Widgets, Control Center control (iOS 18+), Action button | |

---

## 4. Shared module (`mobile/shared`)

```
mobile/
  shared/                 KMP: commonMain / androidMain / iosMain
    protocol/             SSE events + request bodies, mirroring the server's (generated from a schema, §6)
    api/                  Ktor REST + SSE client, auth, reconnect with Last-Event-ID
    conversation/         state machine: idle ↔ listening ↔ thinking ↔ speaking, barge-in
    tools/                device-side tool registry; dispatches tool.invoke to platform actuals
    platform/             expect: AudioIO, Alarms, Calendar, Notifications, Location, SecureStore
    ui/                   Compose Multiplatform screens
  androidApp/             Activity, services, VoiceInteractionService, receivers, widgets
  iosApp/                 Xcode project: SwiftUI host, App Intents, widgets, AlarmKit, Live Activities
```

Device-side tools are declared once in `tools/`, implemented per platform, and advertised in
the stream request's capabilities only where the platform implementation exists and the permission is granted.

---

## 5. iOS without a Mac

Building, signing, and submitting an iOS app requires Xcode, which runs only on macOS. There's no
way around that, only ways to put the Mac somewhere else.

**What works from Windows:**
- All shared Kotlin code, and all Compose UI — written, previewed, and debugged on Android.
  That's the majority of the app, which is why Compose Multiplatform was chosen.
- `iosMain` Kotlin and the Swift files can be edited anywhere; they just can't be compiled here.

**What needs macOS, and where to get it:**

| Option | Good for | Trade-off |
|---|---|---|
| **GitHub Actions macOS runners** | Build, unit tests, simulator tests, **TestFlight upload via fastlane** on every push | Private-repo macOS minutes count at 10× — fine for per-merge builds, wasteful per-commit. No interactive debugging. |
| **Cloud Mac rental** (by the hour/day) | Occasional interactive sessions: Xcode debugger, simulator, provisioning setup | Pay per use; remote desktop latency. AWS EC2 Mac has a 24-hour minimum. |
| **Used Mac mini (M1 or later)** | Everything, locally | Up-front cost. Realistically the right answer once iOS is actively developed, not just built. |

**Plan:** CI-only iOS until the Android app is solid. Every merge to `main` builds the iOS app and
pushes to **TestFlight**; you test on a real iPhone from there. Rent a cloud Mac for the one-off
setup (certificates, provisioning, first AlarmKit/App Intents wiring) and for debugging anything
the iOS-specific Swift layer gets wrong. Revisit buying a Mac mini when iOS-specific work becomes
weekly.

Also needed regardless: **Apple Developer Program** membership (annual fee) for TestFlight and
real-device installs, and an iPhone to test on. Google Play needs a one-time developer
registration; sideloading APKs to your own devices needs nothing.

---

## 6. Keeping the protocol in sync

Three clients (TS web, Kotlin mobile, C# server) speak one event protocol. Hand-written copies will
drift. The server's message records are the source of truth; a build step emits a **JSON Schema**
from them, and the TS and Kotlin types are generated from it. CI fails if generated files are
stale. REST uses the server's OpenAPI document the same way.

*Built (2026-10-03):* `ProtocolCatalog` (src/Familiar.Server/Protocol/) lists every stream event
with its payload record and the request and response bodies both clients use; `ProtocolSchema`
writes them as **`protocol/familiar.schema.json`** - names, types and nullability as the server's
JSON options write them, descriptions from its XML docs, each event with its `EventTypes` name.
`client/scripts/protocol.mjs` turns that into `client/src/protocol/generated.ts` (string-union
enums, interfaces, `EventPayloads`, `EventTypes`) and the phone's `protocol/Generated.kt`
(`@Serializable` data classes; enums as strings with constants, so an app a version behind still
reads a value it doesn't know). Checked in CI both ways: the server's tests fail if the schema is
older than the records, and `npm run protocol:check` if the generated types are older than the
schema. To change the protocol: change the record, run
`FAMILIAR_WRITE_PROTOCOL=1 dotnet test --filter ProtocolSchemaTests`, then `npm run protocol` in
client/. Generating them caught drift that had already happened: the web client was missing
`audio.end`, `connectionId` and a job's `ring`; the app had invented defaults for fields the server
always sends. The whole REST API followed: accounts and devices, user administration, the
familiar and its appearance, avatars, calendars, memories, timers, push, hook tokens, webhooks and
MCP servers - the web client's pages import them (request bodies checked with `satisfies`).
Record parameters with defaults are optional, with the default in the schema (TypeScript `?`,
Kotlin the same default), and a string with fixed values - the model a familiar is drawn as, its
accessory and style, a notification's kind - declares them (`[AllowedValues]`), so TypeScript gets
the union (a test keeps them equal to what the server checks). Making it honest showed two things
the client assumed: `appearance` and `proactive` may be absent (they're optional when saving).

---

## 7. Phases

Mobile work starts once the server has text + voice (PLAN.md phases 1–2), since the app is
mostly a client for those.

**M0 (2026-10-02): Android half built.** `mobile/`: Kotlin 2.4, AGP 9.4 (its KMP library plugin),
Compose Multiplatform 1.12, Ktor 3.6, Gradle 9.8; min API 29, compiled against 37 (Compose 1.12
requires it). The shared module has the protocol (hand-written for now), a Ktor client (device
sign-in, REST, and the SSE stream with `Authorization: Bearer` and Last-Event-ID reconnects), the
conversation reducer ported from the web client, and the Compose UI: sign-in (server address,
username, password) and a typed chat with tool chips. The token and server address are encrypted
with a Keystore AES key. Network security allows plain http (home/VPN) and user-installed CAs (the
NAS's self-signed certificate). Verified on an emulator against the dev server: sign-in, live
greeting, a timer set by tool and announced two minutes later, the session surviving a restart
(stored values encrypted), sign-out revoking the token. CI runs the shared tests and keeps a debug
APK. **Not yet:** the iOS CI build and TestFlight (needs the Apple Developer account). The
schema-generated protocol (§6) followed on 2026-10-03.

**M1 started (2026-10-03): the avatar.** The chat screen shows the familiar above the conversation
on a phone and beside it on a wide screen (≥ 600 dp: an unfolded Fold, a tablet), in the look from
the profile (re-read on every reconnect, so a change in the web studio shows up). State, expressions,
gestures and the text-driven mouth come from the stream, as on the web; typing reads as listening.
Verified on an emulator: a hedgehog with a flower from the profile, thinking bubbles while the model
works, joy and a wave on "wave hello", the side-by-side layout at Fold size.

**M1: replies spoken natively (2026-10-03).** `SpeechPlayer` (shared) is the web AudioPlayer ported:
clips fetched and decoded as soon as they're announced, played in `seq` order, a new reply cutting
off the last, and `heard()` for Stop. On Android, `MediaCodec` decodes the MP3 to PCM and one
`AudioTrack` (usage *assistant*, transient focus that ducks music) plays a reply's sentences back
to back; loudness is the RMS of the samples at the playback head, so lip-sync needs no microphone
permission. Each clip's word timings go to the page once (`clip`) and loudness ~30 times a second
(`level`). The voice is on by default, with a menu switch, and the hello is spoken too. Verified on
an emulator: a two-sentence reply gave the page two timed clips and 161 level updates, and Stop
mid-sentence stored the reply cut to the words played.

**M1: push-to-talk (2026-10-03).** A mic button beside the message box: hold to talk, let go to
send. The first press asks for RECORD_AUDIO instead of recording; pressing while the familiar is
speaking stops it (keeping what was heard), as on the web. `AndroidMicrophone` records 16 kHz mono
16-bit from the voice-recognition source (at most 60 s) and reports loudness, which lights the
button; the recording goes to `POST /api/conversation/audio` as a WAV (`wav()`, shared) and the
reply comes back on the stream, spoken. A tap shorter than a third of a second says to hold it;
sliding off the button cancels. The avatar listens while it's held. Verified on an emulator against
the dev server with a throwaway account: the permission prompt on the first press, the hint on a
tap, "listening" while held, and on release the recording transcribed and answered aloud.
**M1 is done** apart from trying it on a real phone (backlog item 8).

**M2: hands-free (2026-10-03).** A "Hands-free" switch under the message box: the microphone stays
open and each utterance is sent when the user stops talking, with the screen off too.
- *The session outlives the screen.* `FamiliarSession` (shared) now owns the stream, the voice, the
  avatar's state and the microphone; `FamiliarSessions` keeps it for as long as the device is signed
  in, so rotating, unfolding or the activity being recreated doesn't drop the conversation, and the
  chat screen only shows it. The platform services are made once per process (`AppServices`), and
  permission prompts go to whichever activity is showing.
- *Speech detection* is the web client's: `SpeechDetector` (shared, tested) is vad-web's frame
  processor ported - the same thresholds, 700 ms of quiet to end, 300 ms minimum, 300 ms kept
  before the first word, firmer while the familiar talks, and a 30 s cap - and the model is the same
  Silero v5 file, bundled as `assets/vad/` and run with ONNX Runtime on a 512-sample frame plus the
  last 64 of the previous one. Checked against the model offline: a spoken sentence with a pause in
  it is one utterance; silence and hiss are nothing.
- *Android:* `AndroidHandsFree` reads the microphone from the voice-communication source (echo
  cancellation takes the familiar's own voice out; the server's echo guard catches what leaks), and
  `HandsFreeService` is a foreground service of type microphone with a "Pip is listening"
  notification and Stop, a partial wake lock while listening, and a media session so a headset's
  button stops the familiar mid-sentence. Talking over it does the same (barge-in), keeping what was
  heard. RECORD_AUDIO and (13+) POST_NOTIFICATIONS are asked the first time.
- Verified on an emulator: the prompts, the service in the foreground with its notification, the
  microphone still recording (and not silenced) after 20 s with the screen asleep, the headset button
  stopping a reply, Stop in the notification switching it off in the app. Not yet: real speech -
  the emulator's microphone hears nothing - so barge-in by voice, and battery over a long session,
  wait for the phone (backlog item 8). ONNX Runtime adds ~40 MB to the debug APK (arm64 + x86_64).
- Fixed on the way, web and phone: stopping a reply that was still being thought about sent "what was
  heard" of the previous, finished one, and the server cut that to its spoken words ("hf_tester"
  became "hftester"). Players now report nothing heard once a reply has played out, and the server
  counts the heard words and keeps the stored reply's own spelling, cutting nothing if all was heard.

**M3: alarms on the phone, and push through ntfy (2026-10-03).** "Wake me at 7" rings in airplane
mode.
- *Alarms.* Every pending timer and reminder is also one of the phone's own alarms
  (`PhoneAlarms`, `AlarmManager.setAlarmClock` - exact, via USE_EXACT_ALARM, which Android 13+
  grants on install), kept in step with the server's list: the stream's `jobs` event, the list on
  opening, and a push when it changes while the app is closed. They're put back after a restart, and
  any missed while the phone was off go off at once, marked so. Timers and reminders set as alarms
  ring - the alarm sound on the alarm volume, again and again, full screen over the lock screen -
  until dismissed; other reminders are an ordinary notification. The server's `create_reminder`
  has `alarm: true` for that ("wake me", "don't let me miss"), and `JobView.ring` says which ring.
  Notification permission is asked once after signing in (Android 13+ shows nothing without it).
- *Push (decided: ntfy).* The phone registers with its UnifiedPush distributor - the ntfy Android
  app, pointed at the household's ntfy - using the server's VAPID key, so only this server's pushes
  get through, and gives the server its endpoint, tied to the device (signing the device out removes
  it). Pushes stay content-free: on one, the app fetches the pending list (and resets its alarms)
  and what went off recently, and shows what it hasn't already. The server pushes phones whenever
  the timers change, as well as when one goes off. `Push:UnifiedPushServer` names the ntfy; it's the
  one local-network address the server will push to. The NAS compose has an `ntfy` service.
- Verified: on an emulator, a timer asked for in the chat became an exact alarm clock and, in
  airplane mode with the app in the background, rang ("Pip: time's up - Your tea timer is done.")
  until Dismiss; and against a real ntfy, setting and cancelling a timer each put a content-free
  message on the phone's topic, the server's VAPID header accepted. Since then, on the Fold
  (2026-10-06): the ntfy app as the distributor, an announcement pushed with Familiar closed, and a
  timer ringing. Not yet: a restart restoring alarms (BACKLOG.md, "To check on the Fold"). The real
  model set "wake me up in 2 minutes" as a 2-minute timer, which rings anyway.

**M4: the phone's assistant (2026-10-03).** Long-press home opens Familiar listening.
- *The assistant role:* a voice interaction service (with the session and recognition services
  Android requires; recognition answers "not here" - speech is the server's), so Familiar can be
  chosen in Settings > Default apps > Digital assistant app; the chat menu's "Make Familiar your
  assistant" opens that page until it is. The assist gesture opens the app in the assistant's task.
- *Listening once:* opened to talk - the assistant, the Quick Settings tile ("Familiar"), the app
  shortcut ("Talk"), the widget's button - the familiar listens for one thing said, through
  hands-free, then stops; or stops after 12 s of nothing. Hands-free that's already on stays on.
- *Widget:* the next timer or reminder (from the phone's alarms, redrawn when they change) and Talk.
- Verified on an emulator: with Familiar as the assistant, long-pressing home opened it listening
  and it stopped after 12 s of quiet; the tile and the shortcut's intent the same; the menu item
  opening the system's assistant page. Not yet: the widget on a home screen (needs the launcher),
  and all of it on the phone.

**Settings and the studio (2026-10-04).** Everything a user can change on the web, natively: the
chat menu's Settings has the familiar (name, personality, voice with a preview, saying hello first),
what it remembers, calendars, connections (hook tokens, webhooks, MCP servers), the account and its
password, and devices; and Studio changes the look with the avatar previewing it live (turned round
in 3D, trying expressions and gestures), VRM uploads through the system's file chooser included.
Saving updates the chat's name and avatar at once. User administration stays on the web.
- *One list of characters.* The studio's characters, presets, colour names and accessories are
  `client/src/avatar/characters.json`, which the web client imports and `npm run protocol` turns into
  `Characters.kt` (checked in CI with the protocol types), so the two studios can't drift.
- *The password from the phone.* It used to be browser-only; the current password is required either
  way. The phone that changes it stays signed in (its token takes the new security stamp), every other
  browser and phone is signed out.
- Verified on an emulator against the dev server with a throwaway account: renaming the familiar (the
  chat's title followed), the voice preview, the fox in 3D turned round and saved (the server stored
  it), adding a memory, a hook token created and revoked, the password changed from the phone (the
  browser session signed out, the phone still in), the devices list.

**Device calendar (2026-10-03), the first of M3.** "Use this phone's calendar" in the chat menu asks
for READ/WRITE_CALENDAR. Once allowed, the app reads `CalendarContract.Instances` (visible
calendars; declined and cancelled left out) from 12 hours ago to 14 days ahead and uploads it on
start, every 15 minutes while open, and after adding an event. It reconnects the stream with
`capabilities=calendar.write`, and answers `calendar.add` invokes addressed to its connection
(expired or repeated ones ignored) by inserting into the primary writable calendar (Google's,
when there is one). The server falls back to its own calendar when the phone doesn't answer.
Turning it off is the system's app-permission screen.

| Phase | Delivers | Done when |
|---|---|---|
| **M0. Scaffold** | KMP project, Android app, iOS CI build → TestFlight, server URL + login, schema-generated protocol | Log in on an Android phone; an empty iOS build arrives in TestFlight |
| **M1. Talk** | Avatar WebView + bridge, native audio, push-to-talk, streamed TTS, lip-sync | Same conversation as the web client, on Android |
| **M2. Hands-free** | Foreground service, on-device VAD, barge-in, headset support | Talk to it with the phone in a pocket |
| **M3. Device tools** | Alarms/timers on device, calendar read/write + sync window, notifications (FCM), stream capabilities | "Wake me at 7" rings in airplane mode |
| **M4. Be the assistant** | `ROLE_ASSISTANT`, shortcuts, QS tile, widget | Long-press home opens Familiar listening |
| **M5. iOS parity** | AlarmKit, EventKit, APNs, App Intents, Live Activities, widgets | The M1–M3 "done when" lines hold on an iPhone |
| **M6. Extras** | Wake word, Wear OS, situation signals (location, DND, activity) | — |

---

## 8. Open decisions

1. ~~Play Store or sideload~~ — **decided: sideload** for now. Signed release APKs from CI (a
   version tag publishes one as a GitHub release: [RELEASING.md](RELEASING.md)), installed by hand. Play's policy review of exact-alarm and assistant-role permissions is
   deferred until there's a reason to publish.
2. ~~FCM or UnifiedPush/ntfy~~ — **decided (2026-10-03): ntfy on the NAS** (UnifiedPush): no
   Google account or Firebase project, works on de-Googled phones; one more container.
3. **Minimum OS versions** — proposed Android 10 (API 29), iOS 17 (AlarmKit features gated to 26+).
