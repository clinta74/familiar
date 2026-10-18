# Familiar's models

Familiar runs its own Ollama container (`familiar-ollama`), separate from muwbta's, so each can be
tuned for its workload (PLAN.md §4.2). This directory holds the derived model and the Phase 0
bake-off that chooses its base.

| File | |
|---|---|
| `Modelfile.familiar` | The derived model `familiar-chat`: base + `num_ctx` baked in |
| `create-models.sh` | Pulls the base and embedding model, builds `familiar-chat`, verifies the window and tool capability |
| `bakeoff.cs` | Scores candidate bases on what Familiar needs |

## Why a derived model

Ollama's default context is 4096 tokens, and an over-long prompt is **truncated, not refused**:
the symptom is a familiar that forgets its persona or its tools, not an error. Baking `num_ctx`
into the model means a caller that forgets to send it still gets the right window. (muwbta learned
this the hard way; see its `tools/ollama/README.md`.)

## The bake-off

Requirements, in order: **native tool calling** (a hard gate; checked via `ollama show`
capabilities), **restraint** (chit-chat must not set timers), **emotion-tag compliance** (the
avatar's expressions come from it, PLAN.md §4.4), then **speed** — a voice reply has to start in
well under two seconds.

```bash
docker compose -f docker-compose.yml -f docker-compose.gpu.yml --profile llm up -d

docker exec familiar-ollama ollama pull qwen3:8b
docker exec familiar-ollama ollama pull llama3.1:8b
docker exec familiar-ollama ollama pull mistral-nemo:12b

dotnet run tools/ollama/bakeoff.cs -- --models qwen3:8b,llama3.1:8b,mistral-nemo:12b --runs 2
```

It prints a per-case log and a summary table:

| model | tools | restraint | emotion tag | prompt tok/s | gen tok/s | median ms to first token |
|---|---|---|---|---|---|---|

The candidates above are a starting list, not a recommendation; add whatever current 8–14B
tool-capable models look promising. Models that report a `thinking` capability are run with
thinking **off** — reasoning before every spoken reply costs seconds Familiar doesn't have.

Run it on **both** machines. The dev box's 5070 Ti numbers decide quality; the NAS 3060's decide
whether it's usable there, alongside whatever of muwbta's model is resident (PLAN.md §10.3).

Record the results here with the date and Ollama version, then put the winner in
`Modelfile.familiar` and run `create-models.sh`.

### Results

**2026-10-18, dev box, Ollama 0.40.6** (BACKLOG item 37): `familiar-chat` again, after the upgrade -
tools 12/12, restraint 10/10, emotion tag 10/10, prompt 1,667 tok/s, **generation 114.8 tok/s**,
median first token **278 ms**. 0.40 runs gemma4 in llama.cpp's server with its draft model, and reuses
the prompt cache between turns (0.35.0 re-read the whole prompt every time). It needs
`LLAMA_ARG_THREADS` set (it ignores `OLLAMA_NUM_THREAD`) and `LLAMA_ARG_CTX_CHECKPOINTS` kept low:
see the compose files. Rebuild `familiar-chat` after upgrading (`create-models.sh`).

**2026-10-01, dev box (RTX 5070 Ti 16 GB, WSL2), Ollama 0.35.0, num_ctx 8192, 2 runs × 11 cases.**
Winner: **`gemma4:12b`**, now the base of `familiar-chat`.

| model | tools | restraint | emotion tag | prompt tok/s | gen tok/s | median ms to first token |
|---|---|---|---|---|---|---|
| qwen3:8b | 0/12 | 10/10 | 10/10 | 6946 | 32.9 | 87 |
| llama3.1:8b | 12/12 | 9/10 | 10/10 | 11523 | 47.9 | 71 |
| qwen3.5:9b | 4/12 | 10/10 | 10/10 | 2011 | 32.9 | 389 |
| **gemma4:12b** | **12/12** | **10/10** | **10/10** | 1052 | 37.4 | 395 |

Whole replies took 0.6–2.2 s with gemma4 (non-streaming). Streaming makes the first sentence
reach TTS well before that, and the 395 ms to first token fits the PLAN.md §3.2 budget.

What the table doesn't show, from reading the replies:

- **The Qwen models pretend to act.** With an emotion-tag instruction in the system prompt they
  answer tool requests in speech — "Got it! I'll set a timer for ten minutes" (qwen3:8b), "I've
  started a ten-minute timer for your pasta!" (qwen3.5:9b) — and call nothing. Scoping the tag to
  spoken replies didn't fix it. Without the tag instruction qwen3:8b calls tools correctly, so it
  is the *combination* that breaks them, and Familiar needs both. Claiming an action that didn't
  happen is the worst failure an assistant can have; disqualifying.
- **llama3.1:8b calls tools perfectly and converses badly.** "I'm not aware of any specific tool
  that can provide information about octopuses", a reply that was only `[neutral:0.8]`, and raw
  `{"name": "get_calendar", ...}` JSON written into speech (the restraint miss; the avatar would
  read it aloud).
- **gemma4:12b's emotions fit the moment**: `sad` for the sick dog, `thinking` for the camera
  question, `joy` for the finished painting. qwen3.5 chose `angry` and `relaxed` for the sick dog.
- **gemma4 and qwen3.5 are thinking models**; the bake-off sends `think: false`, and Familiar's
  calls must too.

**VRAM:** `familiar-chat` at 8k context uses **~8.9 GB** (nvidia-smi: 10,422 MiB loaded vs
1,535 MiB idle). `ollama ps` reports 1.1 GB for it on 0.35.0, which is wrong — measure with
`nvidia-smi` on the host. On the NAS 3060 (12 GB) it fits alone or with speech (~10.4 GB), not
alongside muwbta's 8.4 GB builder model (PLAN.md §10.3).

Not yet run on the NAS.

## Building the model

```bash
sh tools/ollama/create-models.sh                      # base from Modelfile.familiar
BASE=llama3.1:8b sh tools/ollama/create-models.sh     # try another base under the same name
DOCKER_HOST=ssh://nas sh tools/ollama/create-models.sh    # on the NAS, from this machine
```

The Modelfile is piped into the container, so nothing needs to be mounted or checked out on the NAS.
