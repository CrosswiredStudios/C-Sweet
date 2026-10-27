# RealtyLeadsAI regression review

Completed 2026-10-27 on macOS. Feature branch in all four changed repositories:
`feature/realtyleads-api-key-mcp`. No production actions, push, package publication,
real provider key or native access grant. Core baseline is an unchanged independent
checkout at `d993760dd0e490e24560042a6bdea1e4728b422b` (`baseline-core`).

## Results

| Suite | Result |
| --- | --- |
| Full core UnitTests | 5,810 passed; 44 failed; 544 skipped; 6,398 total |
| Full core IntegrationTests | 98 passed; 1 failed; 6 skipped; 105 total |
| Final affected core tests after fixes | 182 passed; no failures/skips |
| Final broker security tests | 29 passed; no failures/skips |
| DeliveryWorkflow.Tests | After cloning official sibling sources: 11 passed, 6 PostgreSQL-dependent skips |
| GameRoleWorkflow.Tests | After cloning official sibling sources: 3 passed, no failures/skips |
| Earlier full SDK / sample | 609 / 2 passed |
| Earlier SDK template / agent | 7 / 6 passed, both executable self-tests passed |
| Earlier native browser acceptance | Connect → explicit grant → actual agent callback with fake MCP → revoke → denied |

Of the 44 failures in the full unit run, 42 reproduced on unchanged baseline. One
compute timing case failed once under the full run but passed three consecutive
runs (both parameter cases) on **each** checkout; it is not labeled baseline-reproduced.
No relevant source changed in that fixture or its compute runtime. The remaining
failure was the newly added relational vault fixture missing an AgentInstallations
provenance lookup table. That fixture was corrected and passes in the final 182-test
suite. The integration setup-step count failure (expected four, actual five) also
reproduces on baseline. There is no unresolved failure in the affected feature suite;
this is not a claim that the repository-wide suite is green.

The final affected filter covers RemoteMcp, plugin account authority/flows,
connector contracts, approval gateway, import preview, installation, source archive
and executable bundle imports. It uses the packed SDK 5.6.0 with source references
disabled and an isolated NuGet cache. The full-unit run includes the new relational
fixture before its correction; the final scoped rerun verifies the final code,
including archive rejection. Unrelated baseline defects were not modified.

## Environment and gaps

Initial core execution was interrupted after named-pipe fixtures rejected the long
workspace TMPDIR. The completed run uses `/private/tmp/rl-tests`, a short physical
path, and a 90-second hang collector (no hang termination occurred). All inherited
`CSWEET_*` and `ConnectionStrings__*` values were removed for broad-suite runs;
fixtures use Testing environment. No external test database was configured.

Unit skips include 490 requiring isolated memory PostgreSQL; other categories
include compute/hiring/delegation/personal-todo/event-delivery PostgreSQL, seven
Windows native-process cases, mounted-filesystem/MinIO integration and explicitly
retired test scenarios. The six integration skips require disposable PostgreSQL.
These were not silently treated as passes. No PostgreSQL executable was found on
PATH, and no new database/container/credentials were provisioned.

The initial workflow compile prerequisites were subsequently cloned from the official
CrosswiredStudios public repositories; neither credentials nor services were needed
to run their safe fixture tests. These source checkouts remain unchanged:

- CSweet.Agent.ChiefOfStaff
- CSweet.Agent.SoftwareProductManager
- CSweet.Agent.TechnicalDirector.VideoGame
- CSweet.Agent.QA.VideoGame
- CSweet.Agent.Director.VideoGame
- CSweet.Agent.CreativeDirector.VideoGame

Some broad unit failures also require sibling profile source fixtures. These are
outside the four-repository integration and failed on the original baseline run.
The follow-up workflow results above use the exact official source revisions below;
the complete core unit suite was not repeated after adding sibling sources. Actual deployed workload identity, a full
PostgreSQL concurrency/migration run and live provider authentication remain rollout
checks. The synthetic browser harness is not a credentialed production-auth test.

## Reproduction and evidence

Workspace `verification/` contains full TRX results and copied logs for feature and
baseline runs, plus final scoped results. `verification/logs/realty-repeat-*.log`
records all three timing repetitions per checkout. `acceptance-evidence/` contains
three native screenshots. SDK 5.6.0 remains in `packages/`.

Run the full unit/integration projects with `dotnet test -m:1 --nologo`,
`UseLocalCSweetAgentSdk=false`, `RestorePackagesPath=<workspace>/verified-nuget`,
`RestoreAdditionalProjectSources=<workspace>/packages`, `--logger trx` and a short
physical TMPDIR. Clear external database variables. The final affected filter and
original output are recorded in the scoped log/TRX. Full baseline comparisons used
the unchanged published SDK pin, not patched source references.

See [security review](realtyleads-security-review.md) for the two source hardenings
and [isolated rollout plan](realtyleads-development-rollout.md) for specific ports,
storage, prerequisites and approvals. No rollout is executed or authorized by these
reports.

## Full-unit failure classification

- `CSweet.UnitTests.AgentConfigurationVisibilityTests.ConfigurationPages_UseSharedVisibilityEvaluation` — reproduced on unchanged baseline.
- `CSweet.UnitTests.AgentMemoryServiceTests.ConversationLifecycleCaptureRejectsIneligibleContextBeforeWriting(changed: "kind")` — reproduced on unchanged baseline.
- `CSweet.UnitTests.AgentMemoryServiceTests.ConversationLifecycleCaptureRejectsIneligibleContextBeforeWriting(changed: "nonhuman")` — reproduced on unchanged baseline.
- `CSweet.UnitTests.AgentWorkspaceBrokerTests.Canonical_stage_workspace_requires_current_attempt_repository_and_action(operation: "snapshot", scenario: "valid")` — reproduced on unchanged baseline.
- `CSweet.UnitTests.AgentWorkspaceBrokerTests.OfflineBrokerPublishesRealGitAndLfsThenMergesExactCommit` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ApprovalDashboardServiceTests.DeliveredOpenIncidentIsSeparateAttentionAndDisappearsOnlyAfterRecovery` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ComputeGuestResponderTests.Guest_reports_actual_platform_and_runtime_readiness(acceptingWork: False)` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ComputeGuestResponderTests.Guest_reports_actual_platform_and_runtime_readiness(acceptingWork: True)` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ComputeGuestResponderTests.Runtime_failure_or_shutdown_never_emits_a_ready_response` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ComputeMaintenanceDeliveryWorkerTests.Empty_queue_recovers_new_evidence_from_a_hint_or_timer(hint: False)` — timing failure; three reruns pass on feature and baseline.
- `CSweet.UnitTests.ComputeMaintenanceHostTests.Missing_delivery_certificate_leaves_host_and_local_lease_recovery_running(enableIntake: False)` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ComputeMaintenanceHostTests.Missing_delivery_certificate_leaves_host_and_local_lease_recovery_running(enableIntake: True)` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ComputeProviderCapacityTests.Concurrent_environment_claims_cannot_overbook_one_node` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ComputeReplayJournalTests.Background_read_waits_through_long_effect_and_remains_cancellable` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ComputeReplayJournalTests.Separate_journal_instances_serialize_the_entire_effect_and_duplicate_observes` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ComputeResultOutboxTests.Concurrent_journals_allocate_distinct_sequences_with_only_one_execution_claim` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ComputeResultSupervisorTests.Executor_wakes_result_delivery_and_supervisor_joins_cancelled_delivery` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedEnrollmentRefreshCannotRestoreInvalidAuthority(invalidation: "expired")` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedEnrollmentRefreshCannotRestoreInvalidAuthority(invalidation: "failed")` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedEnrollmentRefreshCannotRestoreInvalidAuthority(invalidation: "revoked")` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedLocalSetup_ActiveExistingOfficeBlocksReconnectButAllowsConfirmedRemoval` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedLocalSetup_AlreadyRemovedOfficeContinuesDirectlyIntoInstallation` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedLocalSetup_CanStartBeforeWindowsPackageIsPublished` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedLocalSetup_CleanExistingOfficeRequiresBoundReconnectAndIssuesFreshSetupReceipt` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedLocalSetup_DevelopmentLauncherUsesLoopbackBootstrapOriginButKeepsHttpsOfficeOrigin(hasLocalSource: False)` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedLocalSetup_DevelopmentLauncherUsesLoopbackBootstrapOriginButKeepsHttpsOfficeOrigin(hasLocalSource: True)` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedLocalSetup_ReconcilesExactEnrollmentClaimStrandedByLauncher` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedLocalSetup_RefreshesHandoffWithoutCreatingAnotherSession` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedLocalSetup_RejectsOfficeThatDropsSelectedAllocation` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedLocalSetup_RemovalFailureBecomesRetryableAndRehydratesLegacyFailedSession` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedLocalSetup_UnredeemedHandoffExpiresAfterFiveMinutes` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.AssistedLocalSetup_WaitsForAuthenticatedHeartbeatBeforeReportingReady` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionFleetServiceTests.SelectedStoragePersistsAndReachesAuthenticatedInstallerHandoff` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ExecutionNodeCertificateAuthorityTests.Issue_BindsP256KeyToNodeIdentityAndDropsRequestedExtensions` — reproduced on unchanged baseline.
- `CSweet.UnitTests.GenericStudioArchitectureTests.CoreImplementationContainsNoVideoGameOrEngineBranches` — reproduced on unchanged baseline.
- `CSweet.UnitTests.HierarchicalDeliverySchemaTests.EveryAffectedAgentAcceptsRealV2OutcomesAndPublishesVersionMatchedNotes` — reproduced on unchanged baseline.
- `CSweet.UnitTests.IcuMessageFormatterTests.Every_supported_culture_ships_resources_for_every_area` — reproduced on unchanged baseline.
- `CSweet.UnitTests.InternalGitClientTests.NativeLfsLocksPersistAcrossTokensAndRequireExplicitForceForAnotherOwner` — reproduced on unchanged baseline.
- `CSweet.UnitTests.InternalGitClientTests.NativeLfsUploadsAssetAndDownloadsIntoAnotherClone` — reproduced on unchanged baseline.
- `CSweet.UnitTests.InternalGitClientTests.NativeLfsVerificationBlocksAnotherUsersLockedAssetPush` — reproduced on unchanged baseline.
- `CSweet.UnitTests.OrganizationDeletionTests.PurgeClassification_CoversIndirectRestrictiveDependents` — reproduced on unchanged baseline.
- `CSweet.UnitTests.ProjectDeliverySetupTests.Published_manager_profile_has_a_valid_bounded_execution_graph` — reproduced on unchanged baseline.
- `CSweet.UnitTests.RemoteMcpAccountRepositoryTests.RelationalRevisionFenceRejectsStaleTrackedWriter` — new test fixture corrected; final scoped rerun passes.
- `CSweet.UnitTests.WorkManagementCapabilityHandlerTests.ApprovedManagerSetupGrantsCanConfigureItsRealDeliveryProfileThroughTheBroker` — reproduced on unchanged baseline.

## Official sibling checkout revisions

- `CSweet.Agent.ChiefOfStaff`: `2965fce0f32f33af2b362b4645d2472f8165098a` (source unchanged).
- `CSweet.Agent.SoftwareProductManager`: `e8764700eb7a05779f6fe418fdc3260456581153` (source unchanged).
- `CSweet.Agent.TechnicalDirector.VideoGame`: `3d6dd68a617826d174f205de04eb3224942d0bdb` (source unchanged).
- `CSweet.Agent.QA.VideoGame`: `78f7e637d63604bd5e3f28522b85b66aefb00d19` (source unchanged).
- `CSweet.Agent.Director.VideoGame`: `20325ebdb67a2ab37b8cf41dcd041d807f2c7296` (source unchanged).
- `CSweet.Agent.CreativeDirector.VideoGame`: `c118cfc8e7b47dd61ff5b3401a7193815e87f402` (source unchanged).

## Release reconciliation (2026-10-27)

SDK is based on upstream 5.5.4, including the private HTTP logging and claim-poll deadline fixes. SDK 5.6.0 passed 617 SDK tests, 2 sample tests, pack and 7 generated-template tests. Its GitHub CI passed at `a7ee5ebfd436dc97d2046c3bbff1ade5f38dca8b`. A fresh package cache validates the provider manifest; older local prototype caches with the same unpublished version must not be reused. Broker/probe suite: 47 passed. Core includes upstream `3cef951b` retry-history fix. A new complete safe regression rerun is in progress; the earlier baseline comparison above remains the established evidence, not a claim that every core test passes.

Release rerun: core UnitTests completed with 5,810 passed, 46 failed and 544 skipped (6,400 total). Three additional compute-supervisor timing failures require isolated reruns; other failures match the prior comparison. Browser rerun needs serial rebuild after concurrent MSBuild nodes exited; the earlier passing browser evidence is not presented as a pass for this revision. No production service was involved.


### Completed release verification follow-up

The serial fresh-cache harness rebuild and browser run now pass. The native page
and authenticated endpoints completed connect → explicit grant → actual typed
agent read plus a synthetic planned conversation → revoke → both denied. The
provider fixture returns an empty result, and the conversation correctly reports
that without inventing leads. No real inference or provider credentials were used.
Owner isolation, mutation-header/size checks, cleared password input and no key
readback also pass. The harness was stopped after the run. Focused native-account
and connector tests against the fresh build: 16 passed.

Delivery workflow: 11 passed, 6 database-dependent skips. Game-role workflow:
3 passed. Both processes completed successfully. Integration: 98 passed, the same
baseline setup-step-count failure, 6 skips.

Exact full unit delta from the earlier 44 failures to 46:

- Removed: `RemoteMcpAccountRepositoryTests.RelationalRevisionFenceRejectsStaleTrackedWriter`;
  its relational fixture correction passes.
- Added: `ComputeResultSupervisorTests.Result_lane_failure_does_not_stop_lease_cleanup_or_maintenance_delivery`;
  reproduced on unchanged baseline and integration in isolated comparison.
- Added: `ComputeRecoverySupervisorTests.Shutdown_joins_inflight_recovery_and_retains_pending_evidence`;
  reproduced on unchanged baseline and integration (cancellation/journal-lock race).
- Added: `ComputeIntakeSupervisorTests.Intake_failure_does_not_stop_expired_workload_cleanup`;
  full-run failure was an IOException on the fixture journal lock during shutdown.
  Serial isolated reruns pass on both baseline and integration. The compute code
  and these test sources are unchanged by this integration. This remains an
  intermittent test issue, not a claimed deterministic baseline reproduction.

Thus 43 previously failing cases remain (42 established baseline failures plus
the earlier compute timing case), one scoped fixture failure is fixed, and three
compute timing failures were observed. No remaining scoped regression was found.

GitHub verification at the published draft revisions: SDK CI passed; core's
47 broker/probe tests passed. Core native/memory jobs, plugin validation and agent
checks stop at NU1102 because SDK 5.6.0 has not yet been published (nearest 5.5.4).
These checks need rerunning after the separately approved SDK release; they are
not reported as passing. The consumer manifest correction and installed-host
acceptance remain separate gates.
