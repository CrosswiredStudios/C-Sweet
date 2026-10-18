# Unattended QA tool and evidence recovery

`GameQaHarness.CreateOptions` in the QA agent sets `FileAccessProviderOptions.DisableWriteTools`. Generic file reads remain available; generic write/edit/delete tools are absent. The confined command runner retains its existing approved authority. `GameQaPlan.CreateTool` writes only `.csweet/qa-test-plan.md`; `GameQaReport.CreateTool` writes only the fixed structured report. Both paths reject symlink ancestors. A stale request for the removed generic writer receives ordinary tool-error feedback rather than an interactive approval request.

`GameQaJournal` records observed command results before the tool returns, plus test plans and validated report drafts. Records are scoped to organization, work item/stage, accepted planning, scope/assignment revisions, and a digest of the exact original source files. Observations are split into bounded state records with a content digest and bounded index; `read_qa_evidence` provides paged recovery without truncating the retained output. These state mutations use the platform's existing operating-state/outbox service. A report is checkpointed only after current source-integrity and input-authorization checks. Recovery rematerializes and reauthorizes source and validates current criteria before using a retained report. The first valid submission is immutable within its harness; a later pass claim cannot overwrite an earlier failed report.

`WorkOrchestrator.QaRecoveryEvidenceAsync` supports recovery of legacy attempts that never reached structured submission. A human owner or board manager may retain verified audit observations in an exact `video-game.qa-recovered-evidence.v1` document revision. Saving this type retains evidence with no review or approval request. Dispatch uses the last retained revision, even when a newer draft exists; original legacy submitted revisions remain compatible. Dispatch verifies its human revision, organization/project/team/work-item scope, source attempt and protected assignment, immutable source commit, model-call audit binding, content hash and input budget. It copies only those historical observations into the assignment. This grants no artifact browsing, repository authority, task completion, QA pass or merge approval. Old planning is identified as historical data; current acceptance criteria still govern a new verdict.

The ticket 19 failure occurred when QA requested `file_access_write` for `qa-test-plan-ee126f60.md` after executing its tests. The source snapshot and archive identity checks had succeeded; the remaining harness exposed an interactive writer in an unattended workflow. Thirteen original shell results were recovered from a verified audit receipt and retained separately from any QA verdict.

Validation: QA `GameQaUnattendedTests` and `GameQaReportTests` exercise actual harness tool exposure, stale-tool recovery, plan/report submission, journal recovery, oversized output paging, source/planning changes and immutable first submissions. Platform `QaRecoveryEvidenceTests` covers source/audit/content mismatches, erased input, human authority and retained observations across planning amendments. QA 4.0.5 was packaged and installed through the normal definition/artifact service; 104 QA and 71 focused platform tests passed.

`GameQaJournal.ReadResumeReportAsync` automatically recovers a validated draft only within its original attempt, for lost-reply delivery recovery. A new manager-requested attempt receives historical observations and reports but reassesses the current environment. Its first validated report becomes that attempt's immutable draft. The journal also exposes reports written by 4.0.4, whose index kept the report separately from command observations.

Browser/device checks and accepted branch requirements require actual evidence or an explicit scope amendment. A tool-recovery fix never implies those checks passed.

Legacy human-created documents can have absent project/team fields. Recovery then derives scope from the exact origin task and protected source assignment, including its project, board and input team. Explicit mismatched bindings remain excluded; `QaRecoveryEvidenceTests` exercises the normal human artifact creation/submission path as well as legacy and cross-project cases.

An invalid, unavailable or erased recovery source is not dispatched as evidence. A bounded warning instead requires fresh checks, so malformed historical input cannot cause a pending-stage dispatch loop or silently become a pass.

Live verification on 2026-10-17: the first resumed ticket `VGCD352DE8BC-19` completed its agent attempt, saved its plan and structured report without tool approval, and passed every executed build/Node/HTTP check. The orchestrator correctly retained a decision-required blocker for three unverified accepted criteria: browser/file-URL loading, on-screen rendering, and feature-branch naming. The original 13 command results and the resumed report remain retained; no passing completion artifact was fabricated.

The owner subsequently approved the prepared scaffold-scope amendment with tracked release QA. Deferred browser/file-URL, visual/audio, compatibility/accessibility/touch and hardware performance checks are recorded in backlog task `VGCD352DE8BC-23`, under a separate release-QA milestone/feature outside the frozen sprint hierarchy. `WorkDeliveryService.AmendTaskAsync` advanced ticket 19 to planning revision 3, assignment revision 2 and plan scope revision 2, retaining staffing, dependencies and source identities. The new live QA dispatch includes the exact retained audit observations.

`WorkDeliveryService.ControlAsync` permits a scoped organization owner to pause or resume a plan for amendment while preserving the assigned release manager. Activation and cancellation remain manager-only. `HierarchicalDeliveryServiceTests.ScopedOwnerCanPauseAndResumeManagerPlanButOtherContributorsCannot` verifies owner attribution and rejects other granted contributors and owner cancellation; existing project/board authorization and revision checks still apply. This avoids attributing the owner's approved amendment to an agent manager.

The amended QA attempt `eb2576e9-fa04-4944-aaf6-b894deb90b75` executed fresh checks against `ee126f6031296d1c3f47fc9184489b498832fb1f` and passed all six current criteria. It submitted artifact `a8d9b6b1-5003-4061-87b8-a10c3e78f1fe`, revision `a8d94580-e499-4be7-aabf-f779b0edf2c9`, SHA-256 `0148d3ad4802cbb4969f6d40b06aeeb55095f2fac8d6bd849e8ea69eacda3243`. The hash was independently verified through the human artifact read service. The report links deferred release QA to ticket 23 and explicitly flags `CONTRIBUTING.md`'s `feature/*` convention versus the existing managed Story branch; it grants no release readiness or main promotion. Gabriel then claimed independent artifact acceptance for ticket 19.

Final live verification: Gabriel's independent task-quality attempt `20154291-5bd3-40de-9f87-9356b8a2a0a6` returned `passed` for that exact artifact revision and SHA. Ticket 19 reached `Completed/done` at 2026-10-17 23:44:57 UTC. Ticket 20 unblocked automatically: Victor claimed work item `a5c6e3d9-6525-4287-ab40-43844a3444a4`, attempt `55df5e27-be3e-444f-8c51-d81c5d445308`, and began Technical Director inference at 23:45:03 UTC. This verifies actual downstream execution rather than merely a Ready card. Independent task-quality acceptance does not alter the document library's separate submitted/in-review state or constitute release/main approval.

The legacy evidence document originally remained in the owner's approvals inbox because
retaining observations reused ordinary document submission. `ArtifactEvidenceRetention`, the
human/agent document services, the approvals projection and the review worker now distinguish
saved evidence from a decision. Startup repair dismisses legacy evidence approvals while
preserving their original submitted revisions for compatibility. Ordinary document approval
cards describe the decision and its effect; evidence previews state that no action is needed
and put original logs behind a disclosure.

The owner-requested live cleanup used `IArtifactService.UpdateAsync` to set only the approval
requirement to `NotRequired`. Verification compared all 26,755 characters, stored and computed
SHA-256 digests, revision identities/states, submitted and accepted revision pointers, source
task binding and approval history before and after. No approval decision was created, and
the resulting approvals projection contained no pending items. This cleanup does not require
restarting active agent work. `ArtifactDocumentServiceTests`, `QaRecoveryEvidenceTests`,
`DocumentApprovalRenderingTests`, `CollaborationReviewAuthorityTests` and
`ArtifactReviewJobWorkerTests` cover retention, recovery, legacy cleanup and presentation.

Validation of the evidence/approval change: 110 focused tests passed. The additional existing
`ApprovalDashboardServiceTests.DeliveredOpenIncidentIsSeparateAttentionAndDisappearsOnlyAfterRecovery`
test failed at its project-incident assertion; the same failure reproduced against the older
`ArtifactInputVerified` assembly, which predates this change. The evidence-specific tests passed.
