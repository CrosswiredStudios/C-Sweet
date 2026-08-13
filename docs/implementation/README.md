# C-Sweet Implementation Plans

> **Historical planning set:** Several phase documents in this folder describe the original
> Docker-first distribution and Docker agent-runner design. They remain useful as implementation
> history, but they are not current setup or security instructions. Use the repository
> [README](../../README.md), the [Docker infrastructure guide](../deployment/docker.md), and the
> [agent isolation security overview](../../Documentation/Architecture/AGENT_ISOLATION_SECURITY_OVERVIEW.md)
> for the current architecture. Docker is still required for trusted development infrastructure;
> it is no longer an untrusted-agent isolation mechanism.

Last updated: 2026-07-13

## Purpose

This folder breaks the proposed C-Sweet implementation into phased plans that can be handed to junior developers. The project vision is an open-source, self-hostable application that lets a user model a business, define executive-style goals, assign work to AI or human workers, review artifacts, and eventually connect to a live marketplace of third-party workers.

The first production goal is not the marketplace. The first production goal is a reliable local vertical slice:

> A user launches C-Sweet through Docker or the developer AppHost, completes first-run system setup, connects to a local or hosted LLM provider, creates their first business, runs a first agent-generated task, and approves the resulting artifact.

## Document map

Read these files in order:

1. [00 Architecture Baseline](./00-architecture-baseline.md)
2. [01A Phase 1A - Docker Containerization and Distribution](./phases/01a-docker-containerization-and-distribution.md)
3. [05 Phase 5 - Microsoft Agent Framework Integration](./phases/05-microsoft-agent-framework-integration.md)
4. [06 Phase 6 - Core Business Domain](./phases/06-core-business-domain.md)
5. [07 Phase 7 - Business Onboarding](./phases/07-business-onboarding.md)
6. [08 Phase 8 - First Agent Workflow](./phases/08-first-agent-workflow.md)
7. [09 Phase 9 - Worker Runtime and Worker Contract](./phases/09-worker-runtime-and-worker-contract.md)
8. [09A Phase 9A - Agent Broker and Personal Assistant](./phases/09a-agent-broker-and-personal-assistant.md)
9. [10 Phase 10 - Observability, Security, and Operations](./phases/10-observability-security-operations.md)
10. [11 Marketplace Readiness](./11-marketplace-readiness.md)
11. [12 Junior Developer Task Checklist](./12-junior-developer-task-checklist.md)

## Feature plans

Cross-cutting features are documented as a phased series in their own folder:

- [Native Communications Hub](./native-communications-hub.md) - the authoritative in-app
  workspace for human and agent conversations, durable agent turns, unread state, realtime
  updates, and optional external-provider mirrors.
- [GitHub Agent Import](./features/github-agent-import/README.md) - import public GitHub-hosted
  agent packages by URL, review the root manifest, approve bounded grants, and run community
  agents inside broker-governed sandboxes.
- [Agent Runtime Manager](./features/agent-runtime-manager/README.md) - run always-on and
  scheduled ephemeral agents, manage global container limits, build imported .NET agents, and
  spin containers up/down around broker-reported completion.
- [Satellite Office assisted installation](./features/satellite-office-assisted-installation.md) -
  provide a guided signed-package installation and one-use local enrollment handoff for **This
  machine**, while retaining portable instructions for **Another machine**.

## Recommended implementation order

Do not start by building the marketplace or a complex autonomous company brain. Build in this order:

1. Solution structure and local dev orchestration.
2. Docker containerization and one-command self-hosted startup.
3. Database and first-run configuration state.
4. LLM provider profiles and connection tests.
5. First-run setup wizard.
6. Minimal Microsoft Agent Framework adapter.
7. Core organization, role, task, run, artifact, and approval entities.
8. Business onboarding.
9. One useful agent workflow: generate a 30-day operating plan.
10. Worker runtime and worker contract.
11. Brokered Personal Assistant agent vertical slice.
12. Observability, security, and deployment hardening.
13. Marketplace integration points.

## Core architectural assumptions

- C-Sweet is self-hostable first.
- Docker Compose is the default distribution path for people who want to run the platform without installing the .NET SDK.
- Aspire AppHost is still used for the developer inner loop.
- The default first provider is LM Studio running on the host machine, with Docker guidance for `host.docker.internal`.
- Provider configuration must be flexible enough to support local and hosted OpenAI-compatible endpoints, Azure OpenAI, OpenAI, Ollama, Anthropic, Microsoft Foundry, and later custom providers.
- Enterprise-ready .NET libraries are preferred over custom infrastructure.
- Microsoft.Extensions.AI should be used as the normal LLM abstraction layer.
- Microsoft Agent Framework should be used where agent behavior, multi-agent workflows, tool use, human-in-the-loop, or workflow orchestration adds value.
- Normal deterministic code should still handle CRUD, validation, security, persistence, billing, and state transitions.

## Reference docs

Use these official references when implementing:

- Microsoft Agent Framework overview: https://learn.microsoft.com/en-us/agent-framework/overview/
- Microsoft Agent Framework workflows: https://learn.microsoft.com/en-us/agent-framework/workflows/
- Microsoft Agent Framework human-in-the-loop workflows: https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop
- Microsoft.Extensions.AI: https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai
- Microsoft.Extensions.AI chat quickstart: https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/build-chat-app
- .NET Aspire overview: https://learn.microsoft.com/en-ca/dotnet/aspire/get-started/aspire-overview
- .NET Aspire AppHost: https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/app-host-overview
- ASP.NET Core Blazor Hybrid: https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/
- Build a .NET MAUI Blazor Hybrid app: https://learn.microsoft.com/en-us/aspnet/core/blazor/hybrid/tutorials/maui
- Razor class libraries for shared Blazor components: https://learn.microsoft.com/en-us/aspnet/core/blazor/components/class-libraries
- EF Core overview: https://learn.microsoft.com/en-us/ef/core/
- EF Core DbContext configuration: https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/
- EF Core migrations: https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying
- .NET Docker containerization: https://learn.microsoft.com/en-us/dotnet/core/docker/build-container
- Docker Compose overview: https://docs.docker.com/compose/
- LM Studio OpenAI compatibility: https://lmstudio.ai/docs/developer/openai-compat
- LM Studio local server: https://lmstudio.ai/docs/developer/core/server
- LM Studio chat completions: https://lmstudio.ai/docs/developer/openai-compat/chat-completions
- LM Studio embeddings: https://lmstudio.ai/docs/developer/openai-compat/embeddings
- LM Studio tool use: https://lmstudio.ai/docs/developer/openai-compat/tools

## Definition of done for the full first vertical slice

The first full vertical slice is complete when:

- `docker compose up -d` starts the platform from a clean checkout.
- A fresh database starts with `IsFirstRunComplete = false`.
- The UI redirects to `/setup` until setup is complete.
- The user can configure LM Studio using `http://host.docker.internal:1234/v1` from Docker or `http://localhost:1234/v1` from direct local development.
- The system can list or accept a model ID.
- The system can run a chat completion test.
- The system can store provider capability results.
- The user can create the first admin account or local owner profile.
- The user can create the first business.
- The system creates default executive roles and an initial strategic objective.
- The user can run “Generate 30-day operating plan.”
- A task run is persisted with logs and status changes.
- An artifact is created and shown in the UI.
- The user can approve or reject the artifact.
- Errors are visible in logs and task run history.
