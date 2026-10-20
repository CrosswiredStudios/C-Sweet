# Docker infrastructure and deployment

## Current support status

Docker Desktop is required for the current source-development experience. The Aspire AppHost provisions PostgreSQL and SearXNG through Docker, and the application cannot complete startup while the Docker engine is unavailable.

Docker is **trusted application infrastructure**. It is not the isolation boundary for imported or marketplace agents. On Windows, untrusted agent code runs only in a certified Hyper-V Generation 2 virtual machine managed by RuntimeHost. If that provider is unavailable, agent execution stays disabled; C-Sweet does not fall back to Docker or a host process.

See the [security architecture overview](../../Documentation/Architecture/AGENT_ISOLATION_SECURITY_OVERVIEW.md) for the trust boundaries and startup sequence.

## Windows source prerequisites

- Docker Desktop installed and configured to use Linux containers.
- Docker Desktop's engine running; `docker info` must succeed.
- The .NET 10 SDK specified by `global.json`.
- Git and an OpenAI-compatible model endpoint.
- For local untrusted agents: a supported Windows edition and hardware virtualization. Browser onboarding guides the separate Hyper-V and RuntimeHost setup.

## Recommended startup

After cloning the repository, double-click `Start-CSweet.cmd` in the repository root. The launcher:

1. Verifies the .NET 10 SDK.
2. Verifies the Docker CLI and engine.
3. Attempts to start Docker Desktop when it is installed but stopped.
4. Waits up to two minutes for the engine while reporting elapsed time.
5. Verifies the local HTTPS development certificate, asks Windows to trust it when needed, and falls back to the local HTTP profile if certificate trust is unavailable.
6. Starts the Aspire AppHost, which provisions PostgreSQL and SearXNG, runs migrations, and starts the C-Sweet projects. Docker downloads missing images automatically on the first run.

Developers can start the same topology from an IDE or with:

```powershell
dotnet run --project src/CSweet.AppHost/CSweet.AppHost.csproj --launch-profile https
```

When starting AppHost directly, the IDE or terminal does not currently start Docker Desktop for you. Check it first:

```powershell
docker info
```

## Administrator and email setup

Do not expose a fresh installation to an untrusted network. The first visitor can claim the root administrator account, so complete registration and onboarding on a trusted network first.

The first browser visit opens `/register`, where the root administrator provides their name, email, and password. Registration signs the administrator in immediately and displays ten one-time offline recovery codes. Save those codes before continuing. Direct registration closes permanently after the first administrator is created.

Email delivery is optional. SMTP profiles can be created and tested during setup or later from Account Security. Until a default profile passes its test, offline root recovery codes remain available.

## Model endpoint configuration

When C-Sweet runs through AppHost and the model server runs directly on Windows, use the endpoint reported by that model server. Common local defaults include:

```text
http://localhost:1234/v1
http://host.docker.internal:1234/v1
```

The browser setup flow probes compatible addresses. `host.docker.internal` is primarily needed when the caller itself runs inside a container.

## Data persistence

Aspire stores PostgreSQL data in a named Docker volume so company state, setup data, tasks, artifacts, and audit history survive restarts. Treat that volume as application data and include it in the development backup/reset policy.

Changing the configured PostgreSQL credentials does not change credentials already stored in an initialized volume. See the [debug guide](../implementation/debug-guide.md#aspire-postgres-authentication) for the deliberate development reset procedure.

## SearXNG search infrastructure

`src/CSweet.AppHost/Program.cs` registers `searxng` as a Docker container. Running
`Start-CSweet.cmd`, an IDE AppHost session, or `dotnet run --project src/CSweet.AppHost`
starts it automatically. Aspire allocates a local HTTP endpoint, displays it in the
dashboard, and checks `/healthz`. AgentHost receives that endpoint as
`CSweet:Search:Searxng:BaseUrl`. No user-supplied instance URL or search API key is required.
The core services do not wait for search health, so a search outage does not prevent
access to the application. Agent-facing search callbacks and page reading are future work.

`docker-compose.yml` also includes the `searxng` service in the default topology, using
the same image and mounted configuration. AgentHost receives `http://searxng:8080`;
the search port is not published to the host. This packages the search dependency for
container deployments; the unrelated Compose migration blockers below still apply.
The search service alone can be started with `docker compose up -d searxng`.

- Image: `docker.io/searxng/searxng:2026.10.19-81371861f`, pinned to the multi-platform
  digest `sha256:5ed0323b5f5f8701264ef65e239a2bd83cb57098f2b971fa30bf79c06eeae271`.
  Update both AppHost and Compose pins together after validating an upstream upgrade.
- Settings: `docker/searxng/settings.yml`, mounted read-only. HTML and JSON are enabled;
  the upstream DuckDuckGo and Brave engines require no paid API keys.
  The service is private and has public-instance bot limiting disabled. Keep it off
  public ingress; future agent access belongs behind the authenticated broker.
- Startup: `docker/searxng/start.sh` generates a random `SEARXNG_SECRET` unless one is
  already supplied, then executes the unmodified upstream entrypoint. No secret is
  committed or printed. Without an explicitly supplied secret, browser sessions reset
  when the container restarts. `.gitattributes` preserves Linux shell line endings.
- Cache: the disposable `csweet-searxng-cache` Docker volume survives restarts. It
  contains SearXNG cache data, not canonical C-Sweet company records.

For a development smoke check, copy the `searxng` HTTP URL from the Aspire dashboard:

```powershell
$searchUrl = 'http://localhost:<port-from-dashboard>'
Invoke-RestMethod "$searchUrl/healthz"
$result = Invoke-RestMethod "$searchUrl/search?q=Microsoft%20Learn%20ASP.NET%20Core&format=json"
$result.results | Select-Object -First 3 title, url
$result.unresponsive_engines
```

`/healthz` confirms the local server is running. The JSON search additionally checks
the enabled format and upstream connectivity; an HTTP 200 alone does not prove results
are available. Engines may be throttled or return CAPTCHAs, reported in
`unresponsive_engines`. Use the dashboard container logs for startup failures.

SearXNG is a separate upstream component under **AGPL-3.0-or-later**; C-Sweet does not
copy its implementation into application projects. Its source for this pinned image
is [revision 81371861f67226091d1c4df2e3908a11d3b54eb9](https://github.com/searxng/searxng/tree/81371861f67226091d1c4df2e3908a11d3b54eb9),
including the [license](https://github.com/searxng/searxng/blob/81371861f67226091d1c4df2e3908a11d3b54eb9/LICENSE).
Preserve its notices and satisfy the applicable corresponding-source requirements
when redistributing its image. An upstream source link alone should not be treated
as a complete binary-redistribution compliance process.

## Docker Compose migration status

Docker Compose remains the intended packaging mechanism for trusted core services, but the checked-in `docker-compose.yml` still contains legacy Docker-agent-runner wiring. It is not currently the supported path for testing the new untrusted-agent architecture.

Known migration blockers include:

- A reference to the removed `docker/agenthost.Dockerfile`.
- A Docker daemon socket mount and container-runtime settings in WorkerHost that belonged to the removed Docker agent runner.
- Legacy agent-container names, networks, images, and cleanup settings.
- No packaged Windows RuntimeHost and certified guest-image onboarding path in the Compose distribution.

Until these are removed, use AppHost for Windows development and end-to-end isolation testing. Documentation and release automation must not advertise `docker compose up` as a complete supported installation command.

## Target containerized core topology

A future supported Compose distribution may package trusted application components such as:

| Component | Purpose | Exposure |
|---|---|---|
| Web app | Browser UI | Public application port |
| API | Authentication and application APIs | Internal, reached through the web entry point |
| Migrator | One-shot database migration and seed job | Internal only |
| WorkerHost | Durable trusted background work | Internal only |
| AgentHost | Unprivileged policy and MCP broker | Internal only |
| PostgreSQL | Durable company state | Internal only |
| SearXNG | Agent research search backend | Internal only, through the broker |

That topology must connect to a separately installed, authenticated RuntimeHost for local untrusted execution. Containerizing AgentHost does not make it an isolation provider.

## Security requirements for future Compose packaging

- Never mount `/var/run/docker.sock` or another container-engine control socket into API, WorkerHost, AgentHost, or an agent workload.
- Never execute untrusted repository-controlled build or runtime operations in an ordinary application container.
- Do not expose PostgreSQL, AgentHost, WorkerHost, or RuntimeHost publicly.
- Do not bake secrets into images or commit real `.env` files.
- Persist data-protection keys and PostgreSQL data in protected volumes.
- Authenticate every RuntimeHost request and keep RuntimeHost's API local and narrowly scoped.
- Fail closed when the certified hardware-isolation provider, signed guest image, or certification evidence is unavailable.

## Troubleshooting

### Docker is installed but AppHost fails immediately

Run `docker info`. If it fails, open Docker Desktop, select Linux containers, and wait for the engine status to report **Running**. Then restart C-Sweet.

### Docker is not installed

Install [Docker Desktop for Windows](https://www.docker.com/products/docker-desktop/), start it once, and then run `Start-CSweet.cmd` again.

### Agent isolation is unavailable while Docker is healthy

This is expected when the separate RuntimeHost/Hyper-V readiness checks have not passed. Continue to the Agent Isolation setup step and use **Prepare secure agent runtime**. Docker health alone never enables untrusted agents.

## Acceptance criteria for a packaged release

Before Docker Compose is documented as a supported distribution again, a clean-machine test must verify that:

- The trusted core starts with documented prerequisites and without legacy agent-container resources.
- Database migrations and first-run administrator setup complete successfully.
- Restarting trusted containers preserves application state.
- No trusted service receives the Docker daemon socket.
- Untrusted execution remains disabled until a separately certified provider is ready.
- A certified Windows RuntimeHost can execute the malicious-fixture test suite without Docker or host-process fallback.
