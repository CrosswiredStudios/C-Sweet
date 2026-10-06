# Branch ownership, recovery and clean installation

Read the [normative delivery specification](hierarchical-delivery.md) first. This guide describes the trusted implementation and safe operator actions for a new database.

## Branch and candidate ownership

`WorkDeliveryBranchBinding` records a repository, scope, source and integration target. Tasks publish only to their assigned story; stories promote to a shared release or configured epic; isolated epics promote to the release; releases promote to the repository's recorded default branch. `HierarchicalProjectDelivery.PrepareAsync` derives stable branch names and bootstrap keys from project and scoped epic identities, excludes epics already accepted by completed releases, and retains required children, including cancelled work, until the manager explicitly amends scope.

`CanonicalWorkspaceAuthorization`, `AgentWorkspaceBroker` and `GitWorkspaceCapabilityHandler` use the assigned target during preparation, refresh, publication and conflict recovery. `TaskIntegrationWorkActionExecutor` checks current sprint authorization, pinned staffing, independent Technical Review and exact source/target evidence before invoking a trusted operation. Authors receive no provider credentials or unrestricted merge API.

`InternalGitRepositoryStore.DeliveryBranchAsync` supports `ensure`, `inspect`, `candidate`, `receipt` and `promote`. Candidates preserve both parents; promotion verifies both refs and atomically writes the target and durable Git receipt. Managed refs reject direct publication, deletion and smart HTTP bypass. Provider protections remain in force. The GitHub adapter currently returns an actionable block because it cannot guarantee both atomic ref comparison and orchestration-only managed branch writes; it performs no unsafe substitute operation.

## Recovery

1. Read the current plan, candidate, findings and repository receipts in Delivery → Releases, or use `PlatformWorkClient.ReadDeliveryPlansAsync` and `ReadDeliveryEvidenceAsync`. Wake events are hints and never execution grants.
2. Repair a provider outage and use `RecoverAsync` with the current execution revision and a durable idempotency key. Completed repository receipts remain complete; only unfinished promotions resume. A native Git receipt recovers a successful promotion whose database response was lost. Successful merges are never automatically reverted.
3. When validation fails, the assigned manager authorizes linked remediation tasks through approved scope. Each fix runs under a sprint, receives independent review where code is changed, and receives QA. Resolution requires QA completed after the finding was recorded, followed by fresh aggregate validation.
4. To change unpromoted scope or topology, pause and amend the plan. `ConfigureAsync` increments its scope revision, cancels superseded inbox attempts and revokes temporary document grants. Activate the new revision only after staffing, criteria and repository checks pass. A changed candidate invalidates prior acceptance.
5. A release with successful repository promotions has immutable accepted content. Recover unchanged unfinished operations; use a follow-up release for changes to already promoted content. Cancellation preserves Git history and recorded evidence.

`WorkDeliveryService.SaveDeliveryAsync` converts competing receipt/outbox insert races and serialization conflicts into revision conflicts. Re-read authorized state before retrying. `WorkDeliveryMutationReceipt`, `WorkTaskIntegrationReceipt`, `WorkDeliveryPromotion` and provider receipts provide effect-specific replay protection. Plan transitions, audit and notification outboxes are persisted atomically.

## New installation

1. Choose a new PostgreSQL database explicitly. Configure its connection through the existing application's database configuration; for Aspire development the database name is `CSweet:Postgres:Database`. Keep existing databases and volumes intact. This implementation does not migrate running executions or delete an existing database.
2. Apply the normal EF migration chain, including `HierarchicalDeliveryPlans` and `HierarchicalDeliveryFindings`. `CSweetDbContextModelSnapshot` matches the clean schema. The dedicated PostgreSQL tests create only randomly named `delivery_test_*` databases on a loopback server and remove only their own test database.
3. Restore the verified local packages from `artifacts/hierarchical-packages`. Contracts are 3.25.0; SDK is 3.59.0. Use the `CSweet.Agent.Sdk` checkout for source development. Package-consumer validation explicitly disables sibling references. Import the final version-matched agent manifests using the normal installation process.
4. Select software-delivery profile revision 4, video-game-production revision 6, or lightweight game-manager-brief revision 3 for new projects. Historical profiles and V1 contracts remain available. Assign the project manager, author, independent QA and technical reviewers; game code releases also need certified candidate build staffing and recipe bindings.
5. Create board-local task → story → epic hierarchy, approve planning, configure release scope/topology, finalize task assignments and activate the plan. Start task sprints explicitly. Story, epic and release validation can continue across completed task sprints. Release-manager acceptance authorizes trusted promotion, without granting deployment or public-release approval.

## Verification map

- `HierarchicalTaskRuntimeTests.RunJourney`: actual shared task runtime, exact human evidence, integrated QA failure and reviewed fix, repository-free artifact QA and task-only sprint completion.
- `HierarchicalDeliveryPostgresTests`: clean migrations, all four game/software × code/document journeys through release, contended revision/outbox writes and transactional rollback.
- `HierarchicalDeliveryServiceTests`: cross-board/repository access, aggregate independence, candidate changes, scope amendment, temporary document grants, lost database receipts and partial promotion recovery.
- `HierarchicalGitDeliveryTests`: real local Git, shared release and optional epic topology, managed-ref protection, stale refs, rework and receipt replay.
- `GitHubAppClientTests`: provider guarantees block safely without external writes.
- `HierarchicalDeliverySchemaTests`: typed broker schemas, real V2 outcome schemas and version-matched agent release notes.
- `ProjectsWorkspaceTests`: current candidate identities, delivery status and absence of standalone Merge columns.
- `scripts/verify-hierarchical-agents.py`: 19 package-only consumer suites, manifest/self-tests and local package creation.
- `scripts/verify-hierarchical-package-metadata.py`: `.nuspec` versions/dependencies against final manifests and release-note headings.
- `scripts/verify-hierarchical-platform-packages.py`: platform and Git host builds with all sibling references disabled, plus restored-asset checks.

Final results and package versions are recorded in [verified results and package handoff](hierarchical-delivery-validation.md).

The local feed is a handoff artifact. Publication, deployment and database deletion require separate work and are not performed by this implementation.
