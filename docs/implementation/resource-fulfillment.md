# Durable resource fulfillment

## Outcome

An agent requests a project capability, its current manager reviews an exact provisioning
plan, and a trusted worker installs and verifies capacity. The same resource request owns
progress, failures and readiness. Waiting agents recover from current state after reconnect;
approval is never treated as working capacity. Existing managed-action approvals are reused.

## Implementation tracker

- [x] Typed SDK request/read/decision surface and manager review coordinator.
- [x] Host-owned durable request state, exact approval binding, current authority and outbox.
- [x] Pluggable fulfillment handlers; first handler: published Node/Phaser build provider.
- [x] Shared certification scheduling, restart-safe installation and real fixture evidence.
- [x] Director/Producer/Technical Director continuation and consistent readiness policy.
- [x] Human approval/status surface and failures visible on the same request.
- [x] Failure-scenario regressions, package-consumer checks and release bundles.
- [ ] Published-release installation and live business recovery verification.

## Boundaries

The first executable profile is Node/TypeScript build capacity (Phaser/Babylon). Additional
tool families plug into the same host-owned handler interface; unsupported families fail
explicitly rather than receiving an approval with no executor. Ordinary project npm restore
continues using approved compute outbound access. The model receives no installer command,
credential, host filesystem or database authority. A manager can approve an exact first-party
provider plan through its granted resource decision capability and current reporting chain.
Installation uses normal published-package import/build/install, never patched installed payloads.
Missing compatible host capacity remains a visible setup dependency. Package/image/permission
changes invalidate a reviewed plan; a successful fixture and current eligibility are required
for Ready. Shared platform polling reconciles durable asynchronous operations; agents use events
and bounded reads rather than polling loops.

## Lifecycle and recovery

`ResourceFulfillmentService` stores requests and command receipts in the existing host-owned
`PluginOperationalStates` store. No schema migration is needed. Request creation and every
transition persist agent notifications and application realtime outbox rows in the same
transaction. PostgreSQL transaction advisory locks serialize business mutations; receipts
bind an actor's stable key to the actual command. A repeated key with different requirements
is rejected. Same-requester/project/profile active requests coalesce.

`Preparing` is persisted before repository lookup, download or image discovery. Its plan is
null and it grants no installation authority. The worker prepares a verified plan and creates
the exact approval atomically with the transition to `PendingApproval`. Missing releases,
private catalog access and temporarily unavailable images remain visible dependencies;
recovery honors provider retry timing and does not re-run an agent conversation.

`PendingApproval` becomes `Approved`, `Rejected` or `RevisionRequested`; escalation keeps the
plan and assigns the next eligible manager. Approval binds package/manifest/definition/image
digests, permissions, cost and scope. JSON values, rather than serialized formatting, are
compared because PostgreSQL normalizes approval JSON. Current reporting relationships,
requester project membership, installation grants and owner-approval policy are rechecked.
Approval is not a readiness certificate.

`ResourceFulfillmentWorker` reconciles `Approved` → `Installing` → `Certifying` → `Ready`.
It discovers persisted installation and certification effects after lost replies or restart.
Temporary transport failures become `WaitingDependency` and resume the retained stage.
Actual fixture failure becomes `Failed`; changed authority/artifacts become `NeedsAttention`
or `Revoked`. Identical failed fixtures do not rerun automatically. The assigned approver can
retry the same still-valid approved plan, incrementing `FulfillmentAttempt` and preserving
the prior evidence while creating a new certification identity. A changed plan needs a fresh
request and review. Ready resources are revalidated and lose readiness after revocation.

`NodeToolchainFulfillmentHandler` obtains the official published Node/TypeScript bundle,
validates it before review and uses `IAgentInstallationService` for business-scoped on-demand
installation. It delegates real fixture creation/dispatch to `ToolchainCertificationScheduler`,
shared with administrator setup. The existing certification worker requires reproducible
fixture evidence before granting eligibility. There is no local-source or uncertified fallback.

## Agent collaboration

The Director's `EnsureToolchainDecisionAsync` discovers current technical leadership and
hands supported missing capacity to the Technical Director as `platform.resource.requirement.v1`.
The Technical Director requests it through `PlatformResourceClient` and returns its durable
identity immediately. Producer (or an eligible higher manager) reviews the exact plan through
the SDK's `ManagerResourceReview`; this is separate from installation and certification.
Existing owner-approval configuration still routes decisions to a human owner.

`ResourceCapabilities.Changed` is a wake hint. Manager attention also discovers assigned
pending approvals after missed delivery. `ReconcileResourceReadinessAsync` rereads current
state and wakes only the matching project's production commitment after Ready. Existing
Technical Director feasibility and project selection remain required. Old supported-profile
setup decisions no longer force agents through an inert manual setup authorization.

## Source map

- SDK: `ResourceFulfillment.cs`, `ManagerResourceReview.cs`, `CSweetManagerAgentBase`.
- Infrastructure: `ResourceFulfillmentService`, `IResourceFulfillmentHandler`,
  `NodeToolchainFulfillmentHandler`, `ToolchainCertificationScheduler`.
- Agent host: `ResourceFulfillmentCapabilityHandler`, `ResourceFulfillmentWorker`, `McpToolCatalog`.
- API/UI: `ResourceFulfillmentEndpoints`, `ApprovalEndpoints`, `ApprovalDashboardService`,
  `ResourceFulfillmentPanel`. The status panel appears on Approvals and refreshes after reconnect.
- Regressions: `ResourceFulfillmentTests`, `ResourceFulfillmentPostgresTests`,
  `NodeResourceFulfillmentTests`, SDK `ManagerResourceReviewTests`, Director
  `ProductionCommitmentTests`, Technical Director `ResourceRequestTests`.

## November 6 follow-up: inaccessible provider and lost handoff

The live `Prism Shatter` handoff failed before a resource record existed. The provider's
private GitHub repository already contained release **1.0.2**, its Linux `.cspb` and checksum.
The runtime had no catalog credential, so anonymous release discovery returned 404. The
original implementation mislabeled that as a missing bundle and then rediscovered the same
failed coordination session. Download credentials and AppHost forwarding also needed parity
with metadata access. No new provider version or source-build fallback is required.

The published bundle was downloaded with existing developer access for verification only.
SHA-256: `1ccdb8dafa617b7d77724c909f209a7572553220b9f6b630e51a87b6db06d646`.
Package structure, Linux x64 identity and checksum pass. This does not configure a runtime
credential, publish a repository or install an agent.

`GitHubReleaseAssetAuthenticationHandler` scopes authentication to GitHub's asset API.
`AgentImportPreviewException.IsDependencyFailure` and `RetryAt` preserve remote access and
rate-limit failures through preparation; malformed package verification remains terminal.
The project Overview and Approvals show preparation conditions even with no plan. The retry
endpoint rechecks current authority and exact revision; retrying preparation cannot approve it.

Director **0.5.1** resumes the retained failed resource-request handoff with a stable
`durable-preparation-v1` recovery key. A replay cannot create another recovery epoch. Only the
specific legacy validation failure qualifies; unrelated or unauthorized failures are shown.
Current resource failures block the matching commitment with the actual condition, and a
resource change wakes it for another current-state read.

When the replacement resource is durably created, `WithdrawObsoleteSetupDecisionsAsync`
cancels only pending questions in the same business/project with matching recipe, toolchain
decision type and old setup authority rule. It preserves options, evidence and history, records
the replacement resource ID and rationale, and cancels linked pending executive cards without
inventing a human answer. Existing answers, other projects/recipes and feasibility questions
are retained. Card/decision lock order matches human answering; state and realtime notifications
commit together. The Director then clears its cancelled internal reference.

Follow-up rollout: publish SDK **5.14.0**, then publish/install Producer **6.4.1**, Director
**0.5.1** and Technical Director **6.1.1**, and restart with the matching platform build. The
existing Node provider **1.0.2** stays unchanged. Repository access must be resolved through
an operator-configured read-only catalog credential or an explicitly authorized public release
distribution. Never copy developer credentials into agents or modify installed package records.

Live recovery and question withdrawal remain pending that rollout and access prerequisite;
local tests are not evidence that the live handoff has recovered.

Follow-up verification: SDK 751 tests, sample 2, generated template 7; Producer 509,
Director 367 and Technical Director 231; affected platform/import/update/UI subset 278;
PostgreSQL concurrency and runtime identity 2, run separately against disposable databases.
All pass. AppHost builds with the secret-parameter forwarding. Each published agent output
passes its self-test, source checks, NuGet/version checks and bundle verification. Agent
assemblies match their NuGet packages, and every bundle contains the same final SDK assembly.
Artifacts and live prerequisites are recorded in
`.artifacts/cool-games-review/resource-fulfillment-followup-release-verification.json`.

## Initial verification and rollout

Regression coverage includes missing capacity, exact manager decisions, duplicate commands,
restart after a lost installation reply, concurrent PostgreSQL workers, bounded discovery,
scope/permission/artifact changes, escalation, explicit retry, reproducibility failure and
stale readiness notifications. Fixture orchestration tests drive the actual scheduler and
certification evaluator with retained provider receipts; they do not run a real remote browser.

Publish SDK **5.13.0**, then publish/install Producer **6.4.0**, Director **0.5.0** and Technical
Director **6.1.0** using Installed Agents. Restart with the matching C-Sweet platform build.
No developer or QA release is needed. The handler needs a published official Node provider
bundle and a compatible ready Office image; private release discovery uses the platform's
configured GitHub credential. Missing host capacity remains an explicit dependency.

After deployment, verify the existing project's resource request, assigned approver, actual
fixture result and resumed production commitment. Local test/package verification does not
prove that the live team has recovered or that the game has been completed.

Local verification: SDK 750 tests plus 2 sample and 7 generated-template tests; Producer 509,
Director 357 and Technical Director 231; resource/platform subset 90, with the 2 PostgreSQL
tests run separately against disposable databases. All pass. Published-output self-tests,
bundle verification, package/manifest/release-note version checks and byte-for-byte SDK and
agent assembly checks pass for all three agents. Prepared artifacts are listed in
`.artifacts/cool-games-review/resource-fulfillment-release-verification.json`.

The broader approval/broker subset passes 280 tests, skips 6 environment-dependent tests and
has one existing failure: `ApprovalDashboardServiceTests.DeliveredOpenIncidentIsSeparateAttentionAndDisappearsOnlyAfterRecovery`.
Its fixture expects an incident without current failure or owner input to remain attention;
the unchanged `ProjectHealthService.ReadAttentionAsync` deliberately filters it. This is not
part of the resource lifecycle fix, and the broader suite is not claimed fully green.

## Owner attention

A request in `WaitingDependency`, `NeedsAttention` or `Failed` cannot continue without a person. `ResourceFulfillmentPanel`
takes `AttentionOnly` and `CountChanged`; `CommandCenter.razor` renders it inside "Needs your attention" and adds its count to the
pending total, so the overview no longer reads "Nothing needs your attention" while setup is stuck. The Approvals and project
pages keep the full list. The panel now loads when its parameters change, on realtime `ResourceChanged`, and after Retry; a
parent re-render alone does not refetch.

First seen on 2026-11-10: a fresh database had no Node/TypeScript provider and the release repository was private, so the
request sat in `WaitingDependency` behind warning toasts while the overview showed 0 pending. The first-party provider
repository must be readable by the deployment (public, or `GitHubAgentRepository:AccessToken` configured).

## Provider installation uses the plugin installer

`NodeToolchainFulfillmentHandler` installs the approved provider through `IPluginInstallationService`. It previously took
`IAgentInstallationService`, whose `InstallAsync` accepts agent packages only, so the first real installation (2026-11-10,
Super Awesome Games) stopped in `NeedsAttention` with "Communication providers must be installed through the plugin API."
`NodeResourceFulfillmentTests` used a proxy of the agent interface and could not see this; it now proxies the plugin interface.

## Owner retry

`ResourceFulfillmentService.DecideAsync` lets an active human business Owner send `Retry` for a request in `Failed` or
`NeedsAttention`, in addition to the assigned approver. The revision and plan hash are still required, so the retry re-runs the
same reviewed plan. `ResourceFulfillmentEndpoints` sets `canRetry` to match, which shows "Retry approved setup" to the owner.
Reason: when an agent manager approved the plan and setup then stopped, no one could restart it. The approving agent has no
retry path and the Video Game Director stops while the request is in that state. Approve, reject and revision decisions remain
approver-only. This change has no unit test yet.
