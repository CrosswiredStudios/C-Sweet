<div align="center">
  <img src="src/CSweet.App/wwwroot/icon-192.png" alt="C-Sweet logo" width="104" />
  <h1>C-Sweet</h1>
  <p><strong>Your idea. Your company. Your workforce.</strong></p>
  <p>
    A self-hostable operating environment for agent-first companies.<br />
    Tell your Chief of Staff what you want to build; C-Sweet helps turn that intent into a company that can plan, staff, execute, and improve.
  </p>
  <p>
    <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&amp;logoColor=white" alt=".NET 10" /></a>
    <a href="https://dotnet.microsoft.com/apps/aspnet/web-apps/blazor"><img src="https://img.shields.io/badge/UI-Blazor-512BD4?logo=blazor&amp;logoColor=white" alt="Blazor" /></a>
    <a href="https://www.postgresql.org/"><img src="https://img.shields.io/badge/PostgreSQL-17-4169E1?logo=postgresql&amp;logoColor=white" alt="PostgreSQL 17" /></a>
    <a href="https://www.docker.com/"><img src="https://img.shields.io/badge/Infrastructure-Docker-2496ED?logo=docker&amp;logoColor=white" alt="Docker infrastructure" /></a>
    <a href="#bring-your-own-models-and-infrastructure"><img src="https://img.shields.io/badge/Cloud-Optional-1E6B52" alt="Cloud optional" /></a>
    <a href="#project-status"><img src="https://img.shields.io/badge/Status-Developer_Preview-E6A84A" alt="Developer preview" /></a>
    <a href="https://github.com/CrosswiredStudios/csweet/stargazers"><img src="https://img.shields.io/github/stars/CrosswiredStudios/csweet?style=flat&amp;logo=github&amp;label=Stars" alt="GitHub stars" /></a>
  </p>
  <p>
    <a href="#a-company-can-start-with-one-person">Why C-Sweet?</a> ·
    <a href="#from-idea-to-outcome">See how it works</a> ·
    <a href="#run-c-sweet">Run it</a> ·
    <a href="docs/00-product-vision.md">Explore the vision</a>
  </p>
</div>

![A founder directing an AI-assisted company from a calm command center](assets/readme/csweet-hero.webp)

## A company can start with one person

Starting a business should not require you to already know how to run every department.

C-Sweet gives a founder an executive layer between an idea and the work required to make it real. You act as the CEO: set the direction, define the boundaries, approve the important decisions, and stay focused on the outcome. Your Personal Assistant or Chief of Staff helps translate that direction into plans, roles, tasks, delegated work, briefings, and deliverables.

That means you can begin with a sentence:

> “Research this market, assemble the team we need, and show me the first plan.”

Then grow deliberately—from one goal, to one team, to a company with durable knowledge and a way of working that belongs to you.

## From idea to outcome

```mermaid
flowchart LR
    A["Your idea"] --> B["Chief of Staff"]
    B --> C["Plan and capabilities"]
    C --> D["AI agents"]
    C --> E["Human experts"]
    C --> F["Hybrid services"]
    D --> G["Tasks and artifacts"]
    E --> G
    F --> G
    G --> H["Review and approval"]
    H --> I["Business outcome"]
    I --> J["Company memory"]
    J --> B
```

C-Sweet is **agent-first, not agent-only**. Routine digital work can go to capable agents; people join where judgment, credentials, relationships, accountability, or physical action matter most. Everyone works inside the same organizational model, with explicit responsibilities and authority.

## What C-Sweet brings to the table

| | Capability | What it means for you |
|---|---|---|
| 💬 | **Executive-first workspace** | Lead through a Personal Assistant instead of managing a wall of disconnected chats. |
| 🧭 | **Command center** | See goals, roles, open work, artifacts, approvals, risks, and recommended next actions in one place. |
| 🧑‍💼 | **One mixed workforce** | Organize local agents, remote services, and people as employees with clear roles and reporting lines. |
| ✅ | **Authority by design** | Decide what can be recommended, drafted, approved, or executed autonomously—by capability and scope. |
| 🧠 | **Company-owned memory** | Keep decisions, work history, conversations, artifacts, and organizational knowledge when models or providers change. |
| 📣 | **Proactive briefings** | Let your Chief of Staff summarize progress and bring the decisions that actually need a CEO. |
| 🧩 | **Extensible platform** | Import agent packages, connect communication providers, and grow capabilities through plugins and provider APIs. |
| 🏠 | **Local-first deployment** | Run the core stack on infrastructure you control and choose local or hosted OpenAI-compatible model endpoints. |

## The CEO experience

1. **Name the outcome.** Start a business, launch a product, research an opportunity, or run an operating function.
2. **Set the rules.** Define budget, risk, privacy, quality, timing, approval, and autonomy boundaries.
3. **Build the workforce.** Assign installed agents today and evolve toward specialist services and human professionals as the platform grows.
4. **Review decisions, not noise.** Receive executive briefings, approve high-impact actions, and inspect work when you choose.
5. **Keep what your company learns.** Plans, artifacts, decisions, and performance history remain part of the company.

The ambition is simple: make entrepreneurship feel less like juggling every job at once and more like leading a capable organization.

## Available in the current prototype

- Guided first-run setup for the root administrator, model providers, optional email, and communications
- Multi-business enterprise view and business onboarding
- CEO command center with objectives, roles, tasks, workers, artifacts, approvals, and next actions
- Unified Communications workspace with durable human and agent conversations, streaming, retry, cancellation, and execution traces
- Agent import, validation, configuration, hardware-isolated runtime management, and memory
- Human and agent employee directory with reporting relationships
- Scheduled and on-demand executive briefings
- Team-scoped work boards with Kanban workflows, sprint planning, estimates, capacity, collaboration, and delivery reports
- Role-driven hiring recommendations with explicit owner review and approval
- Brokered Git workspaces for software-development assignments and reviewable pull-request handoff
- Plugin foundations and communication-provider integrations
- Persistent PostgreSQL state, migrations, health checks, and OpenTelemetry foundations

![C-Sweet sign-in screen for the self-hosted command center](assets/readme/csweet-login.png)

The core can optionally connect to C-Sweet Marketplace for in-app agent browsing and Chief-of-Staff capability matching while continuing to work offline. Marketplace purchase and verified-install handoff remain link-based. See [marketplace discovery integration](docs/MARKETPLACE_INTEGRATION.md), the [product vision](docs/00-product-vision.md), and the [prototype roadmap](docs/09-prototype-roadmap.md).

## Start with a first-party team

C-Sweet's embedded catalog includes five first-party agents that are available to install through the same permission review and hiring workflow used for any other agent.

| Agent | What it enables |
|---|---|
| [**Chief of Staff**](https://github.com/CrosswiredStudios/CSweet.Agent.ChiefOfStaff) | Turns executive direction into priorities, organizational plans, hiring recommendations, coordination, and executive briefings. |
| [**Product Manager**](https://github.com/CrosswiredStudios/CSweet.Agent.ProductManager) | Drives product discovery, strategy, roadmaps, requirements, prioritization, and product-team design. |
| [**Software Architect**](https://github.com/CrosswiredStudios/CSweet.Agent.SoftwareArchitect) | Converts approved requirements into architecture decisions, incremental plans, sprints, and developer-ready tickets. |
| [**Software Developer**](https://github.com/CrosswiredStudios/CSweet.Agent.SoftwareDeveloper) | Implements assigned tickets in an isolated Git workspace, validates changes, and opens reviewable pull requests. |
| [**Software QA**](https://github.com/CrosswiredStudios/CSweet.Agent.SoftwareQA) | Validates the assigned commit against acceptance criteria and returns structured release evidence and a pass, fail, or blocked verdict. |

Direction flows from Chief of Staff through product planning, architecture, implementation, and QA. These agents are not automatically installed: importing their GitHub source requires repository access and explicit owner approval.

## Bring your own models and infrastructure

C-Sweet is provider-neutral by design. The setup flow supports OpenAI-compatible endpoints, so a company can choose the balance that fits its privacy, cost, and capability needs.

- Use a local model server such as LM Studio, Ollama, or vLLM.
- Connect a compatible hosted endpoint when stronger or specialized models are useful.
- Use Docker for trusted application infrastructure such as PostgreSQL while keeping untrusted agent execution behind a separate hardware-virtualization boundary.
- Keep framework-specific agent code behind C-Sweet-owned abstractions.

Local-first does not mean isolated. It means your company can decide when the network adds value.

## Run C-Sweet

### Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) with the Linux container engine running. The Aspire AppHost uses it for PostgreSQL, so C-Sweet cannot start from source while the Docker engine is stopped.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) pinned by [`global.json`](global.json).
- Git.
- An OpenAI-compatible model endpoint; [LM Studio](https://lmstudio.ai/) is the default local preset.
- For local untrusted agents on Windows: Windows Professional, Enterprise, or Education with hardware virtualization. Guided setup handles Hyper-V and RuntimeHost preparation.

> [!IMPORTANT]
> Docker is required infrastructure, but it is **not** the security boundary for untrusted agents. Docker runs trusted dependencies such as the development PostgreSQL database. Untrusted agent code runs only through a certified hardware-isolation provider; on Windows that boundary is a dedicated Hyper-V virtual machine.

### Start from a Windows source checkout

1. Clone the repository.
2. Ensure Docker Desktop is running and reports that its Linux container engine is ready.
3. Double-click [`Start-CSweet.cmd`](Start-CSweet.cmd). The launcher checks .NET and Docker, attempts to start Docker Desktop when necessary, starts Aspire, and opens C-Sweet in the browser.
4. Create the root administrator, save the ten offline recovery codes, and follow guided setup.

Developers can instead run the AppHost from Visual Studio or use:

```powershell
dotnet run --project src/CSweet.AppHost/CSweet.AppHost.csproj --launch-profile https
```

If startup fails before the dashboard is usable, verify Docker first:

```powershell
docker info
```

That command must succeed. Starting the Docker Desktop window is not sufficient; wait for its engine status to show **Running**.

### Docker Compose deployment status

Docker Compose remains the intended packaging path for the trusted C-Sweet core. The checked-in Compose topology is being migrated away from the former Docker-based agent runner and must not be treated as the supported untrusted-agent execution path. For current Windows development and hardware-isolation testing, use the Aspire AppHost flow above.

> [!IMPORTANT]
> A fresh instance trusts its first visitor to claim the root administrator account. Complete registration and onboarding on a trusted network before exposing C-Sweet publicly. SMTP is optional; offline recovery codes are available during registration.

For the current container topology, migration limitations, environment variables, and data persistence, read the [Docker infrastructure guide](docs/deployment/docker.md).

## Architecture at a glance

C-Sweet is a modular .NET application with durable state and isolated agent execution. Docker and Hyper-V have deliberately different jobs:

```mermaid
flowchart TB
    User["User browser"]

    subgraph Host["Trusted Windows host"]
        App["C-Sweet UI"]
        API["API and WorkerHost"]
        AgentHost["AgentHost<br/>policy and MCP broker"]
        BuildBroker["Build broker<br/>source, packages, artifact validation"]
        RuntimeHost["RuntimeHost Windows service<br/>privileged VM lifecycle only"]
        ArtifactStore["Validated content-addressed<br/>agent artifacts"]
    end

    subgraph Docker["Docker Desktop — trusted infrastructure"]
        Postgres[("PostgreSQL")]
    end

    subgraph HyperV["Disposable Hyper-V Generation 2 VMs — untrusted-code boundary"]
        BuilderGuest["Signed builder guest<br/>exact commit + brokered packages"]
        RuntimeGuest["Clean signed runtime guest"]
        Agent["Validated immutable agent artifact"]
        RuntimeGuest --> Agent
    end

    User --> App --> API
    API --> Postgres
    API --> AgentHost
    API --> BuildBroker
    API -->|"authenticated local RPC"| RuntimeHost
    RuntimeHost -->|"create, start, stop, destroy"| BuilderGuest
    RuntimeHost -->|"create, start, stop, destroy"| RuntimeGuest
    BuilderGuest -->|"authenticated Hyper-V socket"| BuildBroker
    BuildBroker -->|"validate and store"| ArtifactStore
    ArtifactStore -->|"read-only artifact media"| RuntimeHost
    RuntimeGuest -->|"authenticated Hyper-V socket"| AgentHost
```

Docker stopping prevents the development stack from starting because PostgreSQL is unavailable. It does not cause C-Sweet to downgrade agent execution into a container or host process. If the certified VM boundary is unavailable, untrusted agent execution remains disabled.

| Component | Responsibility |
|---|---|
| `CSweet.App` + `CSweet.UI` | Blazor web experience and shared UI |
| `CSweet.Api` | Authentication, setup, company operations, chat, planning, and provider APIs |
| `CSweet.WorkerHost` | Durable background work and local agent orchestration |
| `CSweet.AgentHost` | Unprivileged policy enforcement and brokered MCP access; it does not own the VM lifecycle |
| `CSweet.ExecutionGateway` | Headquarters gateway for independently installed C-Sweet Satellite Offices |
| `CSweet.Migrator` | One-shot database migrations and initial seed data |
| PostgreSQL | Company state, history, memory, and operational records |

The repository also contains MAUI host foundations, plugin SDK contracts, unit and integration tests, Docker assets, and detailed architecture plans.

<details>
<summary><strong>Technology stack</strong></summary>

- .NET 10, ASP.NET Core, Blazor WebAssembly, and MudBlazor
- Microsoft Agent Framework and Microsoft.Extensions.AI
- PostgreSQL 17 and Entity Framework Core
- Docker/Aspire for trusted development infrastructure and PostgreSQL
- Certified hardware virtual machines for untrusted agent execution
- OpenTelemetry for observability
- Server-sent events for browser streaming and private Streamable HTTP MCP for SDK-managed agent work

</details>

### Agent runtime documentation

Protocol-v2 executable agents use the transport-neutral SDK over C-Sweet's private, outbound-only MCP runtime and durable work inbox:

- [Security architecture overview](Documentation/Architecture/AGENT_ISOLATION_SECURITY_OVERVIEW.md)
- [Architecture](Documentation/Architecture/MCP_AGENT_RUNTIME.md)
- [Software Developer runtime](Documentation/Architecture/SOFTWARE_DEVELOPER_RUNTIME.md)
- [Threat model](Documentation/Security/AGENT_RUNTIME_THREAT_MODEL.md)
- [Implementation and migration](Documentation/Implementation/MCP_ONLY_AGENT_MIGRATION.md)
- [Operations runbook](Documentation/Operations/MCP_AGENT_RUNTIME_RUNBOOK.md)

## Documentation

| Start here | Go deeper |
|---|---|
| [Product vision](docs/00-product-vision.md) | [Domain model](docs/01-domain-model.md) |
| [Example companies and workflows](docs/12-example-scenarios.md) | [Agent orchestration](docs/02-agent-orchestration.md) |
| [Prototype roadmap](docs/09-prototype-roadmap.md) | [Security, privacy, and trust](docs/07-security-privacy-and-trust.md) |
| [Docker deployment](docs/deployment/docker.md) | [Application architecture](docs/08-application-architecture.md) |
| [Implementation plans](docs/implementation/README.md) | [Budgeting and governance](docs/06-budgeting-and-governance.md) |

The main document index lives in [`docs/README.md`](docs/README.md).

## Build and test

The repository targets the SDK pinned in [`global.json`](global.json).

```bash
dotnet restore CSweet.sln
dotnet build CSweet.sln --no-restore
dotnet test tests/CSweet.UnitTests/CSweet.UnitTests.csproj
dotnet test tests/CSweet.IntegrationTests/CSweet.IntegrationTests.csproj
```

Optional local sibling checkouts of `CSweetAgentSdk` and `CSweet.Memory` are detected automatically by local .NET builds. Without them, local builds and Docker use the centrally pinned package versions.

## Help build the company OS

C-Sweet is for founders, operators, agent builders, designers, and developers who believe powerful tools should make ambition more accessible—not bury it under more software.

Good ways to contribute:

- Try one of the [example business scenarios](docs/12-example-scenarios.md) and report where the experience breaks down.
- Improve an implementation plan or turn one into working code.
- Build an agent or plugin that gives a small company a capability it could not easily afford before.
- Strengthen security, accessibility, observability, testing, and deployment.
- Open an [issue](https://github.com/CrosswiredStudios/csweet/issues) with a focused problem or proposal.

If this is a future you want to help create, [star the repository](https://github.com/CrosswiredStudios/csweet) and build with us.

## Project status

> [!NOTE]
> C-Sweet is an active developer preview. Core workflows are implemented, but the product is not yet production-ready. APIs, deployment requirements, and data models may change. `CSweet` is also a working name pending final brand and trademark review.

---

<div align="center">
  <strong>You bring the ambition. C-Sweet helps you build the company around it.</strong>
  <br /><br />
  <a href="docs/00-product-vision.md">Read the vision</a>
  ·
  <a href="docs/12-example-scenarios.md">Imagine your company</a>
  ·
  <a href="https://github.com/CrosswiredStudios/csweet/issues">Join the conversation</a>
</div>
