# OpenHands developer pilot

The independent `CSweet.Agent.SoftwareDeveloper.OpenHands` repository contains three 0.1.0 packages:
`CSweet.Agent.Development` (shared SDK workflows), `CSweet.Agent.OpenHands` (execution worker), and
the thin agent entrypoint. The opt-in identity is `com.csweet.software-developer.openhands`.
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
