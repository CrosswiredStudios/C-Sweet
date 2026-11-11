# Declared verification and QA compute-preparation waits

## What happened (Super Awesome Games, Prism Break, 2026-11-10)

- **#22 went back to engineering although its code was approved.** The plan's browser rule ("Browser-visible increments require early real Chromium launch and retained screenshot…") existed only as constraint prose copied onto every ticket. The engineer's agent bound its passing browser receipt only when the acceptance criteria contained words like "visible" or "canvas"; #22's did not. The technical reviewer read the constraint and rejected the ticket for missing browser evidence. The engineer then committed screenshots and a custom verifier to the game repository to get through review.
- **#20 was failed back to engineering three times with no defect found.** QA's browser test runs in project compute, and Linux compute preparation was not ready. The tool returned an error, the model wrote a failed report, and the ticket returned to the engineer, who could not fix it. Every later ticket in the six-ticket chain waited.

## Behaviour now

### One declaration per ticket

`WorkItemPlanningSpecification.Verifications` (contracts 5.4.0) records the evidence a ticket's candidate must carry:

- `null`: not declared (planned before 5.4.0). Agents keep their previous rules.
- `[]`: planning declared that no special verification is required.
- `[{ kind: "browser-smoke", requireCanvas, requireWebGl, viewportWidths }]`: real Chromium evidence is required.

The flow:

1. The Technical Director's task index declares `browserVerification` (`canvas`, `page` or `none`) on every freshly generated task (Technical Director 6.2.0).
2. The Producer records it as `Verifications` when it creates the ticket (Producer 6.6.0).
3. The platform rejects malformed declarations on create and revise (`WorkManagementCapabilityHandler.ValidateDeclaredVerifications`). The tool schemas already accept any planning object, so no schema change was needed.
4. The engineer requires and binds the receipt from the declaration (Engineer 4.2.0). The receipt is the standalone command `node .csweet/browser-smoke.cjs <build directory>` plus `--canvas` (`WorkItemVerificationKinds.BrowserSmokeReceiptCommand`).
5. Technical review (SDK `BranchDiffTechnicalReview`) checks it in code before any model call. A missing receipt is rejected with the exact command. A bound receipt, or a declared none, tells the reviewer not to raise browser evidence.
6. QA requires its own browser test, canvas, widths and WebGL from the declaration (QA 5.6.0).

### QA waits for compute preparation instead of failing

QA returns outcome `awaiting-compute-preparation` (`WorkOrchestrator.ComputePreparationWait`) with `{ reason, retryAt }` when browser evidence is required and the project's browser compute is not prepared. A failed QA browser environment still blocks for a person to recover. `retryAt` must fall within 15 minutes.

- **Sprint orchestrator** (`WorkOrchestrator.AcceptComputePreparationWait`): the stage moves to `Backoff` with `RetryAt`, `LastSummary` set to the reason and no `LastError`. The item execution returns to `Pending`, and a `qa.compute-preparation.waiting` event is recorded. The attempt does not count toward `maximumAttempts` (`RetryAttemptCount`, `IsComputeWait`), and the board shows "Waiting for browser compute preparation".
- **Delivery-plan runtime** (`WorkDeliveryService.Runtime`): the execution enters `WaitingForEvidence` with the reason as `BlockedReason`, and a new review is requested once `retryAt` passes.
- **Early wake**: when a local compute installation completes as Ready, `ComputeLocalSetupWorker.WakeComputePreparationWaitsAsync` sets `RetryAt = now` for that organization's waiting QA stages. It runs once per completed installation, so it cannot cause a dispatch loop.

The wait never reaches a verdict and never routes the ticket to its author. QA checks readiness before staging a build and before any model call, so a recheck spends no model tokens.

## Release order

Contracts 5.4.0 → platform → SDK (next release; contains the `BranchDiffTechnicalReview` gate) → Technical Director 6.2.0, Producer 6.6.0, Engineer 4.2.0, QA 5.6.0. An older platform rejects `awaiting-compute-preparation`.

## Not covered

- The Software Developer agent keeps its own browser heuristics; software plans do not declare verification yet.
- The compute-preparation coupling to compiling the whole unit-test project (`claude/linux-compute-preparation-blocked-by-test-compile-error-2026-11-10.md`) is a separate fix.
- Existing tickets are not backfilled.

Validation: not yet run. Tests: `ComputePreparationWaitTests` (platform); per-repository tests are listed in each release note.
