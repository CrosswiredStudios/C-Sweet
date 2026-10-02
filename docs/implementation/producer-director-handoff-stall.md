# Producer ↔ Creative Director handoff stall (2026-10-02)

## Evidence

Business "Super Awesome Games": Naomi Chen (Creative Director 1.13.0) had an accepted Tetris pitch and an
approved staffing request (`39a8a7be…`). Gabriel Reyes (Producer 2.14.0) was hired at 00:06 UTC and sent his
kickoff DM. Naomi's turn failed ("The agent couldn't complete that request"). Her
`add_personal_todo` call for "Prepare and share project documentation with the Producer" returned `Conflict`:
"The requested operation requires an element of type 'String', but the target element has type 'Null'."
Every five-minute project review repeated the same failure (12+ times) while the review card still read
"Project reconciliation completed". Gabriel's reviews found an empty portfolio and returned silently.

## Root cause

`CSweetDbContext.CaptureProjectHealthSignals` (`CSweetDbContext.ProjectHealthCapture.cs`) runs inside
`SaveChangesAsync`. `HealthProjectForTask` called `JsonElement.TryGetGuid` on
`PersonalWorkContextJson`'s `workstreamId`, which is JSON `null` for unscoped work. `TryGetGuid` throws
`InvalidOperationException` for non-string values, so any personal task created with a work context failed
to save. `CaptureIncidentFailure` had the same pattern for `incidentId`.

## Fixes

- Host: `ReadGuidProperty` checks the value kind before `TryGetGuid`, and each health signal is captured in
  isolation so advisory capture can never veto the business mutation. Tests:
  `ProjectHealthTests.OptionalGuidReadsNeverThrow`, `PersonalWorkWithNullContextScopesStillSaves`.
- Creative Director 1.14.0: project reviews name the next step (`CreativeDirectorNextStep`), never report a
  failed reconciliation as completed, record consecutive failures (`creative-reconcile-stall:<conversation>`)
  and DM the CEO once after three. A kickoff whose handoff task cannot be queued is answered with the next
  step instead of a failed turn. Pitch-brief replies tolerate fenced or trailing provider text.
- Producer 2.15.0: the kickoff names the handoff the Producer needs, and `producer-handoff-watch` makes an
  empty-portfolio review follow up with the manager (each follow-up is also a Director kickoff wake) and then
  tell the owner once.

## Convention: every waiting agent knows its next step

An agent that is not making progress must be able to answer, from durable state alone: what is the next
step, who owns it, since when, and what failed last. Waiting results and review cards state that next step;
failures are recorded per step and retried; a step that keeps failing, or a counterpart that stays silent, is
escalated once up the reporting chain. Events remain wake hints only, so a missed or failed event is
recovered by the next review rather than by a human noticing silence.

## Recovery

Restart the rebuilt host. Naomi's next review creates the documentation task and the handoff resumes; no
data repair is needed. Import Creative Director 1.14.0 and Producer 2.15.0 for the reporting and follow-up
behavior.
