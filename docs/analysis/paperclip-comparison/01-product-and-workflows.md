# Product and workflow comparison

[Assessment overview](README.md) · [Architecture](02-architecture-and-reliability.md) · [Improvement plan](03-improvement-plan.md) · [Evidence](04-evidence-and-reuse.md)

Paperclip and C-Sweet both organize businesses, agents, goals, work, and human oversight. The distinction is emphasis: Paperclip makes heterogeneous agent runtimes and their daily operation especially explicit; C-Sweet emphasizes an executive operating layer and specialized work within a tightly governed environment. This is an assessment of the inspected product structures, not a claim about user preference or adoption.

## Capability comparison

“Not found” means no equivalent was established in the inspected code and targeted searches. It does not prove repository-wide absence. Source IDs link to the [source register](04-evidence-and-reuse.md#source-register).

| Capability | Paperclip evidence | C-Sweet evidence | Comparative judgment |
|---|---|---|---|
| Company and goal model | Companies, goals, projects, issue ancestry, agents, and org chart. P01, P04 | Organizations, objectives, workstreams/projects, employee reporting lines, and work items. C01, C03 | Strong overlap; a company metaphor is not a differentiator by itself. |
| Executive guidance | CEO/manager role configuration, instructions, managed agents, and team catalog. P02, P06 | Chief operating profiles, leadership coverage, hiring backlog, and delegated manager behavior. C08 | C-Sweet has more explicit domain-specific executive behavior in the inspected first-party agents. Its value still needs outcome testing. |
| Agent onboarding | Runtime adapters, connection configuration, instruction files, and skill assignments. P01, P02 | Source package import, manifest preview, installed revision grants, employee identity, onboarding conversations, and Office readiness. C01, C05, C08 | Paperclip favors ecosystem access; C-Sweet favors explicit platform contracts. |
| Agent languages and runtimes | Twelve adapter directories in the snapshot, plus generic/server and native-runner paths; availability varies by qualification. P02 | Supported authoring API is .NET 10; Python support is explicitly unavailable in the SDK's Python guide. C08 | Clear Paperclip breadth advantage; directory count is not a tested support matrix. |
| Human collaborators | Agent and user assignments, company access, reviews, resolver policy. P04, P05 | Human/agent employee types, managers/owners, team grants, personal boards and approvals. C03, C05 | Both support mixed supervision. Neither was evaluated for enterprise identity or compliance completeness. |
| Work organization | Issues, parents, dependencies, projects, execution policy and work products. P04, P10 | One canonical work item, boards, sprints, capacity, estimates, dependencies, orchestration stages. C03 | C-Sweet is particularly explicit about structured production planning; Paperclip offers direct task execution and review. |
| Reviews and approvals | Execution policy routes completion through reviewers/approvers and records decisions; documented stages currently need one approval. P04 | Artifact revisions, work-item approvals, orchestration policy, exact-revision collaboration and source-control review. C03, C05, C09 | C-Sweet does not need a second review engine. Both need evidence that configured gates cover actual execution paths. |
| Operator attention | Unified attention sources, resolver detail, snooze/dismiss, filters, keyboard actions, decision history. P05 | Approval dashboard, overview widgets, Current Activity, Communications and employee audit timeline. C05, C06 | Paperclip provides the stronger source-level model for a single actionable queue. |
| Task and conversational context | Task threads, agent conversations, external chat channels and native-runner continuity. P03, P04, P13 | Canonical native Communications, protected direct conversations, trace streams, unread sequences, retry/cancel and artifact links. C05 | C-Sweet's native communications model is a strength, but chat is not exclusive to it. |
| Integrations | Slack, Teams, Telegram, Discord, GitHub chat services and governed apps/MCP gateway source. P13 | Plugin capability platform, Discord mirror path, YouTube and toolchain repositories, configurable generation providers. C01, C05 | Paperclip shows broader general-purpose connector coverage. End-to-end connector quality was not tested. |
| Recurring work | Routines with revisions, schedule/webhook triggers, timezone, concurrency/catch-up policies, and run records. P07 | Agent schedules, attention review events, management reviews, briefings and calendar. C06 | Existing C-Sweet schedules are useful foundations; a user-defined routine product is a distinct addition. |
| Failure recovery | Watchdogs, recovery actions, liveness, bounded retry and native safe replacement. P03, P10 | Durable work leases, dispatch recovery, chat retry, execution timing recovery; manager incident escalation in local changes. C03, C06, C10 | Shared problem space. Integrate C-Sweet's work in progress rather than proposing a competing watchdog. |
| Costs and budgets | Ledger, billing categories, company/agent/project policies, incidents and invocation blocks. P08 | Budgets/reservations, provider limits, usage receipts, effort and token analytics. C04 | Paperclip has a clearer inspected spend-to-stop product loop. C-Sweet has detailed attribution worth retaining. |
| Memory and knowledge | Persistent sessions/instructions, company skills, Cognee bridge and alpha LLM Wiki plugin. P02, P09 | Scoped recall/capture and temporal memory framework with provenance and knowledge transfer. C07 | C-Sweet has a more integrated institutional-memory architecture in the sampled evidence. Recall quality was not benchmarked. |
| Skill improvement | Skill Studio, drafts/forks, test-input/run surfaces, catalog and organization skill management. P09 | Typed package behavior, memory procedures, configuration and benchmarks; curated playbook product appears in a phased plan. C07, C08, C11 | Borrow the inspectable author/test/promote workflow; reuse C-Sweet memory and artifact foundations. |
| Company templates | Import/export previews, collision strategies, selected resources, required environment inputs and hashes. P06 | Agent packages, operating profiles, benchmark blueprints; general company portability not found. C08, C11 | One of Paperclip's clearest reusable product advantages. |
| Source control | Workspace realization, branch ownership, GitHub merge and workspace-diff plugin. P10 | Brokered workspaces, managed GitHub, internal Git/LFS, proposed changes, exact-head review and merge receipts. C09 | C-Sweet's internal hosting is a useful local-control option, with real operational and transport limits. |
| Documents and output | Documents/revisions, attachments, work products and artifact surfaces. P10 | Canonical Artifact/ArtifactRevision documents with permissions, stewardship, review, origins and packages. C09 | Strong overlap. The opportunity is a consistent completion evidence view. |
| Deployment and execution | Embedded DB onboarding, authenticated/local modes, remote sandbox providers and native runner. P01, P03, P12 | Headquarters services, independently installed Offices, hardware certification, signed assignments. C01, C02 | Paperclip offers more deployment choices; C-Sweet has a more uniform restrictive execution policy with higher setup cost. |
| Automated release evidence | PR checks, Storybook visual regression, browser/release smoke and runner chaos/evals workflows. P11 | Unit/integration suites, SQL recovery tests and package checks; main Actions workflow narrowly targets Windows distribution. C12 | Prioritize enforcement and full-workflow verification, not a wholesale testing-framework rewrite. |

## First use and activation

Paperclip's quickstart recommends `npx paperclipai onboard --yes`. Its CLI includes checks for database, authentication mode, secrets, storage, ports, service health, and model setup. The developer flow also starts with an embedded PostgreSQL option. “Under five minutes” is an upstream documentation claim; this assessment did not time it. Adapter authentication, provider availability, sandbox setup, and the first successful task can add work beyond installing the app. [P01](04-evidence-and-reuse.md#source-register)

C-Sweet's README specifies Windows, .NET 10, Docker Desktop for trusted infrastructure, and a separate Office for untrusted execution. The current assisted-installation work already includes release-first bundles, preflight, one-use enrollment, progress reporting and stale-installation recovery. Therefore, recommending an installer as if none exists would be misleading. The remaining product opportunity is **a coherent path through those prerequisites with a visible successful execution probe**. The docs explicitly retain unfinished end-to-end and non-Windows validation work. [C01, C02](04-evidence-and-reuse.md#source-register)

Proposed C-Sweet flow: create the owner → verify the model → explain and prepare Office → approve its fingerprint → verify an installed agent's grants → run a bounded sample → show its artifact and consumption. The user should see the exact blocker and recovery action at each stage. An offline Office must remain a visible readiness failure; setup convenience must not quietly select host execution.

## Daily supervision and navigation

Paperclip's `attention.ts` assembles distinct source kinds: approvals, decisions, thread interactions, join requests, recovery actions, productivity reviews, blockers, reviews, failed runs, budget alerts and agent-error alerts. `WhatNeedsMe.tsx` supports filters, groups, history, snooze/dismiss and keyboard selection. These source and UI structures directly support a useful question: what needs this human's action now? [P05](04-evidence-and-reuse.md#source-register)

C-Sweet's `ApprovalDashboardService.GetAsync` already resolves active employee identity and checks manager/owner authority, then aggregates approval types. `CommandCenter.razor` includes decisions/company health, configurable widgets and approval summaries. The navigation includes Approvals and Communications badges. The improvement is to add failures and blockers to a consistent action projection, with current authorized resolvers and causally related evidence. It is not necessary to replace the overview or flatten every notification into a single undifferentiated list. [C05, C06](04-evidence-and-reuse.md#source-register)

For example, “Producer has not completed planning” should resolve to “Project cannot proceed because the approved agent revision lacks capability X,” when that fact is established. Display the affected task, attempt, grant revision, responsible owner and authorized repair. If only a timeout is known, say that. Do not invent a root cause from a stale runtime log.

The inspected Paperclip dashboard screenshot illustrates a compact sidebar and visible agents, work, spend and approvals. It is a historical repository screenshot, not a current UI measurement. Neither application's accessibility, responsiveness, or interaction latency was tested. Claims that one UI is “more polished” require browser testing; the stronger current claim is that Paperclip has more explicit operator workflow and visual-regression assets. [P05, P11, P16](04-evidence-and-reuse.md#source-register)

## Two delivery scenarios

These are source-informed walkthroughs, not executed benchmark results.

### A founder asks for a small software feature

In Paperclip, the user can configure an existing coding runtime, create/assign an issue, bind a workspace, and attach review policy. The server handles checkout and runtime dispatch; completion can be intercepted and routed to a reviewer. Work products, session context, and reported cost are available through its existing services. The likely advantage is reaching familiar coding tools quickly. The risk is assuming every adapter/environment combination offers identical cancellation, telemetry, permissions, or recovery. The native-runner qualification documentation itself distinguishes candidates from production-ready paths. [P02, P03, P04, P10](04-evidence-and-reuse.md#source-register)

In C-Sweet, the founder can work through the Chief or project manager, establish the project and team, assign canonical board work, execute through Office, publish a revision, and review source-control/artifact evidence. This connects the feature to wider company state, but requires more platform-specific readiness. The winning improvement is a minimal approved team and a visible path to first accepted change; additional hiring and planning should be justified by the task. [C03, C08, C09](04-evidence-and-reuse.md#source-register)

**Proposed comparison:** Use the same repository fixture and acceptance test. Count setup interventions, decisions requested, elapsed delivery time, failed attempts, reported usage coverage, and final test outcomes. A successful provider process is not an accepted feature.

### A founder asks for a playable game prototype

C-Sweet has a concrete specialist catalog and Producer behavior for planning, estimates, sprint readiness, staffing and delivery. Its existing reliability plan recognizes gaps around stable criteria, persistent findings, generated assignment context, and golden-path evaluation. That makes a small game prototype a useful differentiation test, but also exposes coordination overhead: many installed roles can create more dependencies than a small prototype needs. [C08, C11](04-evidence-and-reuse.md#source-register)

Paperclip can model a game team, connect tool-capable workers and use goals, tasks, skills and reviews. This review did not establish an equivalent bundled game-production operating contract. That is an opportunity for C-Sweet, not proof Paperclip cannot produce games. A reusable C-Sweet studio template should carry only the necessary roles, toolchains, review policy and sample criteria for the chosen prototype.

**Proposed comparison:** A tiny playable scene with a deterministic build, controls, a win/lose condition, performance constraints and visual/audio evidence. Verify the artifact independently, inject one failed assignment, and require a traceable revision cycle. Score whether the result works before judging presentation.

## Strengths that also create weaknesses

| Design choice | Benefit | Cost or risk |
|---|---|---|
| Paperclip runtime breadth | Reuses agents and workflows users already know. | More provider-specific semantics, credentials, compatibility and recovery paths to qualify. |
| Paperclip issue-centered operation | A clear place to assign work, discuss it and review completion. | Issue services accumulate many concerns; organization knowledge and broader business procedures need additional surfaces. |
| Paperclip portable companies | Faster reuse and community distribution. | Portability must manage environment dependencies, confidential content, imports and authority separately. Scrubbed env values do not prove every document is safe to share. |
| C-Sweet typed platform and Office boundary | Consistent authority and execution constraints. | Higher authoring and installation friction; arbitrary external agents cannot simply be launched unchanged. |
| C-Sweet specialized roles and formal planning | Clear accountability and domain-specific delivery contracts. | More setup, handoffs and ways for a team to stall; small tasks need proportional process. |
| C-Sweet integrated memory | Knowledge can follow company and employee relationships. | Retrieval, provenance, privacy, enrichment cost and stale claims require ongoing evaluation. |
| C-Sweet distributed repositories | Independently versioned contracts, agents and execution services. | Local sibling references can hide release problems; compatibility needs package-only validation. |

These are engineering/product tradeoffs inferred from the inspected structures. No overall numerical score is assigned because deployment, interaction quality, operating cost and real delivery success were not measured.

## The opportunity worth pursuing

C-Sweet can compete through **a repeatable, supervised path from business intent to accepted work**, with clear authority and useful company memory. Paperclip already covers much of the general agent-company vocabulary. The practical advantage will come from fewer unexplained stalls, shorter setup, better decisions and higher-quality deliverables. The next document explains which technical mechanisms support that outcome.
