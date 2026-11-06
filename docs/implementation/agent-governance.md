# Agent governance

## Posture and operation approval

`DatabaseAgentPostureStore` persists posture on `AgentInstallation`. API and AgentHost use scoped readers over the same database. A missing assignment means Manual; invalid stored values refuse execution. Migration `PreserveExistingAgentAutonomy` pins installations that existed before posture governance to Auto, so upgrading does not start queueing approvals for every write; new installations keep the Manual default. `AgentInstallationService.UpdateAsync` carries the chosen posture to each upgraded revision. `AgentGovernanceAccess.CanManageAsync` permits the account owner for user installations, a business Owner, a Manager over the direct report, or a deployment administrator. Archived memberships and employees confer no authority.

`AgentPostureEndpoints` accepts named Plan, Manual and Auto values through the interactive authentication scheme. `AgentGovernancePanel` exposes that control in the employee Configuration tab.

`PlatformCapabilityDispatcher.InvokeAsync` checks grants and handler availability before approval admission. Plan refuses mutations. Manual creates a durable `AgentCapabilityApproval` containing the exact capability and payload, rather than executing it. `CapabilityApprovalInbox` in Approvals lets an authorized human approve or deny that operation. `ApprovalDashboardService` includes pending operation counts in the navigation badge.

`AgentCapabilityApprovalService` fingerprints installation, grant revision, posture revision, capability and exact payload bytes. An approved retry claims execution once. Results are durably retained and replayed; a lost response does not repeat the handler. A policy refusal before execution defers the claim so the approval remains usable after the window clears. Changing the grant or posture invalidates the old approval.

Identical requests in the same authority revision refer to the same operation. Callers that intend distinct operations should use the capability's own stable idempotency key in each payload. An `Executing` receipt without a completion is indeterminate: reconcile the actual operation before requesting new work. The host never automatically repeats that execution. `CSweetDbContext.CaptureGovernanceInvalidations` atomically supersedes pending/approved requests when their posture, grant revision or installation availability changes, so obsolete requests do not remain actionable in the inbox. Approval reads refresh tracked records under the admission lock so an API decision is visible inside a longer-lived broker scope.

`GovernanceEvents.EnqueueAsync` saves audit and notification outbox records in the mutation's transaction. Targeted governance wake hints do not require package subscriptions; they carry identifiers and a recovery capability, not an execution grant. `AgentGovernanceCapabilityHandler` exposes `platform.agent-governance.read.v1` to the authenticated installation: list up to 20 statuses, or provide `approvalId` to retrieve one exact request. Agents should use it after a wake or reconnect and retry only the approved request. Human approval does not bypass the capability handler's domain authorization.

## Temporal admission

`AgentPolicyService.TryAdmitAsync` serializes count and sealed audit append with a PostgreSQL transaction-scoped advisory lock for the business. The dispatcher applies it before destructive and outward executions, including Auto. Current limits are three destructive or sixty outward attempts per installation per hour. Failed attempts still count; a process failure after admission cannot erase the reservation. In-memory fixtures use bounded process-local gates.

This counter does not verify Office attestations, certification recency or host configuration. See [Office attestation decision](../security/OFFICE-ATTESTATION-DECISION.md).

## Delegation packets

`DelegationEndpoints` stores supplemental packet metadata linked to `TrackedDelegation`; it does not maintain a second execution lifecycle. Creation requires the human's own published instruction, a source conversation shared with the managed coordinator, and an idempotency key. `TrackedDelegationService` creates canonical commitments, recipient conversations and durable wake events. Input artifact IDs and the return contract are retained and supplied in the intended outcome. Reads expose the canonical status, revision and work item.

Decisions require authority over the recipient and call the canonical `RespondAsync` with the expected revision and operation key. Completion retains the existing work/evidence checks. Metadata and human audit evidence save in the same transaction as canonical changes. Old standalone packets display as Legacy and cannot pretend to execute; create a sourced tracked delegation instead. `DelegationInbox` renders the packets in Approvals.

## UI and resource limits

`WorkspaceOperationTray` combines live hiring and business setup state, preserving retry, detail and dismiss actions. `OperationTray` displays action-required states explicitly. It replaces the duplicate placeholder tray.

`CodemodeCapabilityHandler.CopyValue` enforces cancellation, depth, node and cumulative byte budgets during evaluation. Nested capability responses are bounded before parsing. Codemode remains disabled by default.

`Sync-AppearanceThemes.py` checks text and semantic colours against page, panel and raised surfaces. All 43 themes retain Night Glass as the default.

Apply migration `PersistAgentGovernance` before running the updated API and AgentHost. The migration adds posture fields, operation approvals and the tracked packet reference. No installed agent payloads are patched by this change.

## Regression coverage

- `AgentGovernanceRegressionTests`: shared persistence, exact approval/replay, management authorization, Plan writes, posture invalidation and policy deferral.
- `AgentPolicyAdmissionRegressionTests`: independent service instances admitting against the sealed audit writer.
- `DelegationPacketRegressionTests`: canonical work/wake creation, metadata retention, idempotent replay and conflicting retries.
- `CodemodeBudgetRegressionTests`: bounded expansion of repeated references.
- `AppearanceStateTests.EveryThemeKeepsSecondaryTextReadableOnEverySurface`: independent contrast checks across all presets.

- `AgentGovernancePostgresTests`: applies migrations to an isolated database, verifies posture across contexts, races approval claims across connections, and checks admission with independent service providers and sealed audit writers. The isolated database is deleted afterward.

These checks exercise host admission and recovery contracts; they do not patch installed agents or prove that every published agent handles the new wake event. Current state remains readable through the installation-bound recovery tool.

## Verification on 2026-10-28

- The final focused run passed **231/231**, including both PostgreSQL acceptance tests. No tests in that run were skipped.
- The final full unit run reported **5,892 passed, 10 failed, 538 skipped** out of 6,440. The ten failures below were also present in the original audit run. Failures introduced while integrating the new controls were corrected and verified before this final full run.
- `dotnet ef migrations has-pending-model-changes` reports no pending model changes.
- `Sync-AppearanceThemes.py --check` passes. Primary and muted text meet 4.5:1 on page, panel and elevated surfaces across all 43 themes.
- The demo builds and runs on `http://127.0.0.1:5198`. Browser checks confirmed Overview, Projects, Source Control, Compute, Approvals, Calendar, Documents, Employees, Analytics, Communications, Security, Settings and Appearance render their content. Theme switching to Pearlescent and back to Night Glass was verified. Exact repository-list and runtime-status fixtures replace the remaining 501 responses; unknown requests still fail explicitly.

The remaining full-suite failures are recorded without attributing them to the audited handoff:

| Tests | Remaining failure |
| --- | --- |
| `HierarchicalDeliverySchemaTests.EveryAffectedAgentAcceptsRealV2OutcomesAndPublishesVersionMatchedNotes` | Missing sibling `CSweet.Agent.CreativeDirector.VideoGame/csweet-plugin.json`. |
| `ProjectDeliverySetupTests.Published_manager_profile_has_a_valid_bounded_execution_graph`, `WorkManagementCapabilityHandlerTests.ApprovedManagerSetupGrantsCanConfigureItsRealDeliveryProfileThroughTheBroker` | Expected manager delivery profiles are null in the available fixture/package. |
| `AgentWorkspaceBrokerTests.Canonical_stage_workspace_requires_current_attempt_repository_and_action` (valid snapshot), `OfflineBrokerPublishesRealGitAndLfsThenMergesExactCommit` | Business storage admission rejects the test scope as unavailable/deletion pending. |
| `AgentMemoryServiceTests.ConversationLifecycleCaptureRejectsIneligibleContextBeforeWriting` (`kind`, `nonhuman`) | Expected exception is not thrown. |
| `ApprovalDashboardServiceTests.DeliveredOpenIncidentIsSeparateAttentionAndDisappearsOnlyAfterRecovery` | Expected delivered incident attention item is absent. |
| `GenericStudioArchitectureTests.CoreImplementationContainsNoVideoGameOrEngineBranches` | Existing vertical branches in `ProjectSetupService.GamePortfolio`, `RepositorySourceStore` and `WorkDeliveryService.ComputeRecovery`. |
| `IcuMessageFormatterTests.Every_supported_culture_ships_resources_for_every_area` | Missing `zh-Hans` resource keys. |

TRX evidence is retained outside the checkout in the user's temporary `csweet-governance-fixed-results` directory: `governance-final-verified.trx` and `governance-final-full.trx`.
