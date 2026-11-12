# Work progress sequence recovery

## Confirmed failure

QFN Games (Prism Breaker), 2026-11-11, 01:13 UTC. Adrian (Engineer) was working ticket `VG58E8BFBD4D-29`. The
platform accepted progress #265, then lost the next report. The following model call's "LLM request received"
report reached `AgentWorkInbox.AppendProgressCoreAsync` with a number past the gap and was rejected with "Progress
sequence numbers must be strictly monotonic without gaps." The SDK failed the model call on that rejection; a second
model call failed the same way, and the attempt finished `Blocked` with the rejection as its summary.

Why one lost report blocked a ticket:

- The platform accepted progress only when `sequence == LastProgressSequence + 1`, so after one gap it rejected every
  later report in the attempt.
- SDK 5.22.0 and earlier (`LeaseProgressReporter`) used up a sequence number before sending the report, so a report
  that failed for any reason created a gap.
- The Engineer's workspace progress swallows failures, which hid the first one. The SDK's model-call status reports
  don't, so the gap surfaced as a failed model call.

The cause of the first lost report was not determined: no agent log was reachable, and rejected work RPCs were not
logged.

## Correction

**Progress never blocks work** (`AgentWorkInbox.AppendProgressCoreAsync`):

- A sequence at or below `LastProgressSequence` was already recorded (a resend after a lost reply). It succeeds and
  writes nothing.
- A higher sequence is accepted even after a gap.
- Past the 4,096-record retention bound, a report still succeeds and renews the lease; only the record is dropped.
  Before, report 4,097 failed the attempt.
- Sequence 0 or below is still rejected, and so is a report over 64 KiB.

Readers already order progress by sequence, so gaps need no reader change.

**Rejected work RPCs are logged.** `McpGatewayEndpoints.LogRejectedWorkRequest` logs every rejected `csweet/work/*`
and `csweet/llm/*` request (method, session, exception type and reason) before it becomes an RPC error, so the first
failure in a chain is visible.

**SDK 5.22.1:** the reporter advances its sequence only after the platform accepts a report, and model-call status
reports are best-effort. Cancellation still stops the call, and a revoked lease still fails the next inference read.

## Verification

`AgentWorkInboxTests`:

- `A_lost_or_resent_progress_report_never_blocks_later_reports`: a gap is accepted, resends of 3 and 2 are
  duplicates with no record, and 0 is rejected.
- The retention-bound test now expects report 4,097 to succeed without storing a record.
- The lease test now expects a gap to be accepted.

Ran all 79 unit-test classes that touch the inbox or the MCP gateway: 1,181 passed. The 2 failures are in code this change
doesn't touch: one needs sibling agent repositories, and the other is `DataProtectionKeyRingTests` (data
protection key ring).

## Recovery

Retrying the blocked ticket starts a new attempt, and its sequence restarts at 1. Once this AgentHost is installed, an
agent on any SDK version can no longer be blocked by a progress gap.
