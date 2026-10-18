# Familiar on TrueNAS

TrueNAS's "Install via YAML" takes the compose file alone: there's no `.env` beside it, so every
`${VAR:-default}` resolves to its default. Write the settings into the file as plain values instead
(passwords included; the file lives in TrueNAS's app configuration, so don't commit or share it).

## Moving the data onto a dataset

With the defaults, the data and backup folders are relative (`./data`, `./backups`), which TrueNAS
resolves inside its own app storage under `/mnt/.ix-apps/`. That's a poor home for them: deleting
or reinstalling the app can remove them, they're not in any snapshot task, and the backups sit
next to the database they're meant to protect. These steps move them onto a dataset you own.

What moves: `data/postgres` (the database), `data/logs` (the server's logs), `data/ntfy` (push,
if ntfy has run) and `backups/`. Ollama's models are already on an absolute path
(`OLLAMA_MODELS_DIR`), and Whisper's model cache is a Docker volume; neither moves.

Run the commands as root, in **System › Shell** (`sudo -i` first).

### 1. Check where things are now

```bash
docker inspect familiar-postgres familiar-backup --format '{{.Name}}{{range .Mounts}}  {{.Source}} -> {{.Destination}}{{end}}'
```

If the sources are under `/mnt/.ix-apps/...`, carry on. The folder that holds `data/` and
`backups/` is the **old folder** below (the postgres source minus `/data/postgres`); for a custom
YAML app it's like `/mnt/.ix-apps/app_configs/familiar/versions/1.0.0/templates/rendered`. TrueNAS
generates that folder from the YAML, so move the data out before editing the app for anything else.

### 2. Take a backup first, and keep it outside the app

Create the dataset in **Datasets › Add Dataset**: e.g. `<pool>/familiar`, preset *Generic*. (A
dataset, not a folder made with `mkdir`: a folder can't have its own snapshots or replication, and
the commands below fail if it doesn't exist.) Then a
copy straight from the database onto it (this needs no password, so it works even if the backup
container's is missing):

```bash
docker exec familiar-postgres pg_dump -U familiar -d familiar --format=custom > /mnt/<pool>/familiar/before-move.dump
```

```bash
ls -lh /mnt/<pool>/familiar/before-move.dump
```

A few MB or more; not zero. If the backup container has the database password (an app deployed
with `${POSTGRES_PASSWORD}` and no `.env` gave it an empty one), a verified backup too:

```bash
docker exec familiar-backup familiar-backup --once
```

If that dump failed, a file-level copy works as well once the app is stopped (step 3), before
copying:

```bash
tar -C "$OLD" -czf /mnt/<pool>/familiar/before-move.tgz data backups
```

### 3. Stop the app

**Apps › familiar › Stop**, then check nothing of it is running (no output):

```bash
docker ps --filter name=familiar- --format '{{.Names}}'
```

### 4. Copy, keeping owners and permissions

Postgres refuses to start on a data folder that isn't owned by its own user (uid 999) with mode
0700, so copy with numeric ownership kept. Set the two folders once (replace both):

```bash
OLD=/mnt/.ix-apps/app_configs/familiar/<the old folder>; NEW=/mnt/<pool>/familiar
```

```bash
rsync -aH --numeric-ids "$OLD/data/" "$NEW/data/"
```

```bash
rsync -aH --numeric-ids "$OLD/backups/" "$NEW/backups/"
```

Check the copy matches and the database folder kept its owner. Postgres 18 keeps its data in
`postgres/18/docker`, which must be `999 999` and `drwx------` (the folders above it are root's):

```bash
du -s "$OLD/data" "$NEW/data" "$OLD/backups" "$NEW/backups"
```

```bash
ls -ln "$NEW/data/postgres/18"
```

### 5. Point the app at the new folders

**Apps › familiar › Edit**, and in the YAML replace every `./data/` with `/mnt/<pool>/familiar/data/`
and `./backups` with `/mnt/<pool>/familiar/backups`. Four lines change: the postgres, server and
ntfy volumes and the backup volume. Save; the app starts.

### 6. Check it

```bash
docker inspect familiar-postgres familiar-backup --format '{{.Name}}{{range .Mounts}}  {{.Source}} -> {{.Destination}}{{end}}'
```

Now under `/mnt/<pool>/familiar/`. Then:

```bash
docker logs familiar-postgres 2>&1 | tail -5
```

"database system is ready to accept connections", and no "initdb" (which would mean it started an
empty database instead of finding yours). Sign in on the web: your conversation, memories and
timers are there. Then one more backup, into the new folder:

```bash
docker exec familiar-backup familiar-backup --once
```

### 7. Afterwards

- Keep the old folder for a week, then delete it (`rm -rf "$OLD/data" "$OLD/backups"`). Keep
  `before-move.dump` until then too.
- **Data Protection › Periodic Snapshot Tasks:** a daily snapshot of `<pool>/familiar`.
- The nightly dumps protect against a broken database, not a lost pool: replicate
  `<pool>/familiar/backups` (or the whole dataset) to another machine or a cloud target
  (**Data Protection › Replication / Cloud Sync**).

**If something's wrong** at step 6: stop the app, put the YAML's paths back to `./data` and
`./backups`, and start it; the old folder is untouched. If the database itself is the problem,
`before-move.dump` restores with `pg_restore`.

## Upgrading Ollama (0.40.6, BACKLOG item 37)

0.40.6 halves the wait for the familiar's first words: it reuses the prompt between turns and
generates about three times faster. In the app's YAML (already changed in
`docker-compose.beta.nas.yml`): the image `ollama/ollama:0.40.6`, and beside `OLLAMA_NUM_THREAD`:

```yaml
      LLAMA_ARG_THREADS: 4
      LLAMA_ARG_THREADS_BATCH: 4
      LLAMA_ARG_CTX_CHECKPOINTS: 4
```

Without the first two it starts a thread per core inside the four-CPU limit and is throttled back
to the old speed; without the third its prompt-cache restore points (320 MB each, 32 by default) grow
past the memory limit. Then redeploy, and rebuild the derived model once from a machine with Docker
access: `DOCKER_HOST=ssh://nas sh tools/ollama/create-models.sh`.

Check the GPU afterwards, with a conversation going and speech used once: `nvidia-smi` on the NAS.
The model now takes about 9.4 GB with its draft model and picture encoder (8.9 before), so with speech
the 3060 is near its 12 GB. If speech-to-text fails with "CUDA out of memory" again, the fallbacks in
order: a smaller context (`num_ctx` 6144 in the Modelfile), or speech on the CPU.

## Push to phones (ntfy)

With the app open, messages arrive on its own connection; while it's in the background they come up
as notifications. When Android has put the app to sleep or it's closed, only a push wakes it - for
announcements (a hook's "speak"), timers and reminders. Push goes through the `ntfy` service in the
YAML, and is off until it has an address.

1. In the YAML, give both settings the same value - the address the **phone** reaches ntfy at.
   Behind a reverse proxy (Nginx Proxy Manager), ntfy needs **a hostname of its own**, e.g.
   `push.example.net` forwarding to the NAS on port 2586 with websockets on and a certificate: it
   can't run under a path like `/push` on Familiar's host (it builds the phones' push addresses from
   its base URL, and doesn't support a path in it).

   ```yaml
     ntfy:
       environment:
         NTFY_BASE_URL: https://push.example.net
         NTFY_BEHIND_PROXY: 'true'
     server:
       environment:
         Push__UnifiedPushServer: https://push.example.net
   ```

   Without a proxy it's `http://<nas-address>:2586` for both, and `NTFY_BEHIND_PROXY` stays
   `'false'`. Either way the two must match exactly (scheme, host and port): ntfy builds each phone's
   push address from `NTFY_BASE_URL`, and the server only pushes to that host. Not `localhost` -
   that's the phone itself. The server container must be able to reach the address too.

   To check the proxy: `https://push.example.net/v1/health` says `{"healthy":true}`. (`/v1/config`
   always shows an empty `base_url` - that's for ntfy's web page - so it doesn't show the setting.)
2. Update the app so the containers restart.
3. On the phone: install the ntfy app and set its default server (Settings, Default server) to the
   same address. Then open Familiar; it registers with ntfy by itself.

To check: set a one-minute timer, close Familiar (swipe it away), and the timer still rings; or send
an announcement from Home Assistant with the app closed, and a notification comes up.
