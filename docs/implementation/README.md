# C-Sweet Implementation Plans

> **Historical planning set:** Several phase documents in this folder describe the original
> Docker-first distribution and Docker agent-runner design. They remain useful as implementation
> history, but they are not current setup or security instructions. Use the repository
> [README](../../README.md), the [Docker infrastructure guide](../deployment/docker.md), and the
> [agent isolation security overview](../../Documentation/Architecture/AGENT_ISOLATION_SECURITY_OVERVIEW.md)
> for the current architecture. Docker is still required for trusted development infrastructure;
> it is no longer an untrusted-agent isolation mechanism.

Last updated: 2026-10-08

## Purpose

This folder breaks the proposed C-Sweet implementation into phased plans that can be handed to junior developers. The project vision is an open-source, self-hostable application that lets a user model a business, define executive-style goals, assign work to AI or human workers, review artifacts, and eventually connect to a live marketplace of third-party workers.

The first production goal is not the marketplace. The first production goal is a reliable local vertical slice:

> A user launches C-Sweet through Docker or the developer AppHost, completes first-run system setup, connects to a local or hosted LLM provider, creates their first business, runs a first agent-generated task, and approves the resulting artifact.

## Document map

[Assignment-scoped QA Git history](internal-git-hosting.md#assignment-scoped-qa-history) maps the
read-only inspection boundary, conventional generated subjects, release rollout and remaining UI gaps.

[Browser testing compute](browser-testing-compute.md) maps the reusable offline Playwright/Chromium
workload, exact-build QA binding, durable completion recovery, retained evidence and rollout.

[OpenHands developer pilot](openhands-developer-pilot.md) maps the independent execution adapter,
SDK 5.1.0 research/checkpoints, protected storage, verification and opt-in rollout.

[Progressive game planning](progressive-game-planning.md) describes complete scope outlines,
bounded increment refinement, checkpoint recovery, independent delivery acceptance and the
verified Sweet Sweet Games rollout.

[Delegation with tracking](features/delegation-with-tracking.md) describes instruction publication,
canonical work, coordinator reporting, subscriptions, recovery and the future continuity boundary.

Current execution: [single execution contract](features/work-management/canonical-execution-contract.md) documents the breaking cutover, regression coverage and remaining live-run prerequisites.

Current delivery: [hierarchical orchestration and release integration](features/work-management/hierarchical-delivery.md) documents canonical assignments, scope activation, branch bindings, QA, aggregate acceptance, recovery and clean installation.

Read these files in order:

Current onboarding: [Personal Assistant and repository business onboarding](features/personal-assistant-repository-onboarding.md) documents the Enterprise dialog, standalone SDK agent, per-user settings/chat, authorized sources, reviewed briefs, and creation recovery.

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

- [Project names and continuity](features/project-naming.md) - human rename controls, approved agent changes, stable execution and memory identity, audit history and current chat context.

- [Forecast-driven planning](features/forecast-driven-planning.md) - optional external date constraints, elapsed-history completion ranges, intake memory and manager-owned pause/escalation/relay.

- [Business deletion](features/business-deletion.md) - durable deletion intent, automatic
  completion after compute teardown, tile progress and failure recovery.

- [Compute page and Linux preparation](compute-page.md) - Linux-only compute types, owner instancing, preparation milestones, process-health evidence, retry isolation and completion collapse.

- [Chief hiring autonomy](features/chief-hiring-autonomy.md) - owner policy, conversational setup, approved-plan delegation, shared hire review, and marketplace attribution.

- [Project approval review](features/project-approval-review.md) - human-readable project proposals, inline Communications approval tools, exact decision binding and history.

- [Memory System Implementation](./features/memory-system-implementation.md) - **current remaining-work plan**. Starts from the implemented runtime recovery, correction integrity and shared-correction writers; identifies repository/package alignment, then defines 21 remaining work packages across correctness/lifecycle/release acceptance, unified context/reconciliation/operator controls, and evaluated delivery/semantic/summary features. Use its register, dependencies and completion criteria for new work.
- [Employee Memory workspace](./features/employee-memory-workspace.md) - embedded Learning Journal, browsing and graph views, preserved review/privacy/recovery tools, and bounded activity aggregation.
- [Agent memory hardening](./features/agent-memory-hardening.md) - historical assessment and implementation evidence. Superseded as the active backlog by **Memory System Implementation**; older checkpoints and handoff instructions may describe behavior or versions since changed.
- [Uniform provider responses](./features/provider-response-compatibility.md) - shared
  agent response contract, bounded formatting recovery, streaming fallback and diagnostics.
- [Agent upgrades and configuration readiness](./features/agent-upgrade-configuration.md) -
  provider-independent package upgrades, retained settings, and local readiness checks.
- [Producer ↔ Creative Director handoff stall](./producer-director-handoff-stall.md) - incident diagnosis, recovery behavior and the next-step/escalation convention for waiting agents. Current remaining acceptance is tracked in Memory System Implementation.
- [October 13 Breakout startup stall](./breakout-startup-stall-2026-10-13.md) - fresh-install Office image mismatch, OnDemand retry churn, V1/V2 staffing mismatch, misleading recovery alerts, and next-run acceptance gates.
- [October 16 Pulse Break planning stall](./pulse-break-planning-stall-2026-10-16.md) - incomplete planning responses, durable planning pages, verified retry reporting, bounded escalation and local recovery.
- [October 17 planning output recovery](./planning-output-recovery-2026-10-17.md) - 128,000-character responses/checkpoints, paginated outlines and coverage, accurate technical classification, manager-to-CEO escalation and existing-report correction.
- [October 17 saved-outline handoff recovery](./planning-digest-handoff-recovery-2026-10-17.md) - exact digest compatibility, terminal retry suppression, valid informational reports and reuse of the completed outline.
- [October 19 QwenGames planning stall](./planning-stall-overview-2026-10-19.md) - oversized coverage output, task-budget mismatch, persistent Overview issue cards and proposed resumable planner hardening.
- [October 17 workspace sync schema recovery](./workspace-sync-schema-recovery-2026-10-17.md) - SDK/gateway request compatibility for source download, checkpoint upload and resource recovery after sprint activation.
- [October 17 developer validation recovery](./developer-validation-recovery-2026-10-17.md) - quoted validation commands, real assertion exit codes, bounded completion correction and paired developer releases.
- [Blocked tickets and retry](./blocked-ticket-retry.md) - where blocked sprint cards go, and how moving them back to Ready or pressing Retry restarts them.
- [Manager project health](./features/producer-project-health.md) - stall detection, bounded diagnostics,
  durable incidents, management escalation and rollout.
- [Adrian CI timeout](./adrian-ci-timeout-2026-10-16.md) - retained evidence, bounded command cleanup,
  runtime fencing, checkpoint recovery and informational CEO escalation.
- [Prism Break runtime build interruption](./prism-break-runtime-build-2026-10-16.md) -
  VGAEBFF008A8-33 evidence, guest reproduction and the retained workflow improvement register.

Cross-cutting features are documented as a phased series in their own folder:

- [Work efficiency and business benchmarks](./features/work-efficiency-and-benchmarks.md) -
  inference accounting, historical work rollups, isolated business trials, and quality evidence.
- [Native Communications Hub](./native-communications-hub.md) - the authoritative in-app
  workspace for human and agent conversations, durable agent turns, unread state, realtime
  updates, and optional external-provider mirrors.
- [GitHub Agent Import](./features/github-agent-import/README.md) - import public GitHub-hosted
  agent packages by URL, review the root manifest, approve bounded grants, and run community
  agents inside broker-governed sandboxes.
- [Agent Runtime Manager](./features/agent-runtime-manager/README.md) - run always-on and
  scheduled ephemeral agents, manage global container limits, build imported .NET agents, and
  spin containers up/down around broker-reported completion.
- [Office assisted installation](./features/office-assisted-installation.md) -
  provide a guided signed-package installation and one-use local enrollment handoff for **This
  machine**, while retaining portable instructions for **Another machine**.
- [Game production reliability](./features/game-production-reliability/README.md) - add
  deterministic game-production evaluations, criterion coverage, persistent review remediation,
  generated effective assignment contracts, curated playbooks, and safe proactive programs.
- [Producer vision-to-delivery review](./features/producer-vision-to-delivery-review.md) -
  observed Breakout FX Demo failures, disconnected kickoff and planning paths, and the required
  project/team setup, technical planning, and multi-sprint acceptance scenarios.
- [Collaborative documents](./features/collaborative-documents.md) - keep document creation,
  revisions, access, review, chat links, and agent capabilities on one canonical artifact model.
- [Work item comments](./features/work-item-comments.md) - make ticket discussion author-scoped,
  editable, and retractable on both team boards and personal boards.
- [Universal work management](./features/work-management/README.md) - canonical work items,
  board directory, sprints, personal boards, orchestration, and the symbol-first code map.
- [Hiring suggestion lifecycle](./features/hiring-suggestion-lifecycle.md) - one actionable hiring
  suggestion per conversation, with created/completed/cancelled/superseded states, withdraw cascade,
  and owner-directed replacement semantics.
- [Employee audit timeline](./features/employee-audit-timeline.md) - employee-scoped replay of the shared audit ledger, full diagnostic evidence, permissions, and historical coverage.
- [Current Activity](./features/current-activity.md) - Overview work and executor rows, task-scoped reasoning/action feeds, permissions, and realtime recovery.
- [Chat turn diagnostics](./chat-turn-diagnostics.md) - use the persisted error code, turn trace,
  agent work-item error, and console locations to diagnose a failed Communications answer.

- [Repository directory](./features/repository-directory.md) - repository activity, project associations, C-Sweet access semantics, and click-only access popovers.
- [GitHub connection wizard](./features/github-connection-wizard.md) - compact platform setup, automatic callback completion, business repository selection, and optional creation setup.

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

- [Windows local reset and disk reclamation](local-reset.md) — preview and reset default
  local runtime services, Hyper-V storage, generated builds and optional user/database data.

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

- [Project-required development](features/project-required-development.md) — SDK prerequisite policy, human setup, durable intake, Chief handoff, repository binding and rollout.
- [Project compute scope](features/project-compute-scope.md) — shared host preparation, existing project testing permissions, durable wakes and generated workspace cleanup.

- [Runtime public internet and dependency setup](features/runtime-public-internet.md) — reviewed execution permission, common Office egress, certified development guests and rollout gates.

- [Coding workload resources and recovery](coding-workload-resources.md) — resource policy, checkpointed VM replacement, guest memory evidence and rollout acceptance.
