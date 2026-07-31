# Docker Deployment

## Purpose

Docker Compose should be the default self-hosted distribution path for C-Sweet.

The goal is that a user can clone the repository, copy the example environment file, start the Compose stack, and complete first-run setup in the browser.

## Administrator email configuration

Do not expose a fresh installation to an untrusted network. The first visitor can claim the root administrator account, so complete registration and onboarding on a trusted network first.

The first browser visit opens `/register`. Registration does not require SMTP or email confirmation: it signs the root administrator in immediately and displays ten one-time offline recovery codes. Save those codes before continuing. Direct registration closes permanently after the first administrator is created.

Email delivery is an optional, skippable technical-setup step. You can enter SMTP settings in the setup wizard, save them, and send a test message to the root administrator. After a successful test, C-Sweet can send root email verification and email password recovery; future invitations will also depend on this tested-sender state. Until then, offline root recovery codes remain available.

The `CSWEET_SMTP__*` values in `.env.example` are optional initial defaults. `CSWEET_SMTP__PUBLICAPPURL` must be the browser-visible root URL (for example, `http://localhost:8080`) so confirmation and reset links return to the app. When settings are saved through setup, the SMTP password is encrypted with the instance's persisted ASP.NET data-protection keys and is never returned to the browser.

```bash
cp .env.example .env
docker compose up -d
```

Then open:

```text
http://localhost:8080
```

## Services

The Docker distribution includes:

| Service | Internal Port | Exposed | Description |
|---------|--------------|---------|-------------|
| `csweet-app` | 8080 | 8080 (configurable via `APP_PORT`) | Blazor WASM frontend served by nginx |
| `csweet-migrator` | - | Internal only | One-shot database migration and setup seed runner |
| `csweet-api` | 8080 | Internal only | API and application services |
| `csweet-agenthost` | 8080, 8081 | Internal only | Brokered agent API and private MCP runtime gateway |
| `csweet-worker` | - | Internal only | Local worker runtime |
| `postgres` | 5432 | Internal only | PostgreSQL database |

Only the web app (`csweet-app`) is exposed publicly by default. The API, worker, and database are internal-only services.

Optional services can be added when needed:

```text
minio           S3-compatible object storage for artifacts
qdrant          Semantic retrieval/vector storage
redis           Cache, backplane, or queue support
```

## LM Studio configuration

When running C-Sweet directly on your machine, LM Studio is usually reachable at:

```text
http://localhost:1234/v1
```

When running C-Sweet inside Docker and LM Studio on the host machine, use:

```text
http://host.docker.internal:1234/v1
```

The included Compose file maps this hostname to the Docker host on Linux:

```yaml
extra_hosts:
  - "host.docker.internal:host-gateway"
```

The setup wizard probes both addresses and prefers the Docker-safe value when it detects container mode.

## Required files (implemented)

```text
docker-compose.yml
.env.example
.dockerignore
docker/api.Dockerfile
docker/app.Dockerfile
docker/agenthost.Dockerfile
docker/migrator.Dockerfile
docker/worker.Dockerfile
```

## Environment file

The `.env.example` file should include safe placeholders:

```env
CSWEET_PUBLIC_URL=http://localhost:8080

POSTGRES_DB=csweet
POSTGRES_USER=csweet
POSTGRES_PASSWORD=change-me

CSWEET_LLM__DEFAULT_PROVIDER_TYPE=LMStudio
CSWEET_LLM__DEFAULT_BASE_URL=http://host.docker.internal:1234/v1
CSWEET_LLM__DEFAULT_API_KEY=lm-studio
CSWEET_LLM__DEFAULT_CHAT_MODEL=

# Override when intentionally running multiple C-Sweet Compose stacks.
CSWEET_AGENTHOST_CONTAINER_NAME=csweet-agenthost
```

Do not commit a real `.env` file.

## Basic commands

```bash
# Start stack
cp .env.example .env
docker compose up -d

# Show running services
docker compose ps

# View logs
docker compose logs -f

# Rebuild images
docker compose build

# Stop services but keep data
docker compose down

# Stop services and remove local volumes/data
docker compose down -v
```

## Data persistence

Postgres must use a named volume so setup state, organizations, tasks, artifacts, and audit history survive container restarts.

Example:

```yaml
volumes:
  csweet-postgres:
```

Do not store user data only inside disposable containers.

## Health checks

Minimum health checks:

```text
csweet-api      GET /api/health
csweet-worker   process health
csweet-app      HTTP response on public port
postgres        pg_isready
```

Health checks should be used by Compose dependencies where practical so the app does not start before the database is ready.

## Agent container lifecycle safety

The worker owns transient agent containers named `csweet-agent-<runtime-id>`. When the worker starts, it removes containers with that exact reserved naming pattern from an earlier worker lifetime and removes their isolated runtime networks. Database reconciliation then records interrupted runtimes and restarts eligible always-on agents.

Each newly launched agent also runs under a broker watchdog. After the startup grace period, the watchdog probes the configured agent broker from inside the runtime network. If the broker remains unreachable for the disconnect timeout, it terminates the agent process. This prevents a debugger kill, worker crash, or broker outage from leaving a resource-consuming agent running indefinitely.

The Compose defaults are:

```env
CSWEET_AGENT_RUNTIME_CLEANUP_ON_STARTUP=true
CSWEET_AGENT_RUNTIME_SESSION_WATCHDOG_ENABLED=true
CSWEET_AGENT_RUNTIME_SESSION_WATCHDOG_STARTUP_GRACE_SECONDS=30
CSWEET_AGENT_RUNTIME_SESSION_WATCHDOG_INTERVAL_SECONDS=10
CSWEET_AGENT_RUNTIME_SESSION_DISCONNECT_SHUTDOWN_SECONDS=120
```

Disable startup cleanup when multiple independent scheduler processes intentionally share the same Docker daemon. The watchdog requires the supported Linux .NET runtime image to provide `/bin/bash`.

## First-run Docker flow

```text
docker compose up -d
  → postgres starts and becomes healthy (pg_isready health check)
  → migrator applies database migrations and seed data
  → agenthost starts the private MCP runtime gateway
  → API starts after migrations complete and exposes /api/health
  → worker and app start after API is healthy
  → user opens http://localhost:8080
  → setup wizard opens
  → user configures LM Studio using host.docker.internal
```

Database migrations run through the dedicated `csweet-migrator` one-shot service, not inside the API or worker containers.

## Security defaults

- Do not expose Postgres outside the Compose network by default.
- Do not bake secrets into images.
- Do not commit `.env`.
- Do not log API keys.
- Prefer named volumes for persistent data.
- Prefer internal networking between app, API, worker, and database.

## Release image publishing

When the project is ready to publish images, use GitHub Actions to build on PRs and publish on tags.

Suggested image names:

```text
ghcr.io/crosswiredstudios/csweet-app
ghcr.io/crosswiredstudios/csweet-api
ghcr.io/crosswiredstudios/csweet-migrator
ghcr.io/crosswiredstudios/csweet-worker
```

## Acceptance test

A clean-machine Docker acceptance test should verify:

```bash
git clone <repo>
cd csweet
cp .env.example .env
docker compose up -d
```

Then confirm:

- `docker compose ps` reports healthy services.
- `http://localhost:8080` opens the setup wizard.
- LM Studio can be configured using `http://host.docker.internal:1234/v1`.
- Restarting containers preserves setup state.
- `docker compose down -v` resets the local install.
