# Evidence limitations and reuse guidance

[Assessment overview](README.md) · [Product comparison](01-product-and-workflows.md) · [Architecture](02-architecture-and-reliability.md) · [Improvement plan](03-improvement-plan.md)

This register makes the assessment reproducible and separates source inspection from demonstrated runtime behavior. Paperclip links are pinned to the inspected commit. C-Sweet links point to the local documentation/source tree, which includes existing local changes.

## Repository snapshots

Inspected on **September 29, 2026, America/Los_Angeles**.

| Repository | HEAD at inspection | Working-tree scope |
|---|---|---|
| Paperclip | `0b12ca9532a006fe3849b1efe0a52bac2f301285` | Shallow clone of public default branch; latest commit timestamp `2026-09-29T16:38:02-07:00`, “fix(server): retain context for unconfirmed adapter stops (#14639)”. |
| C-Sweet | `fb4e94954a8cbea67c1222353a75579a52ec40a3` | 28 pre-existing changed/untracked status entries before report creation; local project-health work included. |
| CSweet.Agent.Sdk | `1211b7bc1e1fe3724e67ee2b71a76f171d166f4f` | Clean at inspection; authoring/operating-contract documentation sampled. |
| CSweet.Memory | `f27c480a38a5f1ae1774b82af3799c832dfd5c0c` | Clean; framework documentation sampled alongside actual main-app integration. |
| CSweet.Office | `0c3facb4c4c11f75fdd3ee96940a611636fe7e65` | Clean; execution documentation and RuntimeHost provider/authorization wiring sampled. |
| CSweet.Isolation | `b0e12a31c77d49489bc1d9da6f3da2734fc3a6a8` | Clean; shared primitives and scope documentation sampled. |
| CSweet.Agent.ChiefOfStaff | `9570aa4dc9270573c0859091ec974b47305edd4c` | Clean; documented runtime behavior sampled. |
| CSweet.Agent.Producer.VideoGame | `d6c3ad4ce1c055830b2a998af3226d346cf09389` | Clean; documented delivery/role behavior sampled. |

Paperclip's inspected server package manifest says `0.3.1`; that is a source package value, **not a verified latest published release**. C-Sweet's local Agent SDK checkout is named `CSweet.Agent.Sdk`; the older `CSweetAgentSdk` sibling path was absent on this machine. Future implementation should resolve the actual configured path before following historical references.

The Paperclip clone is in the ignored `.tmp/paperclip-analysis` research directory. It was not installed or executed, and its repository instructions were treated as source material rather than authority over this assessment. No external company, account, issue, message or deployment was created.

## Evidence levels

- **Implementation evidence:** a route, service, entity, UI binding or contract was read or searched to establish the stated mechanism. This does not prove runtime correctness.
- **Documented behavior:** upstream/project documentation describes the behavior. Where only documentation was sampled, the report avoids claiming a tested implementation.
- **Local work in progress:** implementation exists in the supplied working tree but is not wholly represented by the main HEAD. Project-health incidents/escalation fall into this category.
- **Inference or proposal:** comparative judgment, risk, priority, effort estimate or suggested design derived from evidence. It is not a measured result.

The inspection began with C-Sweet's implementation index and current README. Feature plans were not treated as availability guarantees. Searches then traced selected domains into source and tests. Large service files were sampled around relevant methods; this was not a line-by-line audit of either repository.

## Source register

### Paperclip sources

All links below use the exact inspected commit.

| ID | Primary sources and symbols | What they support |
|---|---|---|
| P01 | [Quickstart](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/docs/start/quickstart.md), [CLI checks](https://github.com/paperclipai/paperclip/tree/0b12ca9532a006fe3849b1efe0a52bac2f301285/cli/src/checks), [server manifest](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/package.json), [architecture guide](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/docs/start/architecture.md) | npm onboarding, diagnostics inventory, Express/PostgreSQL stack and documentation drift. Quickstart timing is claimed, not measured. |
| P02 | [Adapter packages](https://github.com/paperclipai/paperclip/tree/0b12ca9532a006fe3849b1efe0a52bac2f301285/packages/adapters), [adapter types](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/packages/adapter-utils/src/types.ts), `AdapterExecutionResult`, `AdapterRuntime`, `UsageSummary` | Runtime breadth, session metadata, usage basis, billing types, normalized errors and positive recovery evidence. Directory presence alone does not prove qualification. |
| P03 | [Native runner](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/packages/paperclip-runner/README.md), [heartbeat service](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services/heartbeat.ts), [durable chat wakeup](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services/durable-chat-wakeup.ts), [native safe replacement](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services/native-runtime/native-safe-replacement.ts) | Rust runner boundary, conformance, runtime policy, durable retry authority and safe replacement. Provider candidate restrictions qualify breadth claims. |
| P04 | [Issues](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services/issues.ts), `issueService.checkout`; [execution policy guide](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/docs/guides/execution-policy.md), [policy tests](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/__tests__/issue-execution-policy.test.ts) | Conditional checkout, expected-state ownership and documented reviewer/approver routing, changes requested and bounded missing-comment retry. |
| P05 | [Attention service](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services/attention.ts), `ATTENTION_SOURCE_KINDS`; [WhatNeedsMe](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/ui/src/pages/WhatNeedsMe.tsx), [UI routes](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/ui/src/App.tsx) | Unified human action sources, filtering/history/keyboard controls and routed UI presence. No usability outcome claimed. |
| P06 | [Company portability](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services/company-portability.ts), `extractPortableScopedEnvInputs`, `previewExport`; [portability tests](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/__tests__/company-portability.test.ts), [team catalog UI](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/ui/src/pages/TeamCatalog.tsx) | Preview/import/export, secret-input treatment, collision handling and team-template surface. Export content needs its own review. |
| P07 | [Routines](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services/routines.ts), [routine tests](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/__tests__/routines-service.test.ts) | Revision snapshots, timezone, run idempotency, skip/coalescing and trigger policies. |
| P08 | [Budgets](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services/budgets.ts), `budgetService`, `evaluateCostEvent`, `getInvocationBlock`; [costs](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services/costs.ts), `costService.createEvent`; [budget tests](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/__tests__/budgets-service.test.ts) | Cost-ledger integration, scope policies, incident/pause/cancel and admission checks. No strict zero-overspend proof. |
| P09 | [Skill Studio](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/ui/src/pages/SkillStudio.tsx), [Cognee connection](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services/cognee-connection.ts), [LLM Wiki](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/packages/plugins/plugin-llm-wiki/README.md) | Skill editing/testing surfaces, remember/recall integration and alpha knowledge plugin. These qualify any claim that Paperclip lacks memory. |
| P10 | [Task watchdogs](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services/task-watchdogs.ts), [service inventory](https://github.com/paperclipai/paperclip/tree/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services), [workspace diff plugin](https://github.com/paperclipai/paperclip/tree/0b12ca9532a006fe3849b1efe0a52bac2f301285/packages/plugins/plugin-workspace-diff) | Watchdog state model; source surfaces for artifacts, documents, work products, workspaces and GitHub merge. Broader inventory is weaker evidence than a traced lifecycle. |
| P11 | [Package scripts](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/package.json), [PR workflow](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/.github/workflows/pr.yml), [trusted PR jobs](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/.github/workflows/pr-trusted.yml), [chaos evals](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/.github/workflows/runner-chaos-evals.yml), [visual-test config](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/tests/storybook-visual/playwright.config.ts) | Checked-in quality gates, browser/visual/release/fault test entry points. Tests and workflow history were not executed or audited for pass rates. |
| P12 | [Deployment modes](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/docs/deploy/deployment-modes.md), [sandbox requirements](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/packages/plugins/sandbox-providers/SANDBOX-REQUIREMENTS.md), [low-trust validation](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services/low-trust-runtime-containment.ts), [plugin VM loader](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services/plugin-runtime-sandbox.ts) | Different trust modes, provider assumptions, low-trust checks and module restrictions. Not a security certification. |
| P13 | [Service inventory](https://github.com/paperclipai/paperclip/tree/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services), [server dependencies](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/package.json), [live events](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/server/src/services/live-events.ts) | Connector/gateway source breadth and the specifically process-local live-event mechanism. |
| P14 | [License](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/LICENSE) | Root MIT grant, copyright/notice condition and warranty disclaimer. |
| P15 | [Roadmap](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/ROADMAP.md) | Upstream direction and claimed milestones. Not treated as independent proof of completion. |
| P16 | [Historical dashboard screenshot](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/screenshots/PR-8000-home-flag-off.png) | Visually inspected illustration only. No current-browser, mobile or accessibility inference. |

### C-Sweet sources

| ID | Sources and symbols | What they support |
|---|---|---|
| C01 | [Current README](../../../README.md), [implementation index](../../implementation/README.md), [navigation](../../../src/CSweet.UI/Layout/NavMenu.razor) | Current developer-preview status, setup/deployment caveats, catalog, product surface and historical-plan warnings. |
| C02 | [Isolation overview](../../../Documentation/Architecture/AGENT_ISOLATION_SECURITY_OVERVIEW.md), [Office README](../../../../CSweet.Office/README.md), [RuntimeHost wiring](../../../../CSweet.Office/src/CSweet.Office.RuntimeHost/Program.cs), [assisted installation](../../implementation/features/office-assisted-installation.md), [Isolation primitives](../../../../CSweet.Isolation/README.md) | Execution boundary, privileged verification wiring, provider scope, existing setup work and unverified deployment portions. |
| C03 | [Work-management map](../../implementation/features/work-management/README.md), [AgentWorkInbox](../../../src/CSweet.Infrastructure/Setup/AgentWorkInbox.cs), [WorkOrchestrator](../../../src/CSweet.Infrastructure/WorkManagement/WorkOrchestrator.cs), [orchestration model](../../../src/CSweet.Domain/WorkManagement/WorkOrchestration.cs) | Canonical work, policy revisions, leased/idempotent dispatch, stages and recovery structure. |
| C04 | [Workforce handler](../../../src/CSweet.AgentHost/Broker/WorkforcePlatformCapabilityHandler.cs), `EvaluateBudgetAsync`; [LLM handler](../../../src/CSweet.AgentHost/Broker/PlatformLlmCapabilityHandler.cs), [LLM job service](../../../src/CSweet.AgentHost/Broker/PlatformLlmJobService.cs), [inference analytics](../../../src/CSweet.Infrastructure/Analytics/InferenceAnalyticsService.cs) | Existing budget reservations and provider limits; sampled dispatch/usage path and monetary-enforcement gap requiring verification. |
| C05 | [Communications](../../implementation/native-communications-hub.md), [hiring lifecycle](../../implementation/features/hiring-suggestion-lifecycle.md), [approval dashboard](../../../src/CSweet.Infrastructure/Core/ApprovalDashboardService.cs), [chat diagnostics](../../implementation/chat-turn-diagnostics.md) | Conversation authority/outboxes, stale-action handling, authorized approval aggregation and durable failure evidence. |
| C06 | [Attention scheduler](../../../src/CSweet.AgentHost/Broker/AgentAttentionScheduler.cs), [platform event entity](../../../src/CSweet.Domain/Core/AgentPlatformEventOutboxItem.cs), [company overview](../../../src/CSweet.UI/Pages/CommandCenter.razor), [Current Activity](../../implementation/features/current-activity.md), [employee audit timeline](../../implementation/features/employee-audit-timeline.md) | Platform-owned review wakes, durable events and existing operator views. |
| C07 | [App memory service](../../../src/CSweet.Infrastructure/Core/AgentMemoryService.cs), [memory framework](../../../../CSweet.Memory/README.md) | Actual recall/capture integration plus framework-level provenance, namespace and transfer capabilities. Framework claims were not all traced end to end in the UI. |
| C08 | [Agent SDK](../../../../CSweet.Agent.Sdk/README.md), [Python support status](../../../../CSweet.Agent.Sdk/python/README.md), [Chief](../../../../CSweet.Agent.ChiefOfStaff/README.md), [Producer](../../../../CSweet.Agent.Producer.VideoGame/README.md), [Chief refinement intent](../../16-chief-of-staff-agent-refinement.md) | Supported authoring path, described specialist behavior and distinction between current callbacks and executive vision. |
| C09 | [Collaborative documents](../../implementation/features/collaborative-documents.md), [internal Git](../../implementation/internal-git-hosting.md) | Canonical artifact revisions, access/review, internal repositories, LFS, merge semantics and operational limits. |
| C10 | [Project health](../../implementation/features/producer-project-health.md), [ProjectHealthService](../../../src/CSweet.Infrastructure/Core/ProjectHealthService.cs), [ProjectHealthReader](../../../src/CSweet.Infrastructure/Core/ProjectHealthReader.cs) | Local work in progress: incident detection, manager escalation and human delivery. Historical test counts in that doc were not rerun by this assessment. |
| C11 | [Work efficiency and benchmarks](../../implementation/features/work-efficiency-and-benchmarks.md), [game-production reliability](../../implementation/features/game-production-reliability/README.md), [playbook phase](../../implementation/features/game-production-reliability/05-phase-4-playbooks-and-programs.md) | Detailed accounting semantics, benchmark implementation and stated limitations; future criterion/playbook work. |
| C12 | [Windows build workflow](../../../.github/workflows/windows-build.yml), [build switches](../../../Directory.Build.props), [unit tests](../../../tests/CSweet.UnitTests), [integration tests](../../../tests/CSweet.IntegrationTests), [contributor rules](../../../AGENTS.md) | Main-repository CI scope, existing regression assets and package/version obligations. |

## Findings that should not be overstated

| Tempting claim | Supported formulation |
|---|---|
| Paperclip is just a task board or CLI launcher. | It contains a broad control plane, native runner, skills, governance, portability and recovery surfaces. |
| Paperclip has no memory or security. | It has knowledge integrations, sessions and explicit security controls; its execution assurance varies by mode/provider. |
| Paperclip guarantees no budget overspend. | Observed costs trigger incidents, admission blocks and pause/cancel behavior; a strict concurrent spending bound was not established. |
| C-Sweet needs approvals, budgets or recovery from scratch. | Those already exist; integration, coverage and operator experience are the actual opportunities. |
| C-Sweet's larger specialist catalog proves better output. | It proves available role/package breadth, not artifact quality or reliable delivery. |
| C-Sweet already implements every reliability-plan phase. | Plans and implementation differ; curated playbooks and full acceptance coverage require separate verification. |
| An MIT repository makes every included dependency or asset freely reusable on identical terms. | Verify the license/provenance of the exact files and dependencies selected for reuse. |
| More tests or workflow files prove production readiness. | They show engineering assets and intended checks; observed runs and deployment acceptance are separate evidence. |

## MIT reuse and practical borrowing

The inspected root license is MIT, copyright Paperclip AI, 2025. It grants permission to use, modify and distribute the covered software, including commercially, subject to retaining its copyright and permission notice in copies or substantial portions. It also disclaims warranty. [P14](https://github.com/paperclipai/paperclip/blob/0b12ca9532a006fe3849b1efe0a52bac2f301285/LICENSE)

For a concrete port, record the source commit/files, preserve applicable notices, inspect file-level and dependency licenses, and document modifications. Do not infer a right to adopt Paperclip branding, third-party assets or hosted-service access from the source license. This assessment copies no application implementation into C-Sweet; it links evidence and proposes adaptation.

| Candidate | Recommended reuse mode | Reason |
|---|---|---|
| Action queue taxonomy and interaction patterns | Reimplement on C-Sweet projections | Valuable operator design; React components are not directly reusable in Blazor. |
| Budget incidents and admission concepts | Adapt with stronger explicit settlement semantics | Existing C-Sweet budgeting and accounting should stay authoritative. |
| Recovery/conformance scenarios | Translate fixtures and assertions; preserve notices for copied material | Behavior and failure cases transfer better than the runtime. |
| Template schema ideas | Define a C-Sweet format with preview and versioned provenance | Identity, grants and package installation differ. |
| Adapter normalization contracts | Use as design reference for one qualified bridge | Sessions/errors/usage are useful; credentials and execution boundaries differ. |
| Small parsers or pure utilities | Consider case by case | Port only when license review, tests and maintenance cost justify it. |
| Entire heartbeat/issue service or database model | Avoid | Large coupling to Paperclip persistence, providers and authority model. |
| Native runner as a dependency | Separate architectural experiment | Rust protocol/driver integration and qualification are substantial, not a drop-in SDK replacement. |

## Fair follow-up validation

No paid inference, running application, test suite or provider sandbox was exercised for this assessment. A controlled evaluation should use disposable companies and fixture repositories, with explicit cost limits and equivalent model/provider configuration where supported. Installation differences should be recorded rather than hidden.

| Experiment | Evidence to collect | What it would establish |
|---|---|---|
| Fresh install through accepted first artifact | Prerequisite time, interventions, failures, readiness and artifact receipt | Activation quality, beyond README commands. |
| Same small software change | Exact revision, tests, review cycle, costs/coverage, elapsed time | Delivery effectiveness under comparable conditions. |
| Restart and disconnected worker | Lease transitions, recovered wake, duplicate-effect checks | Recovery behavior, not merely successful restart. |
| Concurrent budget exhaustion | Admission/reservation records, provider calls and final billed/unknown totals | Actual enforcement boundary and overspend exposure. |
| Stale approval and revoked grant | Old/new revisions, rejected mutation and audit | Current-authority enforcement. |
| Import/export into a second instance | Preview, missing dependencies, secret scan, identities and round-trip assertions | Real portability and safe activation. |
| Keyboard and narrow-screen supervision | Completed tasks, focus behavior, screen-reader labels and screenshots | Usability/accessibility evidence currently missing. |
| Backup and restore | Restored artifacts/repositories, trust state, lease reconciliation, lost-work inventory | Recoverability and measured recovery objectives. |

Report distributions and failed cases, not only the best run. Compare accepted outcomes, human effort and consumption separately. Keep unknown usage, unsupported providers and incomplete trials visible. These experiments are the appropriate next step for replacing this assessment's qualified judgments with measured evidence.
