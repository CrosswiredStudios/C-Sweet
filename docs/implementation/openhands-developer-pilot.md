# OpenHands developer pilot

The independent `CSweet.Agent.SoftwareDeveloper.OpenHands` repository contains three 0.1.4 packages:
`CSweet.Agent.Development` (shared SDK workflows), `CSweet.Agent.OpenHands` (execution worker), and
the thin agent entrypoint. The initial release is 0.1.0; 0.1.1 adds the installer-required bundle
checksum sidecar. Version 0.1.4 preserves provider reasoning defaults and operational failure
classification, and is installed through the normal update flow in the disposable pilot business.
The opt-in identity is `com.csweet.software-developer.openhands`.
Daniel and Adrian's repositories, package versions and installed payloads are unchanged.

`IDevelopmentExecutionEngine` receives current authorized context, source workspace, tools, limits
and checkpoint callbacks. Shared workflows retain exact-source validation and SDK publication;
OpenHands owns the coding conversation. The unmodified OpenHands SDK/tools 1.56.0 run in a private
hash-locked Python 3.12 environment inside the existing certified Linux guest. The .NET entrypoint
supplies an ephemeral, work-scoped `ModelBridge` backed by `AgentRuntimeContext.CreateChatClient`.
Python receives only that callback's loopback credential and declared tool schemas. All provider
selection, model budgets, leases and platform mutation authority stay with C-Sweet.

## Platform symbols

- `ResearchCapabilityHandler` implements SDK research using the startup-managed SearXNG endpoint
  and `PlatformWebProxyCapabilityHandler` for page fetches. SmartReader extracts supplied HTML;
  it does not perform an additional network fetch. Public DNS checks, pinned connections, redirect
  authorization, timeouts and response limits remain in the protected transport.
- `ExecutionCheckpointCapabilityHandler` supports inline and staged/chunked archive transfer without
  increasing Office's broker frame limit. `ResearchAndCheckpointToolSchemas` hides checkpoint tools
  from models and documents bounded research inputs.
- `ExecutionCheckpointStore` writes protected immutable engine/source blobs before advancing a
  revisioned `PluginOperationalState` head under `RuntimeCheckpointGate`. Current work leases and
  source assignment authorization are required. Engine versions and logical work lineage are checked
  on restore. There is no new table or metadata migration: version 1 reuses the existing state schema.
- `ExecutionCheckpointCleanupService` invokes serialized, ownership-checked orphan collection. Current
  head blobs survive cleanup; abandoned uploads and unreferenced generations have a 24-hour grace
  period. Private storage is bounded to 2 GiB per installation. Existing business-storage deletion
  removes the organization tree, and removed installations are reclaimed after the grace period.
- `ManagerResponseRecovery` retains exact question/answer revisions and asks the typed orchestration
  retry API to resume only a resolved, unchanged assignment. Discussion does not grant execution rights.
- `FirstPartyMarketplaceAgentOptions.ManualSelectionOnly` keeps the published pilot visible for owner
  selection. `IAgentCatalogService.IsAutomaticSelectionAllowed` excludes flagged agents from automatic
  candidate selection and rechecks the restriction before a delegated hire can commit. Installed copies
  retain this restriction through their stable agent identity. The default developer remains eligible.

The new generic APIs ship in SDK 5.1.0. WorkManagement and Office contracts were not modified.

## Verification and rollout

`ResearchCapabilityTests` covers search parsing, partial failures, extraction and policy delegation.
`ExecutionCheckpointStoreTests` covers protected storage, revision/idempotency, stale authority,
archive rejection, chunk assembly, storage bounds and cleanup. Existing gateway/archive/deletion
regressions protect shared transport and storage behavior. The pilot repository adds real pinned
worker tests, model translation, workflow regressions and manager-response recovery tests.

Publish SDK 5.1.0 before the new packages and agent release. Import and install through normal
C-Sweet flows, then use a disposable project and independently review the delivered candidate.
`docs/pilot-acceptance.md` in the agent repository defines the live journeys and matched-model
comparison. Build/test results alone do not establish live acceptance or justify replacing a developer.

The pilot has verified clean Linux preparation (628 compute tests), installed 0.1.4, and reached
real model/tool execution and test repair. All 244 adapter tests passed locally and in Linux release CI.
Live work also exposed platform recovery defects: project plan-child workspace routing, execution
order after board retries, and project coordinator claims expiring during a live callback.
`AgentWorkInbox.RenewPersonalTaskClaimAsync` now renews only an authorized live project claim,
including exact server-bound inline callbacks, within the delivery deadline. All 175 focused
lease, project-policy, personal-plan and workspace tests passed. Detailed evidence and unresolved
live scenarios are recorded in the agent repository's `docs/pilot-results.md`; source review,
governed manager resume and the matched comparison remain acceptance requirements.

## Human manager replies

OpenHands 0.1.5 extends assigned-stage questions to current human managers. The reviewed grants and
SDK/WorkManagement/Office packages do not change. `TicketDiscussionTools` retains either an agent
installation or human employee identity; `ManagerResponseRecovery` checks exact question/answer
revisions, current roster availability, management and assignment before typed orchestration retry.
All 260 adapter tests passed locally, including actual worker persistence, with 22 focused question
and response cases. Linux release CI verifies the same suite before publishing assets.

`WorkItemComments` offers Reply only on agent questions when the viewer can comment. The internal
`AddWorkItemCommentRequest` accepts an optional question identity/revision; the platform validates
the current question on that ticket and writes `discussion.reply` plus its correlation, activity
and notification outbox atomically. Changed/deleted/foreign questions and archived boards are
rejected. Existing ordinary comments retain their behavior. All 36 collaboration/UI/discussion
tests passed. Replies remain evidence and cannot grant execution or approve a scope change.
