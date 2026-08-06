# Phase 1A - Docker Containerization and Distribution

> **Historical phase plan:** The one-command Compose topology described here has not yet been
> migrated to the hardware-isolated agent architecture. Docker remains required trusted
> infrastructure, but it must not run untrusted agents or receive agent-runner privileges. Use the
> [current Docker infrastructure guide](../../deployment/docker.md) for supported startup guidance
> and migration status.

## Goal

Make Docker containerization a first-class part of the platform from the beginning so new users can run C-Sweet with one command instead of manually installing and wiring every service.

The target first-run experience should be:

```bash
cp .env.example .env
docker compose up -d
```

Then the user opens:

```text
http://localhost:8080
```

and lands in the C-Sweet setup wizard.

## Why this phase belongs near the beginning

Docker will likely be the easiest distribution path for self-hosted users. It also gives developers a repeatable local environment and makes later deployment to a VPS, NAS, home server, or private cloud much simpler.

C-Sweet should still support normal `dotnet run` development through Aspire, but Docker Compose should be the default “I just want to run it” path.

## Relationship to .NET Aspire

Use both:

- **Aspire AppHost** for developer inner-loop orchestration and observability.
- **Docker Compose** for user-facing self-hosted distribution.

Do not require end users to understand Aspire to run the platform.

## Deliverables

- Root `Dockerfile` or service-specific Dockerfiles.
- `docker-compose.yml` for normal self-hosted runtime.
- `docker-compose.override.yml` for local development overrides if useful.
- `.dockerignore`.
- `.env.example`.
- `docs/deployment/docker.md`.
- Container health checks.
- Persistent volumes for database and artifact storage.
- Clear LM Studio host connection guidance.
- GitHub Actions image build placeholder or full image publishing workflow.

## Recommended container services

Initial Compose stack:

```text
csweet-web
csweet-api
csweet-worker
postgres
minio optional
qdrant optional later
redis optional later
```

### Minimum first vertical slice stack

For the first usable Docker distribution, include:

```text
csweet-api
csweet-app
csweet-worker
postgres
```

Add MinIO/Qdrant/Redis when the code actually uses them.

## Proposed file layout

```text
/docker
  /api.Dockerfile
  /app.Dockerfile
  /worker.Dockerfile
  /nginx.conf optional
  /entrypoints
    wait-for-db.sh optional

docker-compose.yml
docker-compose.override.yml optional
.env.example
.dockerignore

docs/deployment/docker.md
```

Alternative acceptable layout:

```text
src/CSweet.Api/Dockerfile
src/CSweet.App/Dockerfile
src/CSweet.WorkerHost/Dockerfile
```

Either layout is acceptable, but use one pattern consistently.

## Port conventions

Recommended defaults:

```text
8080  Public web app / reverse proxy
8081  API, internal or developer exposed
8082  WorkerHost, internal only by default
5432  Postgres, not exposed by default unless development override is enabled
9000  MinIO API optional
9001  MinIO console optional
```

For self-hosted distribution, prefer exposing only the web entry point by default.

## Environment variables

Create `.env.example` with safe defaults and comments.

```env
# Public URL users open in the browser
CSWEET_PUBLIC_URL=http://localhost:8080

# Database
POSTGRES_DB=csweet
POSTGRES_USER=csweet
POSTGRES_PASSWORD=change-me
CSWEET_CONNECTIONSTRINGS__POSTGRES=Host=postgres;Port=5432;Database=csweet;Username=csweet;Password=change-me

# First-run setup
CSWEET_SETUP__ALLOW_FIRST_RUN=true

# Default local LLM provider preset
CSWEET_LLM__DEFAULT_PROVIDER_TYPE=LMStudio
CSWEET_LLM__DEFAULT_BASE_URL=http://host.docker.internal:1234/v1
CSWEET_LLM__DEFAULT_API_KEY=lm-studio
CSWEET_LLM__DEFAULT_CHAT_MODEL=

# Runtime
ASPNETCORE_ENVIRONMENT=Production
DOTNET_ENVIRONMENT=Production
```

## LM Studio from containers

When LM Studio runs on the host machine and C-Sweet runs in Docker, `localhost` from inside the container means the container itself, not the host.

For Docker Desktop on Windows/macOS, use:

```text
http://host.docker.internal:1234/v1
```

For Linux, developers may need to add an `extra_hosts` mapping or use the host gateway feature:

```yaml
extra_hosts:
  - "host.docker.internal:host-gateway"
```

The setup wizard should explain this when the app is running inside Docker.

## Compose baseline

Initial `docker-compose.yml` should look conceptually like this:

```yaml
services:
  csweet-app:
    build:
      context: .
      dockerfile: docker/app.Dockerfile
    ports:
      - "8080:80"
    depends_on:
      csweet-api:
        condition: service_healthy
    environment:
      CSWEET_API_BASE_URL: http://csweet-api:8080

  csweet-api:
    build:
      context: .
      dockerfile: docker/api.Dockerfile
    depends_on:
      postgres:
        condition: service_healthy
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ConnectionStrings__Postgres: Host=postgres;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/api/health"]
      interval: 10s
      timeout: 5s
      retries: 5

  csweet-worker:
    build:
      context: .
      dockerfile: docker/worker.Dockerfile
    depends_on:
      postgres:
        condition: service_healthy
      csweet-api:
        condition: service_healthy
    environment:
      DOTNET_ENVIRONMENT: Production
      ConnectionStrings__Postgres: Host=postgres;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}

  postgres:
    image: postgres:17-alpine
    environment:
      POSTGRES_DB: ${POSTGRES_DB}
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      - csweet-postgres:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER} -d ${POSTGRES_DB}"]
      interval: 10s
      timeout: 5s
      retries: 5

volumes:
  csweet-postgres:
```

Adjust service names and ports once the actual project names are finalized.

## Dockerfile requirements

Each .NET service image should use multi-stage builds:

```text
restore layer
build layer
publish layer
runtime layer
```

Requirements:

- Build in Release mode.
- Run as a non-root user when practical.
- Do not copy `.git`, `bin`, `obj`, local secrets, or model files.
- Keep image-specific files deterministic.
- Use official Microsoft .NET images.
- Pin major/minor tags initially; consider digest pinning for production releases.

## Blazor WASM container options

There are two acceptable first-version options.

### Option A - Static app served by Nginx

Build the Blazor WASM app and serve static files from Nginx.

Pros:

- Small runtime image.
- Common static hosting pattern.
- Keeps frontend separate from API.

Cons:

- Needs runtime configuration injection for API base URL.

### Option B - ASP.NET Core hosted app

Serve the Blazor app from an ASP.NET Core host.

Pros:

- Easier dynamic configuration.
- Simpler local routing.

Cons:

- Larger runtime.
- Slightly more coupling.

Recommendation for first implementation:

Use whichever matches the actual Blazor project template selected in Phase 1, but document the choice clearly.

## Persistent data

The first Compose setup must persist:

```text
Postgres data
Artifact files when file storage is added
Object storage data if MinIO is added
```

Do not store persistent data only inside disposable containers.

## Migrations

Choose one migration strategy for Docker startup.

### Option A - App applies migrations on startup

Pros:

- Easiest user experience.

Cons:

- Requires careful locking in multi-instance deployments.

### Option B - Migration job container

Pros:

- More explicit.
- Better for production later.

Cons:

- More Compose complexity.

Recommendation for early self-hosted distribution:

- Use a migration job or a clearly isolated startup migration service if easy.
- If the API applies migrations on startup, guard it so only one instance runs migrations.

## Image publishing

Add a GitHub Actions workflow later or in this phase if the repo is ready:

```text
.github/workflows/docker-publish.yml
```

Expected behavior:

- Build images on pull request to validate Dockerfiles.
- Publish images on tags.
- Push to GHCR.

Suggested image names:

```text
ghcr.io/crosswiredstudios/csweet-app
ghcr.io/crosswiredstudios/csweet-api
ghcr.io/crosswiredstudios/csweet-worker
```

## Developer commands

Add these to `docs/deployment/docker.md`:

```bash
# Start stack
cp .env.example .env
docker compose up -d

# View logs
docker compose logs -f

# Stop stack
docker compose down

# Stop stack and remove volumes
docker compose down -v

# Rebuild after code changes
docker compose build
docker compose up -d
```

## Health checks

Minimum health checks:

```text
csweet-api: GET /api/health
csweet-worker: GET /api/worker-host/health or process health
postgres: pg_isready
csweet-app: static/index response or reverse proxy health
```

## First-run setup in Docker

The Docker stack should start into the same first-run setup flow as development mode:

```text
Container stack starts
  → Postgres initializes
  → API applies/validates migrations
  → SystemConfiguration seeded with IsFirstRunComplete = false
  → UI opens setup wizard
  → user configures LM Studio using host.docker.internal
```

## Testing requirements

### Automated tests

- Dockerfiles build in CI.
- Compose file validates.
- API container starts and health endpoint returns healthy.
- API can connect to Postgres container.

### Manual QA

From a clean machine with Docker installed:

```bash
git clone <repo>
cd csweet
cp .env.example .env
docker compose up -d
```

Verify:

- `docker compose ps` shows healthy services.
- Opening `http://localhost:8080` shows setup wizard.
- Setup wizard can configure LM Studio at `http://host.docker.internal:1234/v1`.
- Stopping and starting the stack preserves setup state.
- `docker compose down -v` resets state.

## Acceptance criteria

- [x] Root Compose file exists.
- [x] Dockerfiles exist for runtime services.
- [x] `.dockerignore` exists.
- [x] `.env.example` exists.
- [x] Docker deployment docs exist.
- [ ] `docker compose up -d` starts the platform.
- [x] Database data is persisted in a named volume.
- [ ] First-run setup works from Docker.
- [x] LM Studio host connection guidance is documented.
- [ ] CI validates image builds before release.

## Implementation status

Partially complete.

Completed:

- Service-specific Dockerfiles for API, Blazor app, and worker.
- Root `docker-compose.yml` with `csweet-app`, `csweet-api`, `csweet-worker`, and `postgres`.
- Named `csweet-postgres` volume.
- App container serves Blazor WASM through nginx and proxies `/api` to `csweet-api`.
- API, app, and Postgres health checks.
- Docker deployment documentation and LM Studio host guidance.

Verified:

- `docker compose config`
- `dotnet publish src/CSweet.Api/CSweet.Api.csproj -c Release`
- `dotnet publish src/CSweet.App/CSweet.App.csproj -c Release`
- `dotnet publish src/CSweet.WorkerHost/CSweet.WorkerHost.csproj -c Release`

Not yet verified:

- `docker compose build`
- `docker compose up -d`

The Docker daemon was not running during validation, so full image build and container startup still need to be run once Docker Desktop is available.

Deferred to later phases:

- Real first-run setup persistence.
- Database migrations or a migration job container.
- CI image build/publish workflow.

## Common mistakes

- Do not assume `localhost:1234` reaches LM Studio from inside a container.
- Do not expose Postgres publicly by default.
- Do not bake secrets into images.
- Do not require users to install the .NET SDK to run the Docker distribution.
- Do not store user data in containers without volumes.
- Do not make Docker depend on the hosted marketplace.
