# Familiar — Backlog

Work that's agreed but not finished, in the order it'll be done. Bigger designs live in
[PLAN.md](PLAN.md) and [MOBILE.md](MOBILE.md). When an item ships, move it to
[BACKLOG-DONE.md](BACKLOG-DONE.md) under the same number - code comments cite items by number
("BACKLOG item 21") - and say what was built and how it was checked. New items take the next
number: **27**.

---

## To check on the Fold (and the site)

Built and tested on the emulator or the dev box, not yet seen on the real phone or site. Tick each off here
(or open an item if it doesn't work).

- **Texts, calls, directions and "where am I"** (item 21, v0.3.7, with the NAS updated): the first
  of each asks for contacts or location; with Familiar in the background, a notification to tap.
- **Sound effects in the app** (item 14): the avatar page plays them; "Turn sound effects off" in
  the menu.
- **The sleepy avatar** (item 18): it dozes after a few minutes, and wakes on a tap or when the app
  comes back to the front.
- **The avatar's framing** (item 23) at the Fold's sizes: folded, with the keyboard up, and unfolded
  (side by side, unchanged); and the flat characters, which weren't checked.
- **Alarms after a restart** (MOBILE.md, M3): a timer or reminder set, the phone restarted, and it
  still rings.
- **The home-screen widget** (`NextUpWidget`).
- **Contacts** (item 25, with the NAS updated): "take me to [someone]'s" and "what's [someone]'s
  number?" with real contacts.
- **"Where am I" on the website** (item 24), on the real site over HTTPS with "Look things up" on:
  the browser asks once to allow location; then "what's the weather where I am?".

---

## What it can do

### 26. Dates worked out, not guessed

Asked for 2026-10-08: maths and exact statistics calculated or found, not made up. **Measured the
same day on the real model** (look-ups on), eight questions: it already used `calculate` for 17% of
2,340, 4,837 x 296, compound interest, an average and someone's age, and `look_up` for the
population of Leeds and the height of Everest - all right. The one miss was **dates**: "how many days
until Christmas?" - it passed "2026-12-25 - 2026-10-09" to `calculate`, which read the dates as
subtraction (-56), then counted months itself (108) and said "about eighty-seven" (it was 77).

- **Date arithmetic:** a `days` tool (or `calculate` learning dates): days between two dates or until
  one, a date plus or minus some days or weeks, someone's age - in the user's time zone, "today" known.
- **`calculate` refusing dates:** an expression like "2026-12-25 - 2026-10-09" gets "that's dates -
  use the dates tool", not a wrong number.
- Re-run the eight, plus "how many days since 1 January", "what date is 100 days from now", "how many
  weeks until my birthday on 3 March".

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

---

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
