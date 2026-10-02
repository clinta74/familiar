# Familiar

A voice-first AI companion with an expressive 2D/3D avatar, running entirely against local models.
Web first, then native Android and iOS, one server for all of them.

- [docs/PLAN.md](docs/PLAN.md) — server and web design, deployment, phases
- [docs/MOBILE.md](docs/MOBILE.md) — Android and iOS

**Status: Phase 1 (text + avatar).** Talk to your familiar by typing: replies stream in from the local model, and its plush llama avatar reacts - ears, eyes, brows, blush, tears, gestures - to what it says.
Accounts, browser sessions and phone device tokens work; local and NAS stacks, images and CI are in
place. Voice is phase 2.

---

## Prerequisites

| | Version | |
|---|---|---|
| .NET SDK | 10.0+ | `dotnet --version` |
| Node.js | 22+ | `node --version` |
| Docker | Rancher Desktop / Docker Desktop | must be running |
| `dotnet-ef` | 10.x | `dotnet tool install --global dotnet-ef` |

## Local development

```bash
cp .env.example .env              # defaults are fine
docker compose up -d              # postgres (pgvector) + adminer

dotnet run --project src/Familiar.Server --launch-profile http     # http://localhost:5280

cd client && npm install && npm run dev                            # http://localhost:5273
```

Open <http://localhost:5273> and register. **The first account on an empty database becomes the
admin.** The server applies migrations on startup.

| Service | URL |
|---|---|
| Client (Vite) | <http://localhost:5273> |
| Server | <http://localhost:5280> |
| Liveness / readiness | `/health`, `/health/ready` |
| Adminer | <http://localhost:8081> |
| Ollama | <http://localhost:11435> (`--profile llm`) |
| Speech-to-text | <http://localhost:8001> (`--profile speech`) |
| Text-to-speech | <http://localhost:8002> (`--profile speech`) |

Every port differs from muwbta's dev stack, so both can run at once.

### AI services

```bash
docker compose --profile llm --profile speech up -d                                   # CPU
docker compose -f docker-compose.yml -f docker-compose.gpu.yml --profile llm --profile speech up -d   # GPU
sh tools/ollama/create-models.sh                                                      # build familiar-chat
```

The GPU overlay uses WSL2's `/dev/dxg`, not the NVIDIA toolkit; see the comments in
[docker-compose.gpu.yml](docker-compose.gpu.yml). Choosing the model is the Phase 0 bake-off:
[tools/ollama/README.md](tools/ollama/README.md).

### Why the client proxies the API

Browser sessions are an HttpOnly, `SameSite=Lax` cookie, and the conversation's event stream
(`EventSource`) can't send an `Authorization` header — so `/api` is proxied through Vite (and
through nginx in the image) to keep the browser on one origin. Phones use bearer device tokens
instead.

## Authentication

| | Browser | Phone apps |
|---|---|---|
| Credential | `familiar.session` cookie, 14-day sliding | `fam_…` device token, 180-day sliding |
| Sign in | `POST /api/auth/login` | `POST /api/auth/devices/login` |
| Sign out | `POST /api/auth/logout` | `DELETE /api/auth/devices/{id}` |

- A password change signs out every other browser session and every device (security stamp).
- Cookie sessions re-check the account once a minute; device tokens on every request.
- Device tokens can't change the password. Settings → Devices signs out a lost phone.
- Unknown user and wrong password are indistinguishable, by response and by timing.
- Per-username login backoff, plus a per-address rate limit on the auth endpoints.
- Data Protection keys are stored in Postgres, so recreating the server container doesn't sign
  everyone out. They are stored unencrypted (the startup warning says so); anyone who can read
  the database can already read everything else in it.
- After the admin exists, set `Auth__AllowRegistration=false` (the NAS default).

## Tests

```bash
dotnet test                 # server: unit + integration against a real Postgres (Testcontainers)
cd client && npm test       # client
```

Server tests start a `pgvector/pgvector:pg18` container and give every test its own database, so
the first-account-is-admin rule and the registration lock are genuinely exercised. Docker must be
running.

## Deployment (NAS)

Images are built by GitHub Actions and pushed to Docker Hub from `main` and `v*` tags:
`familiar-server`, `familiar-client`, `familiar-backup`. Set the `DOCKER_USERNAME` and
`DOCKER_PASSWORD` repository secrets.

```bash
cd deploy
cp .env.example .env        # set POSTGRES_PASSWORD and paths
docker compose -f docker-compose.nas.yml up -d
docker compose -f docker-compose.nas.yml -f docker-compose.nas.gpu.yml up -d    # with the 3060
```

The client listens on **7280** (HTTPS, self-signed unless a cert is mounted). Phones reach it over
the VPN. Backups run nightly at 03:00 UTC, each one test-restored before it's kept.

**Stop muwbta's Ollama first.** The 3060 holds Familiar's model and the speech models (~10.4 GB)
but not muwbta's 8.4 GB builder model as well; see PLAN.md §10.3 and the comments in
[deploy/docker-compose.nas.gpu.yml](deploy/docker-compose.nas.gpu.yml).

## Layout

```
src/
  Familiar.Domain/        entities. No dependencies.
  Familiar.Persistence/   EF Core 10 + Npgsql, migrations, snake_case naming
  Familiar.Server/        ASP.NET Core: auth, health, (later) conversation, tools, scheduler
client/                   React 19 + Vite + TypeScript, nginx image
tests/                    xUnit + Testcontainers
tools/ollama/             Modelfile, create-models.sh, bake-off
tools/backup/             verified nightly pg_dump sidecar
deploy/                   NAS compose files
```

## Conventions

- **Warnings are errors**, and `.editorconfig` severities are enforced on build.
- **Package versions live in `Directory.Packages.props`.**
- **Logging uses `[LoggerMessage]` source generators** (`ServerLog.cs`).
- **Time comes from `TimeProvider`**, never `DateTimeOffset.UtcNow`, so tests can control it.
- **Database identifiers are snake_case**, enforced by `SnakeCaseNaming`.
- **Migrations are explicit and applied at startup**; never `EnsureCreated`.

```bash
dotnet ef migrations add <Name> --project src/Familiar.Persistence --startup-project src/Familiar.Persistence --output-dir Migrations
```
