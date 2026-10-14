# Single execution contract

## Decision

This pre-customer development cutover replaces both execution generations with
`WorkManagementCapabilityNames.ExecutionRun` (`work.execution.run`) and
`WorkExecutionAssignment`. WorkManagement.Contracts and Agent SDK are both 4.0.0.
Package major versions communicate the breaking API change; they do not introduce
parallel runtime execution protocols.

The Breakout incident combined a Producer requirement for V1 with SDK planning
that additionally required V2. A Creative Director advertising only V2 could not
satisfy that conjunction. The SDK now adds the same canonical capability already
used by Producer planning, manifests, the catalog and platform authorization.
Exact role, skills, assignment principal and independent-review checks remain.

## Implementation map

- Contracts `WorkDeliveryContracts.WorkExecutionAssignment` owns Task, Story,
  Epic and Release envelopes. `RequireTaskScope` and checked task identity
  accessors validate real sprint authority without converting the envelope.
- SDK `CSweetAgentBase.ExecuteCapabilityAsync` validates scope and dispatches
  the original task payload to `ExecuteCapabilityCoreAsync`; aggregate work goes
  to `ExecuteDeliveryScopeAsync`. Principal, planning revision, permitted outcomes,
  delivery-plan identity and exact candidate survive dispatch.
- SDK `HierarchicalProjectDelivery.PrepareAsync` and the agent planning producers
  require the same capability. There are no V1/V2 aliases or automatic adapters.
- Platform `WorkOrchestrator.DispatchAsync` constructs the canonical task
  assignment, including ordinary sprint work without a delivery plan.
  `WorkOrchestrator.DispatchRecovery` checks task scope and authoritative identities
  before adopting detached work. `ProjectWorkPolicy` authorizes the canonical
  envelope by scope. `WorkDeliveryService` uses it for aggregate dispatch too.
- Each affected agent's manifest retains one scope-aware capability schema.
  Implementations, package pins, version assertions and release notes agree.

## Package set

| Package / agent | Version |
| --- | --- |
| WorkManagement.Contracts; Agent SDK | 4.0.0 |
| Video Game Engineer | 4.0.0 |
| Video Game Creative Director; Software Developer; Software QA | 2.0.0 |
| Software Architect | 1.0.0 |
| Video Game Producer; Software Product Manager | 3.0.0 |
| Art Director; Artist; Audio Designer; Build Release Engineer | 3.0.0 |
| Game Designer; Level Designer; Narrative Designer; Playtest Researcher | 3.0.0 |
| Video Game QA; Technical Artist; Technical Director; UI/UX Accessibility Designer | 3.0.0 |

The local verified feed is `artifacts/execution-contract/packages`. Package metadata
and dependencies are recorded in `artifacts/execution-contract/package-verification.json`.
Those build artifacts are local outputs, not published releases.

## Regression coverage

SDK `CanonicalExecutionDispatchTests` covers full envelope preservation for all four
scopes and rejects missing/unknown scope and aggregate sprint authority.
`SingleQaStaffingTests.CreativeDirectorWithOnlyCanonicalExecutionCanAuthorWithoutDuplicateHiring`
isolates the original staffing conjunction using a roster fixture; it does not
claim to execute the production Creative Director callback.

Verification uses packaged dependencies with `UseLocalCSweetAgentSdk=false` and
`UseLocalCSweetWorkManagementContracts=false`, supplied through
`RestoreAdditionalProjectSources`. Contracts tests, SDK tests and sample tests,
all 19 agent test suites and self-tests, platform orchestration/delivery/assignment
tests, and a freshly generated standalone SDK-template agent pass. Every changed
package is packed and its `.nuspec` version/dependency pins inspected. Logs are in
`artifacts/execution-contract`. PostgreSQL integration tests require their dedicated
test-database configuration and are skipped in the ordinary unit run.

Final counts: Contracts 46; SDK 393 plus 2 sample tests; agents 1,399 plus 19
self-tests; generated template 7 plus its self-test. Focused platform checks:
143 passed, 2 PostgreSQL tests skipped. The broader platform run: 4,258 passed,
512 skipped, 3 failures in the separate Personal Assistant/onboarding work already
present in this checkout: `OrganizationDeletionTests.PurgeClassification_CoversIndirectRestrictiveDependents`
(new Personal Assistant dependent),
`EnterpriseWorkspaceTests.CreationValidatesNameAndPreservesHiringHandoff(true)`
(creation-error UI), and
`GenericStudioArchitectureTests.CoreImplementationContainsNoVideoGameOrEngineBranches`
(`PersonalAssistant/RepositoryBusinessAnalysisService`). These were left intact.

## Cutover and live acceptance

Pause/drain active work, publish Contracts then SDK then the affected agents, and
upgrade the platform and installed executing agents together. The distinct new
capability prevents an old package from accidentally matching a new assignment.
Re-plan unfinished staffing and recreate unfinished execution through normal,
authorized recovery using current revisions. Preserve completed evidence; do not
rewrite historical outcomes, manufacture grants, or replay old queued envelopes
under the new capability name.

This change does not upgrade the running Office or revive the observed project.
The [incident report](../../breakout-startup-stall-2026-10-13.md) records the separate
live Office profile/certification mismatch, startup retry churn and misleading
health alerts. A compatible live development environment is still required.

The Creative Director also has a pre-existing manifest/handler mismatch:
`VideoGameCreativeDirectorAgent.ExecuteCapabilityCoreAsync` handles vision and
management check-ins, but does not implement task execution or override aggregate
execution. Its advertised capability alone is not proof of delivery readiness.
Before reusing the observed level-spec assignment, assign that work to a suitable
implemented design specialist or implement and verify the missing callback.

The next-run acceptance gate remains a fresh install with the released package
set and actual connected Office: hire once, plan, estimate, activate, execute a
real task and independently review the exact delivered revision. Unit success
does not establish that live acceptance result.
