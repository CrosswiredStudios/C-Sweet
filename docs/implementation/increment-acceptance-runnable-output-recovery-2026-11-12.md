# Increment acceptance with nothing runnable in scope

## Confirmed failure

QFN Games (Prism Breaker), 2026-11-12. Production Sprint 1 held three Code tasks: a Phaser dependency pin, its
verification, and a technical-path document. All three declared `planning.verifications = []` (explicitly no browser
evidence). The increment's Release-scope acceptance starts with build readiness as a compute test. `DeliveryComputeTest`
(SDK) requires a runnable HTTP app: `package.json` + lockfile + a `build` script, a root `Dockerfile`, or a
dependency-free `index.html`. It rejected the candidate with "The candidate needs a root Dockerfile compatible with
available cached images, or a dependency-free static index.html. Ask the Developer to supply a runnable test package."

The Producer's correction coordinator then asked its model which task should fix that. The model correctly answered
`NeedsPlanning`. The wait reason embedded the model's wording, which changed every call, so each
`CoordinateCorrectionAsync` wait write bumped the correction revision, emitted `work-delivery.changed`, woke the Producer,
and asked the model again, about every 45 seconds. Nobody owned the next step, and Sprint 2 was never populated.

## Correction (platform)

- **`WorkDeliveryService.RunnableOutputInScopeAsync`**: collects the Code tasks under an execution's scope (through
  ticket-workflow child scopes) and returns false only when every one explicitly declares no browser evidence
  (`WorkItemVerifications.IsDeclared` and no `BrowserSmoke`). Undeclared (older) tasks count as runnable.
- **`ValidateReadinessAsync`**: an approved compute-test readiness result with no compute tests and no builds is
  accepted only when `RunnableOutputInScopeAsync` is false. The platform derives this from the tasks' own planning, not
  from the agent's claim.
- **`RecoverAsync`**: new recognised case, `IsNoRunnableOutputRejectionAsync`. The execution is blocked at build
  readiness, every open finding is this execution's exact `NoRunnablePackage` message, and nothing runnable is in scope.
  In that case the release manager or an owner may recover without remediation tasks: the findings become
  `Superseded`, with evidence that the request was outside the declared scope and passes no criterion, the matching
  correction becomes `Completed`, and validation restarts from build readiness.
- **`CoordinateCorrectionAsync`**: a wait write whose reason equals the saved one, on a `NeedsPlanning` correction,
  returns the current plan without bumping the revision or emitting an event.

## Correction (SDK 5.23.0)

- Build readiness completes as approved, with "No test application to start", when the candidate has no runnable entry
  point and nothing runnable is in scope.
- The correction coordinator calls `work.delivery.recover.v1` for a "no runnable test package" correction when nothing
  runnable is in scope.
- Multi-source selections are keyed on a stable fingerprint (correction, findings, exact source revisions). The model is
  asked once per fingerprint. On `NeedsPlanning` the owner gets one decision
  (`work-delivery.correction-source.v1`): fix it in a named completed task, or keep the increment waiting.

Agents need SDK 5.23.0: Producer 6.6.4 (corrections) and Technical Director 6.2.2 (build readiness).

## Verification

`DeliveryComputeGenerationTests`:

- `Readiness_without_a_test_link_is_accepted_only_when_every_code_task_declared_no_browser_evidence`: accepted when
  declared none, rejected when undeclared.
- `Manager_reruns_acceptance_after_a_no_runnable_package_rejection_only_when_nothing_runnable_was_declared`: findings
  superseded, correction completed, back at build readiness; refused when undeclared.
- `An_unchanged_correction_wait_is_not_written_again`.

All 83 unit-test classes touching delivery, the inbox or the gateway: 1,304 passed. The 2 failures are in code this
change doesn't touch (sibling agent repositories; `DataProtectionKeyRingTests`).

## Recovery for QFN Games

Install this AgentHost, then publish SDK 5.23.0 and update the Producer and the Technical Director. The Producer's next
review re-runs acceptance of the Sprint 1 increment by itself. If acceptance stays blocked, the owner can retry the
"Increment … acceptance" ticket.
