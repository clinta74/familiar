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

*Not run yet.*

## Building the model

```bash
sh tools/ollama/create-models.sh                      # base from Modelfile.familiar
BASE=llama3.1:8b sh tools/ollama/create-models.sh     # try another base under the same name
DOCKER_HOST=ssh://nas sh tools/ollama/create-models.sh    # on the NAS, from this machine
```

The Modelfile is piped into the container, so nothing needs to be mounted or checked out on the NAS.
