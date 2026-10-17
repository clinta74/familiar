# Familiar — Backlog

Work that's agreed but not finished, in the order it'll be done. Bigger designs live in
[PLAN.md](PLAN.md) and [MOBILE.md](MOBILE.md). When an item ships, move it to
[BACKLOG-DONE.md](BACKLOG-DONE.md) under the same number - code comments cite items by number
("BACKLOG item 21") - and say what was built and how it was checked. New items take the next
number: **36**.

---

## To check on the Fold (and the site)

Built and tested on the emulator or the dev box, not yet seen on the real phone or site. Tick each off here
(or open an item if it doesn't work).

- **Texts and calls** (item 21; directions and "where am I" confirmed 2026-10-09): Messages or the
  dialler opens ready; with Familiar in the background, a notification to tap.
- **Sound effects in a conversation in the app** (item 14; they play in the studio, 2026-10-09): ask
  for one by name ("play a drumroll"), or tell it a joke.
- **The sleepy avatar** (item 18): it dozes after a few minutes, and wakes on a tap or when the app
  comes back to the front.
- **Alarms after a restart** (MOBILE.md, M3): a timer or reminder set, the phone restarted, and it
  still rings.
- **The home-screen widget** (`NextUpWidget`).
- **The 3D avatar appearing faster** (item 28): from opening the app to the character showing.
- **Hands-free hearing you** (item 27): answers that repeat the familiar's words ("yes, set the timer")
  get through; if one still vanishes, the NAS log says why ("Ignored a transcript that was the
  familiar's own voice", or no transcript at all).
- **Home Assistant** (item 29), against the real one: Settings → Connections → Home Assistant, its
  address as the NAS reaches it and a long-lived access token (your Home Assistant profile →
  Security); the devices exposed to Assist should be listed. Then "turn on the kitchen light", a
  dimmer to a percentage, the heating to a temperature, "is the front door locked?", "did I leave
  anything on?" - and unlocking should ask first. Brightness and colour (item 30): "set it to 50
  percent", "make it green" after naming a lamp.
- **Where you live, picked** (item 32): in the app, Settings → Your familiar → Where you live:
  search for the town and pick it, or "Use my location"; then "do I need a coat?" and "where do I
  live?". A home typed in before shows as "typed, not picked" until it's picked.
- **"Where am I" on the website** (item 24), on the real site over HTTPS with "Look things up" on:
  the browser asks once to allow location; then "what's the weather where I am?".

---

## Parked

Ideas that came up and were set aside - not agreed. Each would need asking about first.

- **General web search** (SearXNG on the NAS, safe search strict, titles and snippets only) - only if
  the narrow look-ups leave gaps (item 15).
- **Faster first words** (items 5 and 11): shorter tool descriptions; a model whose prompt cache
  Ollama reuses; starting to speak at the first clause rather than the first whole sentence.
- **Push without ntfy:** the app keeping its own connection in the background (a foreground service
  with its own notification), only if dropping the ntfy app is wanted. Discussed 2026-10-06; ntfy
  kept.
- **Sending texts directly** (`SEND_SMS`), after a confirmation, for hands-free use (item 21).
- **Singing:** Kokoro's speech pitched to a tune, for short set pieces like "happy birthday"
  (item 14's original notes).
- **30 fps when idle**, if battery matters in long hands-free sessions (item 8).
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

## Knowing more, showing more

Asked 2026-10-17, from a chat about the elections. Asked "who are the candidates for me?", it asked
which races; asked to look where he was, the browser refused location and it stopped there; reminded
it knew from the weather, it said "Warren" and guessed Ohio (it's Warren, Michigan), then gave a
congressional district from memory, then offered to "look up that link" and gave none. Four of eight
exchanges went on finding out where he lives, which it could have known from the start; the district
was guessed, not found; and it offered something no tool can do. The order below is the suggested
order. 32 is done (BACKLOG-DONE.md).

### 33. Find local facts, not guess them

The district was made up and the candidates weren't found: `look_up` returns an article's opening
paragraph, and a list of candidates is in a table further down.

- **A rule:** never offer what no tool can do ("would you like me to look up that link?"); and civic
  facts - districts, candidates, polling places, deadlines - only from a look-up, never memory.
- **`look_up` reads a section:** given an article and what to find ("Michigan's 10th district" in "2026
  United States House of Representatives elections in Michigan"), it returns that section's text and
  table rows, trimmed, with the article's last-edited date. Wikipedia only, so the same safety.
- **Where they live, officially** (US): the Census Bureau's geocoder (free, no key) gives the county,
  congressional and state legislative districts for home's coordinates - one more allow-listed host.
  Elsewhere, the region from item 32.
- **Faster:** the look-ups in one round run in parallel rather than one after another, and each tool's
  description says what it can't answer, so the model doesn't spend a round finding out. The time
  lost in that chat was mostly exchanges, not milliseconds; 32 and the rule above save those.
- A general web search stays parked; if this still leaves gaps, it's the next thing to reconsider.

### 34. Links: given, and read

Two things "a web link" can mean, both useful:

- **The familiar hands over a link.** A reply can carry link cards - title and site, tappable,
  shown under the message and never read aloud ("Michigan Secretary of State - Voter Information").
  Only links a tool returned in that reply can be shown: look-up results carry their source page, and
  the model refers to one by number (`[link:1]`, stripped like the emotion tags); it can never write
  an address of its own. A short built-in list of official sites (state election offices, the
  Census) can be a tool result too.
- **The familiar reads a link you give it.** Paste a link into chat and it can read the page: public
  http(s) only (never the LAN or the server itself), a size and time limit, text only (no scripts),
  trimmed, and labelled as text from that site, not instructions - a look-up, so after it anything
  that changes something asks first. Off with "Look things up".

### 35. Pictures in the chat

The chat model already sees images - `ollama show familiar-chat` lists vision (gemma4 with its
52M-parameter image encoder) - so no new model.

- **Web:** paste, drop or attach an image; **app:** attach or share to Familiar from another app.
  Shown in the chat with the message; "what's this plant?", "read this letter", "what does this
  error say?".
- **Server:** stored with the message (Postgres, size-capped, shrunk to ~1024 px, location and camera
  data stripped), sent to the model with that turn only; later turns see "(an image they sent: …)"
  in its place, so a picture doesn't cost every turn after it. Measure what an image adds before the
  first word (it isn't cached).
- **Safety:** text in a picture is someone else's words, like a look-up - a photo of a note saying
  "ignore your instructions" mustn't act - so a turn with an image asks first before changing
  anything. The memory pass reads the words, not the pictures.
- Not voice: hands-free can't send one, and "describe what you see" through the camera is a later
  idea.

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
