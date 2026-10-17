# Saved outline digest and repeated recovery

## Confirmed failure

Victor 4.2.1 completed Breakout Resonance's full outline, both detail pages and all six
coverage pages at 01:08:52 Pacific on October 17. The original planning collaboration
`ede56c8b-a1cc-434e-9b01-46bd111a9fd6` completed at 01:09:30. Gabriel 3.2.2 then rejected
the exact saved outline because `ProgressiveScopePlan` omitted `DependencyPins`, while
Victor's retained `PlanningOutput` serialized that empty ledger. No scope had changed.

Replaying the actual retained proposal gives:

- Victor's retained digest: `aeaffe869fb47a73eec6dbd550bd60f9b044b5a19e0229cacb15716af9b21fa0`.
- Producer's omitted-field digest: `ae2bf07ad7605472440b8f77d108663ee423ffe3ddccbdcc364da8387c25e250`.
- Including the empty ledger reproduces Victor's exact retained digest.

The progressive error lacked `PlanningRecoveryMarker`. Routine attention discovery
therefore requeued the unchanged blocked commitment. Repeated evidence revisions refreshed
manager reports. The platform did not recognize the legacy digest error as a technical issue,
and `UpdatePresentationAsync` used the incoming owner-input flag rather than the normalized
report's flag. The normalized technical report also exceeded the SDK's 25-word next-step
bound, making later report validation fail. The incident accumulated 55 historical messages.
Compute had independently reached Ready.

## Corrections and regression coverage

Producer 3.2.3 `ProgressivePlanning.ProgressiveScopePlan` retains the empty pin ledger in
the exact digest projection. Hash verification still rejects modified scope and mismatched
inputs. Terminal progressive validation and refinement errors carry the existing recovery
marker, so `EnsureCommitmentAsync` does not requeue the same unchanged failure on attention
wakes. Technical validation errors retain the diagnostic and management escalation marker.
Explicit authorized retry remains available after repair.

`Fixtures/technical-director-4.2.1-outline.json` in the Producer tests was generated through
Victor's actual `GenerateProgressivePlanningAsync` saved-outline delivery path using its
contract example and no model calls. It prevents both sides of a test from constructing the
same incorrect digest. Tests also replay the actual retained live proposal when supplied
through `CSWEET_COMPLETED_OUTLINE_CAPTURE`, reject scope tampering and exercise eight
attention passes without requeueing a terminal failure.

Platform `ProjectHealthService.TechnicalPlanning` recognizes the exact historical digest
failure as a technical issue. Its normalized report satisfies the SDK format bounds.
`ProjectHealthService.UpdatePresentationAsync` routes using the normalized report's
`RequiresOwnerInput` value. Regression coverage submits eight changing evidence revisions
with an incoming owner-input request, verifies valid normalized informational reports,
retains CEO routing/deadlines and historical messages, cancels obsolete decision requests,
and verifies no repeated status reports.

Producer's 373 tests, self-test, NuGet package and Linux bundle verification passed against
published SDK dependencies. Platform's 118 health tests and three PostgreSQL health
integration tests passed without skips (494 tests in total). The saved
scope, approvals, source artifacts and original coordination transcript are preserved.

## Local recovery

Gabriel's existing installation was upgraded through `AgentDefinitionService.UpdateAsync`
to 3.2.3 after verifying that manifest authority was unchanged. The local development bundle
SHA-256 is `949e30bbb6339ecfaac778b2b19678a6341afc640e03fdec3017fb261cf0b00d`;
the retained source snapshot SHA-256 is
`0fa0f9a3d151f212addfc54f4fe3b276e4129215e508eb4f2923633c0df2cd80`.
These artifacts are local; no remote release is claimed.

The platform's tested infrastructure/broker binaries were installed with rollback copies.
The existing commitment `54326cf1-be46-4248-9c84-25954891c2e6` was requeued through
`IPersonalTodoService.RequeueAsync`. Victor's completed collaboration was not resumed or
regenerated. At 07:57:39 Pacific, Gabriel published six Epics and six Stories and opened
refinement session `8dd4e992-ae72-4c6c-b090-5928ac012aaf` for `story-project-scaffold`.
The existing report became informational and the historical message count remained 55.

## Live acceptance

At 08:07:41 Pacific, Victor's Story refinement completed after saving its seven-task
index and all three brief pages. Gabriel published eight executable tickets at
08:07:44–45: the seven proposed tasks plus the exact Phaser dependency-pin prerequisite.
Their canonical identifiers are `VGCD352DE8BC-13` through `VGCD352DE8BC-20`. Every ticket
retains refinement session `8dd4e992-ae72-4c6c-b090-5928ac012aaf` and proposal artifact
digest `B6791D6348B7161CEA8E5669A0592918B09453C9C700D427565E17C634E9AC4A`.
The six Epics and six Stories remain intact.

All eight tickets entered Production Sprint 1 (`3c119de8-6013-4cbd-85c3-088bcb283ce1`).
Naomi answered the three correlated manager planning decisions using `continue-current-plan`;
the decision-change notification requeued Gabriel automatically. This did not invent
ship/composer dates or modify the accepted brief. Engineering, QA and Technical Director
estimate sessions completed. At 08:12:23 Pacific all eight tickets had recorded estimates
totaling 38 points, and Gabriel opened QA sprint-readiness session
`c29e859a-5fc0-447f-8cdb-d976b8f4cb74`.

At 08:13:23 Pacific, the main planning commitment was Running without a blocker,
the estimation/readiness commitment was Ready, and QA readiness was Summarizing.
The sprint remained Planned; execution start and final preflight were not yet confirmed.
The historical digest incident remained informational (`requiresOwnerInput=false`), with
55 messages and report revision 89 unchanged throughout this verification. Read-only
canonical database verification is retained locally in
`.artifacts/stall-diagnosis/digest-live-acceptance.txt`.
