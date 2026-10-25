# Chief quality improvement: implementation evidence

Recorded October 23, 2026. The original correctness increment is retained below; the
subsequent leadership candidate and default-model regression fix follow it. This records
implementation and verification. The final closure increment below also records publication;
it does not claim an installation update, completed acceptance gates, or complete implementation
of the six-phase plan. Continue using
[the implementation plan](chief-of-staff-quality-improvement.md).

## Source and release inputs

| Input | Recorded value |
|---|---|
| C-Sweet starting HEAD | `0ceb176334bf684d8c0cef7cc851d88653dd9dfd` |
| Chief starting HEAD | `1642bb86fcb8c827605fced4a68dfe109ccc4314` |
| Chief manifest/implementation/package | `2.13.5` → `2.13.6` |
| Chief shipping dependencies | SDK `4.1.1`; Memory and Memory.MicrosoftAgentFramework `0.3.0` |
| Actual sibling SDK | `../CSweet.Agent.Sdk`, HEAD `6dc8d90a3cb47961a07e43d514a0e345f4136dd2`, version `5.3.1` |
| Sibling memory | HEAD `064b2133625010cea1a73231d8a72a3a27103b86` (not used for shipping verification) |
| Platform package verification | SDK `5.3.1`; WorkManagement.Contracts `5.1.0`; Office.Contracts `0.11.0`; Memory `0.5.0` and Memory.Broker `0.5.1` |
| Provider/model | Deterministic test clients; production provider/model and live grant snapshot not captured |

Pre-existing changes to `Directory.Packages.props`, the implementation index, and the
planning-prerequisite handoff document were preserved. No SDK/shared contract package was
changed. The shipping verification explicitly disables sibling references; source SDK
integration is a separate run.

## Implemented behavior and code map

| Package | Implemented changes | Evidence |
|---|---|---|
| C10 | `ChiefOfStaffAgent.HandleManagerEventAsync` no longer calls a prose-triggered recommendation builder; the builder is deleted. `HiringTurnTools` guards staffing proposals, recommendation changes and hiring tools. The same turn guard suppresses deterministic candidate selection after `ask_user`. | `ChiefCorrectnessTests.OrdinaryDiscussionCreatesNoStaffingOrSetup`, `QuestionToolAlsoPreventsDeterministicCandidateSelection`, `HiringTurnToolsTests` |
| C11 | `HiringService.WithdrawRecommendationCoreAsync` and `RecordRecommendationFulfillmentAsync` call `WorkItemMutationEngine.SettleHiringRecommendationAsync`. Source state, card settlement, lifecycle activities, successor availability and realtime outbox commit together. Cancellation clears claims/waits; fulfillment requires all seats. Installation-scoped backlog reads repair up to 100 proven terminal source-linked orphans, including an empty pending backlog. | `HiringExecutionCardTests.cs` (partial `PersonalTodoServiceTests`); PostgreSQL rollback and stale-writer tests in `HiringSuggestionPostgresTests.cs` |
| C11 concurrency | `CoreConfigurations.ConfigureWorkTask` makes `WorkTask.Revision` a database concurrency token, with the model snapshot synchronized. This is metadata over the existing column and needs no schema change. A real PostgreSQL test initially reproduced stale completion overwriting cancellation, then passed with concurrency protection. | `ConcurrentLateWorkerCannotOverwriteWithdrawnHiringCard`; migration compatibility and PostgreSQL personal-work checks |
| C12 | Authenticated hiring answer capture precedes preference-change routing. `IsHiringPolicyChangeRequest` distinguishes explicit instructions from informational questions. `ReadCurrentUserMessage` retains precedence. Response prose never starts setup; rejected stale answers neither reopen setup nor claim a save. PM delivery finishes before final turn commit. | `ChiefCorrectnessTests`, `OperatingProfileOnboardingTests.StaleHiringAnswerDoesNotReopenWizardOrClaimASave` |
| C13 (partial) | User-message conversation/business/installation/turn IDs are checked before effects; host cancellation propagates through fact extraction. Product consultations use message or turn identity and reject missing correlation. Approved removal follows a bounded, same-requester/team source lineage. Delayed approval notifications re-read current status and ignore superseded requests. | `ApprovedRemovalOnlyWithdrawsItsOwnTeamAndRequestLineage`, `StaleApprovedEventDoesNotReconcileSupersededRequest`, malformed/current-message regressions |
| C14 (partial audit) | Removed mention, management-status and resource-need subscriptions whose callbacks previously returned without handling them. They are no longer advertised as supported intake. No working report/mention workflow was replaced; durable intake remains work to implement. | Manifest regression in `CalendarManifestTests`; release notes explain compatibility |
| C15 | `ChiefOfStaffAgent.ProductContext.cs` fingerprints meaningful brief/business/finance/organization content, ignoring delivery/snapshot timestamps. It saves a pending request before delivery and acknowledgment afterwards, with revision checks. Recovery replays the exact request and source identity, allowing existing receiver message/comment keys to deduplicate. Attention review discovers pending delivery for current authorized PM peers. Removed the unused extra `Plan` invocation; context-update retains follow-through ownership. | `UnchangedPmContextIsNotRedeliveredAfterAnotherEventOrRestart`, `LostDeliveryAcknowledgmentRetriesOriginalRequestWithoutRepeatingEffect`, `FinanceOnlyAndWorkforceOnlyChangesAreDelivered`, `PmDeliveryIsAcknowledgedBeforeFinalReply` |

No historical recommendation was blanket-deleted. Canonical hiring cards are matched by
recommendation identity, organization, installation, personal board ownership and executable
status; audit cards and another installation's cards are excluded. Approved organizational
plans without full hiring fulfillment cannot complete execution cards.

## Verification

The original sandboxed VSTest discovery and smoke runs stalled before test execution while
waiting for a local test child-process connection. `TestResults/runner.log` in the Chief
checkout retains the diagnostic. A bounded run outside that sandbox succeeded, establishing
an initial baseline of **97 passing Chief tests**. This is a runner/environment failure,
not a passing or failing application-test result.

Executed checks:

- Chief Release suite with `UseLocalCSweetAgentSdk=false` and `UseLocalCSweetMemory=false`:
  **126 passed**, no skips. Results: Chief `tests/CSweet.Agents.ChiefOfStaff.Tests/TestResults/release-correctness.trx`.
- Separate sibling-SDK integration: **126 passed**, no skips, using
  `UseLocalCSweetAgentSdk=true`, memory/packages pinned, and isolated
  `publish/sibling-integration` output.
- Affected platform hiring/suggestion/personal-work/resource-change/canonical-work/migration
  selection: **322 passed, 38 skipped**. Skips were opt-in suites without their individual
  test-environment settings plus an existing disabled board test; they do not count as passes.
- Dedicated PostgreSQL selection: **16 passed**, no skips. Covers the new source/card/outbox
  rollback and stale-writer tests, plus personal-work claims and fresh migration/concurrency
  compatibility. A final focused rerun after recovery-order changes also passed **10 tests**.
- Platform builds/tests use `--artifacts-path .artifacts/chief-quality` and disable SDK, memory,
  work contracts, Office contracts and isolation sibling references. Normal output directories
  are locked by the running development app; tests do not restart or update that app.
- PostgreSQL uses a separate temporary `postgres:17-alpine` container on loopback and unique
  per-test databases, never the running business database. Test databases/container are removed.
- Linux x64 publish, NuGet pack, bundle pack/verification and implementation self-test succeed.
  Manifest, both profile identities, project version, release-note filename/heading and
  inspected `.nupkg` version all agree on `2.13.6`.
- `git diff --check` passes. Existing unrelated nullable/analyzer warnings remain in platform builds.

Platform TRX evidence is retained under `.artifacts/chief-quality/results`, including the
initial failing PostgreSQL race reproduction. Chief output is under its ignored `publish/`
directory: `CSweet.Agents.ChiefOfStaff-2.13.6-linux-x64.csab` and
`packages/CSweet.Agents.ChiefOfStaff.2.13.6.nupkg`. Bundle SHA-256:
`96949204e08adc03211c2f0c521c14b0525f36a04eb3017fa402e5f2d434b45c`.

## Leadership candidate: Chief 2.14.0 and SDK 5.4.0

The unpublished 2.13.6 candidate was superseded by 2.14.0. Manifest, both implementation
identities, project/package version and `releases/2.14.0.md` agree. SDK 5.4.0 is an additive
typed leadership surface over protocol v2. Chief now explicitly opts into sibling SDK/memory
source only for integration checks; shipping uses SDK 5.4.0 and memory 0.3.0 packages. Main
C-Sweet's SDK pin is 5.4.0. No shared WorkManagement/Office contract or memory package changed.

| Work | Implementation and authoritative boundary |
|---|---|
| C20/C21 | `ChiefLeadershipService`, `ChiefLeadershipAgenda`, `ChiefFocusRecord`, `ChiefCoverageRecord`, `ChiefFocusPrompt` and `ChiefLeadershipReceipt` persist focus, assessments, purpose-bound immutable answers and replay receipts. Organization locks, revision checks, payload digests and authenticated current-Chief checks protect writes. Broker grants derive organization/installation from the session. |
| C22 | Onboarding uses a native focus decision with stable option IDs. Native answers survive restart and missed callbacks. A current explicit `Focus on <configured-domain-id>` turn can initialize and capture the agenda without asking again. Custom answers remain saved, blocked data; focus capture creates no hire. |
| C23/C24 | `WorkItemMutationEngine.ProjectChiefAssessmentAsync` maintains canonical source-linked cards, reuses stable legacy coverage correlations, and repairs projections on reads. Existing operating-state keys are preserved. Legacy answers bind through native decision identity rather than prose. Replacement Chiefs receive new card projections while organization history persists; predecessor writes/delegation are fenced. |
| C30–C33 | `ChiefOfStaffAgent.Leadership.cs` performs one bounded typed interpretation per eligible callback, with at most two JSON attempts. Core rejects unsupported identities, stale inputs and CEO-title-only coverage. It creates either a supported outcome, one correlated native question, a durable block/review, or one linked proactive recommendation. Hiring still uses the existing policy, finance and delegation pipeline. General and Game Studio are the first gated profiles; other existing profiles retain onboarding. |
| C34–C36 | Read reconciliation observes withdrawal/fulfillment, preserves hiring across focus changes and rechecks actual coverage after fulfillment. Owner acceptance and deferral are explicit native outcomes. External engagement remains unverified. Due reviews and relevant evidence changes requeue bounded work during recovery. A saved recommendation's stable delivery intent/ack survives notification failure. Old profile obligations cannot silently disappear into a new profile. |
| Rollout | `ChiefLeadershipOptions.Enabled` binds `CSweet:ChiefLeadership:Enabled` and defaults false. Owner pause/resume instructions preserve commitments. The additive migrations refuse destructive rollback when leadership history exists. Deploy Core/AgentHost and grant support before installing the requiring Chief. |

New migrations are `AddChiefLeadershipState` and `AddChiefLeadershipOrganizationIntegrity`.
Their PostgreSQL tests exercise empty upgrade, source/card/outbox rollback, immutable competing
native answers, duplicate capture and refusal to drop retained state. Model snapshots match.
Concurrent live-generation telemetry work in the shared checkout was preserved and is outside
this implementation's scope.

Candidate verification:

- SDK solution: **560 SDK tests and 2 sample tests passed**. Template generation from the packed
  SDK: **7 tests passed**, plus its self-test. The `.nupkg` metadata is 5.4.0.
- Chief: **133 tests passed** against the packed SDK with sibling references disabled; a
  separate explicit source-SDK run also passed **133 tests**. Linux publish and NuGet pack succeed.
- Final affected-platform package selection: **507 passed, 36 skipped**. Skips are unrelated
  opt-in suites without their environment variables; the new leadership and default-provider
  PostgreSQL tests ran against a disposable local container.
- Real provider HTTP endpoint selection: **21 passed**, including HTTP 409 before selecting
  a replacement and successful deletion after explicit replacement.
- Final focused default-model/hiring/UI recovery selection: **159 passed**, including durable
  Chief attention invalidation after changing the default. Final leadership selection:
  **52 passed**, including responsibility-to-role guards and actual Game Studio coverage.
  A final current-owner command selection passed **6 tests** after rejecting historical
  completed command turns while preserving immutable native-answer recovery.
- The initial broad selection also picked up an unrelated workspace fixture failure:
  `AgentWorkspaceBrokerTests.Canonical_stage_workspace_requires_current_attempt_repository_and_action`
  (`snapshot`, `valid`) reports unavailable business storage and fails in isolation. It remains
  recorded in `unrelated-workspace.trx`; workspace tests were excluded from the final affected
  workflow selection. This is not represented as a passing full-platform suite.

Current results are under `.artifacts/chief-leadership/results`; Chief and SDK TRX files remain
in their test projects' ignored `TestResults` directories. The final Chief bundle is
`publish/CSweet.Agents.ChiefOfStaff-2.14.0-linux-x64.csab`, SHA-256
`b40b178c3ddc0544c57b0957a6aa1a3f9934f711cfe20cd1805cd0032bb68dad`.

## Reported automatic-hiring regression and default model settings

Read-only inspection of the supplied Swift Games example confirmed a pending Creative Director
recommendation with broad automatic delegation. The Chief's instance override pointed to the
enabled Local vLLM model, while the candidate's approved definition retained a deleted provider.
`ConfigureDelegatedHireAsync` previously treated the presence of those stale keys as usable
configuration and therefore never copied the working pair. No live configuration or hiring
record was patched to bypass the failure.

The fix validates candidate provider availability and the model pair. Under broad delegation,
unusable defaults use `SystemConfiguration.DefaultChatProviderId` plus that provider's saved
`DefaultChatModel`; older installations without a usable default may use the Chief's current
validated pair. Valid candidate defaults and already-frozen snapshots are preserved. Required
configuration, grants and spending still validate before any hire.

`LlmProviders.razor` exposes the default pair and an explicit replacement picker. Deleting or
disabling the default requires another enabled, tested selection if any other profile exists.
Provider mutations serialize in PostgreSQL, and rejected deletion leaves secrets untouched.
Selecting/changing the default durably invalidates pending Chiefs' attention schedules in the
same transaction, so recovery rechecks the current policy and recommendation. A blocked hire
projects `AutomaticHiringBlockReason` from its source-linked audit card; Overview and
Communications show the reason and a shared Review hiring action instead of implying progress.
Regression fixtures cover deleted/disabled/empty defaults, valid-default preservation, narrower
delegation, secret exclusion, system-default selection, replacement guards and deletion races.

## Correctness closure and published acceptance candidate

Chief **2.14.1-rc.2**, commit `97f80f72381f95b8598f1621e65cdd13f64c9d65`, supersedes the
unpublished 2.14.0 candidate. [Chief PR 1](https://github.com/CrosswiredStudios/CSweet.Agent.ChiefOfStaff/pull/1)
contains the source; [the published prerelease](https://github.com/CrosswiredStudios/CSweet.Agent.ChiefOfStaff/releases/tag/v2.14.1-rc.2)
is explicitly excluded from latest stable promotion. GitHub Actions run `40062594996` succeeded.
The published Linux bundle digest is
`d060fa6b59f292cbbbbcc392aff7482bbb03f1785ac242e9f3258641d4d364bb`.
The separately verified local precommit bundle digest is
`a305ef18e5c1e5c6322113fbfd215468b7599654dc65ee9f71719a9d513a7123`;
these are distinct build artifacts and are not interchangeable provenance claims.

SDK **5.4.0**, commit `322c4fd9de591aa66f79ee55603a57b7bae1f44f`, is
[published](https://github.com/CrosswiredStudios/CSweet.Agent.Sdk/releases/tag/v5.4.0).
Its CI and trusted NuGet publishing runs `40060849732` and `40060852028` succeeded.

- **C13:** `ChiefOfStaffAgent.ResourceRecovery.cs` classifies missing source/identity/reporting
  data, excessive lineage, denied access, validation, approval and budget blocks into retained
  source-correlated review work. A review task re-reads current source and cannot complete
  from a model claim. Stale revisions stop; unknown defects, temporary unavailability and
  cancellation propagate. Recovery isolates permanent failures per request and repairs an
  interrupted card resolution. Malformed user-message JSON is ignored before effects;
  capability cancellation is no longer swallowed. RC.2 also retains a discovery review when read access is lost before any request identity is known; restoring access re-runs bounded discovery before settling that review.
- **C14:** [the event inventory](chief-of-staff-event-inventory.md) maps every advertised
  subscription through the actual SDK or Chief entrypoint. Calendar reminders now re-read the
  current authorized occurrence and deliver an idempotent owner reminder. Canceled/moved events,
  foreign recipients and instructions inside event text cannot trigger work. Removed unsupported
  manager-report/resource-need/mention intake remains Phase 4 work, rather than an acknowledged no-op.
- **Leadership conflicts:** Core and `ChiefLeadershipCapabilityHandler` distinguish optimistic
  concurrency/preemption from invalid coverage. The Chief stops stale interpretations without
  converting an owner's new focus or pause into an evidence block.
- **Target-model correction:** the initial local Qwen run exposed an unsupported CEO owner ID
  attached to a useful legal question. The interpretation policy now leaves unknown ownership
  empty and clears unused role fields outside Hire. A deterministic regression covers the exact
  shape; the final real-model fixtures assert the corrected ownership and next action.

Final retained verification:

| Check | Result and scope |
|---|---|
| Chief deterministic suite | **150 passed**, no skips; `correctness-final.trx`, pinned packages and sibling references disabled |
| Local target model | **4 passed**, no skips, `qwen3.8-flash-next` at the configured local endpoint; synthetic fixtures only; `target-model-final.trx` |
| Isolated companion platform | **300 passed**, no skips, including PostgreSQL, leadership conflict, source/card settlement, DI, hiring policy/default models and provider checks; `isolated-platform.trx` |
| Real provider HTTP endpoints | **21 passed**, no skips; default-provider replacement and HTTP 409 deletion guard; `isolated-provider-http.trx` |
| Cross-component workflow | **7 passed**, no skips, both shared and isolated checkouts; `workflow-final.trx` and `isolated-workflow.trx` |
| SDK foundation | **560 SDK + 2 sample + 7 generated-template tests passed**; NuGet version inspected as 5.4.0 |
| Chief packaging | Linux publish, NuGet pack/version inspection, `.csab` verification and implementation self-test pass at 2.14.1-rc.2 |

The seven workflow checks join the actual Chief to `ChiefLeadershipCapabilityHandler`,
`PersonalTodoCapabilityHandler`, `PluginOperationsCapabilityHandler`, `CommunicationHubCapabilityHandler`
and real Core services. They cover native focus, immutable owner acceptance/deferral/external choice,
missed answer callbacks, restart, one hiring recommendation, interruption after real notification
effects before acknowledgment, fulfillment reassessment and continuation. Two scenarios use
disposable PostgreSQL databases. The real Product Manager receiver is recreated and invoked twice
with the retained request; the actual communication service stores exactly one CEO message and
one reporting conversation. That receiver fixture has no subordinate team and does not claim
installed cross-process or architect/comment delivery acceptance.

The isolated companion checkout excludes concurrent generation telemetry, plugin-bundle import,
work-description and game-planning changes. It is at
`C:/Users/PC/.codex/worktrees/chief-quality/csweet`; its output uses `ChiefVerified`.
`CSweet.DeliveryWorkflow.Tests` accepts `CSweetSourceRepositoriesRoot` to select sibling agent
checkouts explicitly. All shipping dependency checks disable sibling SDK/memory/shared-contract
references. Initial SDK verification with a custom artifacts root failed the sample's repository
discovery; the normal-layout run passed. That layout failure was not reported as a product failure.

Final target-model baseline (four assessment interpretations, **not** the complete C03 journey baseline):

| Scenario | Model calls | Wall time | Accepted next action |
|---|---:|---:|---|
| CEO title alone | 1 | 35.8 s | One ownership question |
| Existing Product Manager | 1 | 62.8 s | Adequately covered, no hire |
| Existing Game Studio creative ownership | 1 | 160.1 s | Adequately covered, no conflicting hire |
| Licensed legal need | 1 | 87.4 s | Human/external coverage question, no agent hire |

These four model evaluations exercised the final interpretation policy in RC.1; that implementation and prompt are unchanged in RC.2, which adds only discovery recovery and release metadata.

Provider-reported usage was **4,299 input / 5,274 output tokens**. Queueing and concurrent local
work affect these timings; do not treat them as production latency or a cost estimate.

## Remaining gates and next work

- **G0 remains open:** capture per-journey target-model/grant traces and the C03 ordinary-question,
  onboarding, replacement-hiring and attention-recovery call/latency baseline. The deterministic
  test timings are not a production latency baseline. Retain a missing-focus-transition
  reproduction alongside the new focus implementation.
- **G1 installed acceptance remains open:** C13/C14 implementation, runtime inventory and real
  receiver/platform retry checks now pass. Complete installed conversation evaluations and normal
  UI removal/replacement verification. The four local assessment evaluations do not replace those journeys.
- **G2/G3 installed acceptance remains open:** the candidate is published and deterministic
  persistence/broker/package scenarios pass. Run the published installed-prerelease candidate
  with the target model in a disposable business, including the normal UI journeys, before
  accepting G3 or enabling proactive execution. Exhaustive crash-boundary, all-profile,
  reassignment/custom-focus clarification and production cost/latency evaluations remain.
- **Broader Phases 4 and 5 remain behind G3:** durable manager/project reporting, typed generic
  task outcomes, fair approved-plan recovery beyond one page, measured callback efficiency,
  workflow-service decomposition and prompt/localization work remain. Minimal typed SDK adoption
  and explicit dependency selection needed by this candidate are included; no broad shell rewrite
  was started ahead of the required checkpoint.

Deployment still follows published releases and Installed Agents. Deploy the companion platform
schema/capability and correctness changes before Chief 2.14.1-rc.2; do not patch stored manifests,
digests or runtime payloads. SDK and Chief publication is complete. No installation update,
live-business change or broader proactive rollout was performed. The normal UI still shows Chief
2.13.5. AgentHost was rebuilt and restarted with the final broker conflict fix for acceptance.
The published RC.2 preview was blocked by the existing global setting, "Imported agents are disabled
in global runtime settings." The owner explicitly declined enabling it and offered to stop/update
the existing Chief. No alternative import, installation, definition update or global-setting change
was performed. A Previewed package record is not an approval or installation.

The temporary AgentHost development configuration enabling Chief leadership was removed from both
source and Debug output, restoring the prior absent-file configuration. Browser control disconnected
before a second controlled AgentHost restart could be verified; the running process may retain cached
options until the next restart. Restart AgentHost as part of the next controlled acceptance session.
Do not infer that an installed Chief update alone enables the default-off leadership gate.

The preview also exposed a release-selection defect: an explicit prerelease tag still consulted only
the latest stable release. `IGitHubAgentRepositoryClient.GetReleaseByTagAsync`,
`GitHubAgentRepositoryClient.ReadReleaseAsync` and
`AgentImportPreviewService.ResolvePrebuiltReleaseAsync` now select the explicitly requested published
candidate bundle. Default/branch previews still reject prereleases, drafts remain rejected, and
manifest-version matching remains required. This source fix does not alter runtime approval policy.
The isolated published-dependency preview/client/definition regression run passed **65 tests**, with
zero failures or skips (`tests/CSweet.UnitTests/TestResults/explicit-release-preview.trx`). It includes
exact-candidate selection ahead of latest stable, draft/ref/version rejection and escaped tag routes.

**G3 remains open.** Resume installed/UI acceptance only through the normal Installed Agents flow,
after the owner-controlled update and a controlled test-gate session. Active unrelated QFN Games
work must remain outside the disposable acceptance business. Broader refactoring remains deferred.

## October 24: normal onboarding for every business

The installed RC.2 Chief accepted the Creative & Product focus, persisted the native answer and
made its coverage row Ready, but performed no assessment or hiring recommendation. The real broker
snapshot returned `proactiveEnabled: false`, no next assessment, and a platform-rollout block. Its
callback completed normally; the confirmation incorrectly promised that assessment would continue.

The owner rejected the proposed rollout/cohort restriction. This supersedes earlier default-off,
General/Game-Studio-only and bounded-cohort statements in this historical evidence document:

- `ChiefLeadershipService` no longer accepts rollout options. `ChiefLeadershipOptions`, its DI
  registration, the profile execution restriction and the business allowlist are removed.
  `SnapshotAsync` offers the next eligible assessment for every supported operating profile.
- `AssessAsync` accepts the appropriate CEO-direct primary-offering manager recommendation across
  all seven profiles. Existing manager ownership must be assessed before another company-level hire.
  Licensed-human, evidence, authority, owner-pause and one-unresolved-recommendation checks remain.
- Owner pause/resume immediately reprojects the visible assessment card. A paused Ready assessment
  displays its actual block; resume clears that execution block while retaining the original focus.
- Chief RC.3 added truthful focus/decision confirmations and a current-state read after idempotent
  command receipts. RC.4 documents normal execution for all businesses and retains that correction.
  Published RC.3 notes remain historical. RC.4's interpretation policy is unchanged from RC.2.
- The temporary business-only development configuration was removed. Global imported-agent settings,
  approved packages, installed payloads, source decisions and live hiring records were not changed.
  Rebuild/restart the platform to load this correction; an agent-only update cannot remove an old
  platform's execution gate. Existing saved focus is recovered through normal durable discovery.

Verification uses published dependencies with sibling references disabled:

| Verification | Final result |
|---|---|
| Chief deterministic regressions | 156 passed, zero failed/skipped (`unrestricted-leadership.trx`) |
| Isolated Chief platform and DI checks, including real PostgreSQL | 70 passed, zero failed/skipped (`chief-unrestricted-onboarding.trx`) |
| Real Chief/broker workflow journeys, including Creative Director and PostgreSQL fulfillment/restart | 8 passed, zero failed/skipped (`unrestricted-director-workflow.trx`) |
| Chief RC.4 Linux bundle and implementation self-test | Passed |
| Chief NuGet metadata | `CSweet.Agents.ChiefOfStaff`, `2.14.1-rc.4` |

The isolated platform checkout aligns with the current platform's published SDK 5.5.0 pin. Chief
ships against compatible SDK 5.4.0. An initial historical-checkout integration run exposed a sibling
Technical Director dependency downgrade; another root restore encountered a concurrent unpublished
Office 0.12.0 pin. Final isolated runs avoid those unrelated changes and pass. An initial new
workflow fixture queried a local assessment inside an EF predicate; extracting its ID fixed the
fixture translation failure before the final successful PostgreSQL run.

The full installed/model/UI product acceptance checkpoint remains open for the owner's test.
It is a verification checkpoint, not a runtime feature gate. Broader refactoring remains deferred.
