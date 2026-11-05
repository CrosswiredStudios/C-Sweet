# Recovery after an executor update

`WorkOrchestrator.RecoverUpdatedExecutorsAsync` runs during normal sprint reconciliation.
It discovers stopped current stages from durable state, so an offline host or missed update
notification does not lose the recovery opportunity. It does not introduce an agent polling loop.

Recovery requires an active sprint, a stopped current ticket/stage,
and a known infrastructure failure recognized by `IsPackageRecoverable`. The same assigned
installation must be enabled, active and ready. Configuration must be Current, or PendingNextStart with no active runtime so normal dispatch starts the updated package. Its installed
package must be newer than the actual version recorded in `AgentRunLog` for the failed work,
and installed after that attempt stopped. Missing or malformed version telemetry fails closed.
The recognized errors are retryable QA transport failures, the QA operation-generation mismatch and the legacy exact MCP 500
specialist-execution outcome. Product findings, permission failures and approvals do not qualify.

The recovery leaves source, assignment revision, planning, completed stages, attempts and
retained evidence intact. It clears the stopped card's claim and wait state, then records
`stage.executor-update.recovery` once per stage, failed attempt and installed package.
`WorkOrchestrationBoardState.SaveBoardChangesAsync` commits state, event and board notification
outbox together. Normal dispatch and inbox claiming still recheck current project authority,
assignment, grants and capacity. An update is a recovery condition, never a QA verdict.

`ExecutorUpdateRecoveryTests` cover version, tenant, lifecycle, cancellation, retry-budget,
product-finding and idempotency boundaries. `QaBrowserComputeWaitTests` retain the exact
operation generation, owner, source and build fences. `WorkDispatchRecoveryTests` and
`WorkOrchestrationRetryTests` verify normal dispatch and completion still work.

## Prism Break rollout

1. Publish and install QA **5.4.11**, Producer **6.2.4** and Director **0.4.4** through
   published releases and Installed Agents. GameEngineer **4.1.15** and companion Software
   Developer **2.1.14** contain the browser-command receipt correction and release-source guard.
   Restart C-Sweet using the updated platform source/build for executor-update recovery.
2. Re-read Sprint 2 and ticket **VGD7DFCCB217-38**. The expected recovery boundary is the existing
   quality stage `96a6a783-89b3-4c37-8738-4830b285d663`, with implementation and technical review
   completed. Preserve integrated Story commit `00671086190bb795266490748832d7007a0cc855`.
   Confirm the recovery event and a new QA attempt on the installed version. If telemetry is
   insufficient for automatic recovery, use the existing ticket Retry action after verifying
   the QA update. Do not reopen implementation or manufacture a quality pass.
3. QA's journal and browser state bind to the same source and planning across attempts.
   `GameQaInteractionTesting` resumes the corrected pending script; completed exports remain
   evidence rather than new waits. Check the unresolved scene-lifecycle assertion independently.
   A prior test's closure-variable error is not a product defect or a passing assertion.
4. After QA has an accepted verdict, the Producer should inspect existing follow-on tickets
   **39** and **40** before starting them. They overlap native Chromium QA and technical review.
   Use the normal manager planning amendment to retain any distinct acceptance criteria and
   link exact-commit evidence. Do not delete history, silently mark checks passed, or create
   replacement planning/review tickets. Existing Technical Director progressive-planning rules
   already prevent new review-only siblings for the same candidate.

Live verification on 2026-11-05 confirmed `stage.executor-update.recovery` at 18:21:56 UTC,
recording QA 5.4.10 -> 5.4.11 and retained assignment revision 1. The actual QA inference
records report 5.4.11. The existing quality stage resumed without reopening implementation,
technical review or integration. Operations 39-43 completed without infrastructure failures.
Generation 40 retained a failed pixel-sampling assertion; QA corrected that test after
observing caption glyphs over brick centers. Generation 43's retained result has 16 satisfied
checks, no errors, and the same integrated source/build. The final independent criterion
report is still pending; successful script checks are not a completed ticket verdict.
The owner published and installed Producer 6.2.4 and Director 0.4.4, then restarted the
platform. After cold startup, the interrupted reporting lease expired and the normal bounded
retry path dispatched fresh QA work. At 18:37:45 UTC, Rohan's replacement runtime claimed
work `0af2b9ad-a707-4ef8-b908-cf85853d0b1d`; the existing quality stage is Running with
no error and no retry timer. The Producer is Running too. Final QA acceptance remains pending.
Source changes and local packages do not themselves update installed agents.

The 5.5.0 QA follow-up uses one resumable build-transfer operation; existing completed browser and behavioral results retain their source/build/generation fences.

At 18:42:34 UTC the replacement QA attempt failed with `runtime.transport` while preparing its report. The retained 16-check result remains evidence, with no accepted ticket verdict. A verified newer installed executor may supply one recovery even when prior infrastructure failures exhausted the ordinary retry budget. Recovery is still idempotent per failed attempt/package, and a failure on the same installed version does not qualify again. Product findings and permission errors remain excluded.

The required follow-up rollout is SDK 5.10.0, QA 5.5.0, matching core and trusted compute provider code. Use published releases and Installed Agents for QA; source builds are verification, not an installed-agent update.
