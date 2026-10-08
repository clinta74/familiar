# Familiar — Backlog

Work that's agreed but not finished, in the order it'll be done. Bigger designs live in
[PLAN.md](PLAN.md) and [MOBILE.md](MOBILE.md). When an item ships, move it to
[BACKLOG-DONE.md](BACKLOG-DONE.md) under the same number - code comments cite items by number
("BACKLOG item 21") - and say what was built and how it was checked. New items take the next
number: **26**.

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
