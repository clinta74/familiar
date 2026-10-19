# Familiar — Backlog

Work that's agreed but not finished, in the order it'll be done. Bigger designs live in
[PLAN.md](PLAN.md) and [MOBILE.md](MOBILE.md). When an item ships, move it to
[BACKLOG-DONE.md](BACKLOG-DONE.md) under the same number - code comments cite items by number
("BACKLOG item 21") - and say what was built and how it was checked. New items take the next
number: **41**.

---

## To check on the Fold (and the site)

Built and tested on the emulator or the dev box, not yet seen on the real phone or site. Tick each off here
(or open an item if it doesn't work).

**The newest (items 29-35)**

- **Home Assistant** (item 29), against the real one: Settings → Connections → Home Assistant, its
  address as the NAS reaches it and a long-lived access token (your Home Assistant profile →
  Security); the devices exposed to Assist should be listed. Then "turn on the kitchen light", a
  dimmer to a percentage, the heating to a temperature, "is the front door locked?", "did I leave
  anything on?" - and unlocking should ask first. Brightness and colour (item 30): "set it to 50
  percent", "make it green" after naming a lamp.
- **Where you live, picked** (item 32): in the app, Settings → Your familiar → Where you live:
  search for the town and pick it, or "Use my location"; then "do I need a coat?" and "where do I
  live?". A home typed in before shows as "typed, not picked" until it's picked.
- **Link cards in the app** (item 34): "give me a link to the Michigan Secretary of State" shows a
  card under the reply; tapping it opens the browser.
- **Push without ntfy** (item 38): after updating the NAS and the app, a quiet "Pip is standing by"
  notification; with Familiar swiped away, an announcement from Home Assistant comes up, and a timer
  set on the website rings on the phone. Then after a restart, without opening the app. Then remove
  ntfy (deploy/TRUENAS.md, "Push to phones").
- **Texts without Messages** (item 39): Settings, Devices, "Send texts without Messages" (it asks to
  send SMS); then, hands-free, "text Liz I'm running late" - the familiar reads it back, "yes" sends
  it, and it's in Messages. "No" or "yes, but say ten minutes" sends nothing.
- **Pictures in the app** (item 35): the picture button by the message box opens the photo picker;
  "what's this?" with a photo; and from Photos or another app, Share → Familiar puts the picture in
  the chat ready to send. Sent pictures show in the bubbles, there and after reopening.

**The phone**

- **Texts and calls** (item 21; directions and "where am I" confirmed 2026-10-09): Messages or the
  dialler opens ready; with Familiar in the background, a notification to tap.
- **Alarms after a restart** (MOBILE.md, M3): a timer or reminder set, the phone restarted, and it
  still rings.
- **The home-screen widget** (`NextUpWidget`).

**Talking and the avatar**

- **Hands-free hearing you** (item 27): answers that repeat the familiar's words ("yes, set the timer")
  get through; if one still vanishes, the NAS log says why ("Ignored a transcript that was the
  familiar's own voice", or no transcript at all).
- **Sound effects in a conversation in the app** (item 14; they play in the studio, 2026-10-09): ask
  for one by name ("play a drumroll"), or tell it a joke.
- **The sleepy avatar** (item 18): it dozes after a few minutes, and wakes on a tap or when the app
  comes back to the front.
- **The 3D avatar appearing faster** (item 28): from opening the app to the character showing.

**The website**

- **"Where am I" on the website** (item 24), on the real site over HTTPS with "Look things up" on:
  "what's the weather where I am?". Location itself works there since 2026-10-17 (Settings' "Use my
  location" confirmed): until then the site's nginx sent `Permissions-Policy: geolocation=()`, which
  blocked it whatever the browser allowed.

---

## Parked

Ideas that came up and were set aside - not agreed. Each would need asking about first.

- **General web search** (SearXNG on the NAS, safe search strict, titles and snippets only) - only if
  the narrow look-ups leave gaps (item 15).
- **Singing:** Kokoro's speech pitched to a tune, for short set pieces like "happy birthday"
  (item 14's original notes).
- **A "System One" decision model** (typed choice / yes-no answers with calibrated probabilities, no
  text): TypeSafe's Jev, and the open ones listed at github.com/yanng981/awesome-system-one. Looked
  at 2026-10-16 and set aside: Jev is cloud-only, against keeping everything on our hardware; none of
  them fill in free-form arguments (the `home` command, a timer's length, a text), so gemma still has
  to; choosing among ~23 tools is where the small open ones are weakest (Laya 0.425 on a 77-label set,
  against Jev's 0.870); and the server's risk classes, confirmations, allow-lists, dates and sums are
  rules in code on purpose, not a model's judgement. The one narrow fit: a small CPU encoder (Laya,
  Von, OpenDecider nano; ONNX, callable from .NET, under 100 ms) answering yes/no checks on a reply -
  "does this claim an action that wasn't taken?" (now the regex in `TurnRunner.ClaimsToAct`, item 30)
  or "worth remembering?". Only if the regex starts missing claims in real use; first score a few
  hundred replies offline against it.

---

## Next, in this order (asked 2026-10-18)

37, 38 and 39 are done (BACKLOG-DONE.md); 40 is for later (asked 2026-10-18).

### 40. Lower frame rate when idle

The avatar draws at full rate all the time. When nothing's moving - no speech, no gesture, only
breathing and blinks - 30 fps (or less) would do, and save battery in long hands-free sessions with
the screen on (item 8 measured the 3D cost on the Fold).

## Voice and sound

### 14. Vocal sounds in the familiar's own voice — left till last (decided 2026-10-06)

The eleven non-vocal effects are done (BACKLOG-DONE.md, item 14). A giggle, a gasp, a sigh or a yawn
has to sound like the familiar's chosen voice, and neither Kokoro (it reads "Hehehe!" out as a word)
nor synthesised blips (listened to 2026-10-05: far off) do.

Ways to make them in the voice - each made once per voice, offline on the dev box's GPU, cached as
small clips the server hands out like speech clips, and listened to before anything ships:

- **A speech model that can laugh, cloning the Kokoro voice** (try first): Dia (Nari Labs, Apache 2.0)
  performs "(laughs)", "(gasps)", "(sighs)" and copies a voice from a short audio prompt; CosyVoice 2
  does "[laughter]" and "[breath]" with zero-shot cloning. Prompted with a sentence Kokoro says in the
  chosen voice, it should giggle in something close to it.
- **Voice conversion of real recordings:** public-domain recordings of a giggle, a gasp, a sigh,
  converted into the Kokoro voice (Seed-VC, OpenVoice's tone converter), with a Kokoro sample as the
  target.
- **Recordings, pitched per voice:** a female and a male set, shifted towards each voice's pitch -
  closer than synthesis, still not the voice.

**Next:** a trial of Dia in two or three voices, sent as clips to listen to. It needs downloads on
the dev box, each to be agreed first: the `pytorch/pytorch:2.7.1-cuda12.8-cudnn9-runtime` image from
Docker Hub (~3.5 GB), the `nari-labs/Dia-1.6B-0626` model from Hugging Face (~6.5 GB), and its
Python packages (~1 GB).
