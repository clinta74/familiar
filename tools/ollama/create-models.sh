#!/bin/sh
# Builds Familiar's derived models inside the ollama container, and proves the context window took.
#
#   sh tools/ollama/create-models.sh                      # base from Modelfile.familiar
#   BASE=llama3.1:8b sh tools/ollama/create-models.sh     # same derived model over another base
#
# Works through `docker exec` only, and the Modelfile is piped in on stdin, so the same command
# runs on the dev box and on the NAS with no repository checkout mounted into the container.
#
# Idempotent: `ollama create` overwrites a model of the same name. Creating evicts whatever is
# loaded, so the next request pays a cold load.

set -e

# Git Bash on Windows rewrites POSIX-looking arguments to native programs, so the container path
# /tmp/Modelfile.familiar would reach `docker exec` as C:/Users/.../Temp/... and ollama would find
# nothing there. No effect anywhere else.
export MSYS_NO_PATHCONV=1

CONTAINER=${CONTAINER:-familiar-ollama}
MODEL=${MODEL:-familiar-chat}
EMBED=${EMBED:-nomic-embed-text}
HERE=$(dirname "$0")
MODELFILE="$HERE/Modelfile.familiar"

say() { printf '\n== %s\n' "$1"; }

say "Checking $CONTAINER is up"
docker exec "$CONTAINER" ollama list >/dev/null

DEFAULT_BASE=$(awk '/^FROM /{print $2; exit}' "$MODELFILE")
BASE=${BASE:-$DEFAULT_BASE}

say "Pulling base $BASE"
docker exec "$CONTAINER" ollama pull "$BASE"

say "Pulling embedding model $EMBED"
docker exec "$CONTAINER" ollama pull "$EMBED"

say "Creating $MODEL from $BASE"
sed "s|^FROM .*|FROM $BASE|" "$MODELFILE" | docker exec -i "$CONTAINER" sh -c 'cat > /tmp/Modelfile.familiar'
docker exec "$CONTAINER" ollama create "$MODEL" -f /tmp/Modelfile.familiar

say "Verifying"
params=$(docker exec "$CONTAINER" ollama show "$MODEL" --parameters)
expected=$(awk '/^PARAMETER num_ctx/{print $3; exit}' "$MODELFILE")
if echo "$params" | grep -Eq "num_ctx[[:space:]]+$expected"; then
    echo "num_ctx $expected: ok"
else
    echo "num_ctx did not take - expected $expected, got:" >&2
    echo "$params" >&2
    exit 1
fi

# Tool calling is a hard requirement (PLAN.md §4.1). Ollama lists it under Capabilities.
if docker exec "$CONTAINER" ollama show "$MODEL" | grep -A6 -i '^ *capabilities' | grep -qw tools; then
    echo "tools capability: ok"
else
    echo "WARNING: $BASE does not advertise tool calling - it cannot drive Familiar's tools" >&2
fi

say "Done: $MODEL (base $BASE)"
