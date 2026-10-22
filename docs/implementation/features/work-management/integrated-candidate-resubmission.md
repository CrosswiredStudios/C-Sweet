# Unchanged integrated-candidate resubmission

`IntegratedCandidateResubmission` distinguishes the original developer publication
from the trusted Story integration commit. Unchanged rework must not invent a new
publication, rewrite history, repeat a merge or turn an old QA failure into a pass.

`RequireAsync` validates the activated plan, current task specification and staffing
digest, latest non-superseded publication, completed integration receipt, repository
and team access, and both current Git branch heads. Advanced branches, unintegrated
publications, revoked access and stale task revisions fail closed.

`QueueQaAsync` records `candidate.resubmitted` with publication/receipt IDs, exact
integrated SHA, assignment/planning/scope revisions, actor and rationale. It replays
the existing integration receipt into the current traversal and queues fresh,
independently assigned QA. Historical attempts and findings remain unchanged.
State, audit and wake/UI outboxes persist together through
`WorkOrchestrationBoardState.SaveBoardChangesAsync`. No new agent grants are added.

`CurrentPublicationAsync` is the shared revision-bound selector used by QA result
persistence, manual QA, the Git workspace broker, canonical workspace authorization
and scoped history. Only a recorded current resubmission can reuse an older
publication across an assignment or criterion amendment; newer publications and
changed revisions invalidate that binding.

`WorkOrchestrationService.GetIntegratedCandidateAsync` and
`ResubmitIntegratedCandidateAsync` expose the governed owner/manager recovery.
The board manager or a human with board retry authority may inspect a stopped
author stage, then confirm the exact integrated SHA and explain why no edits are
needed. POST is revision-bound, transactional and idempotent. Replay still checks
current authority and exact request identity. Running author work is not interrupted.

UI: open a blocked Code ticket's **Overview**, select **Resubmit existing
candidate…**, inspect both SHAs, enter a rationale, then **Confirm resubmit to QA**.
This action does not approve the candidate or discard retained workspace edits.
API routes live in `IntegratedCandidateEndpoints` beneath the board item:
`GET integrated-candidate` and `POST resubmit-integrated-candidate`.

Engineer 4.1.6 and Software Developer 2.1.5 also recognize the exact integrated
commit in prior platform outcomes. After unchanged-workspace validation and
finding-resolution checks, they explicitly mark `ResubmitsIntegratedCandidate`.
`WorkOrchestrator.ValidateIntegratedResubmissionAsync` verifies that request against
the same platform-owned receipt before queuing QA. Ordinary changed code retains
the normal publication → independent Technical Review → integration → QA gates.

Deploy the API, AgentHost, WorkerHost and App changes before updating the agents
through published releases and Installed Agents. There is no SDK or shared NuGet
contract change. Regression coverage: `IntegratedCandidateResubmissionTests`, both
agents' `NoChangeReworkTests`, and the developer-core parity script.

## Overlapping callback context

The first live resubmission correctly reached fresh QA, but QA's inference overlapped
an attention callback on the same runtime. `PlatformMemoryReadEvidence.BeginAsync`
treated that server-attributed inference like an unattributed memory read and refused
two live leases. Chat-stream context now uses the trusted
`InferenceExecutionAttribution` work ID and attempt, retaining all runtime, grant,
employee, lease and evidence checks. Unattributed reads remain single-lease only;
wrong work/attempt identities and changed sources still fail closed.
`AgentMemoryOverlappingDispatchTests` exercises this against isolated PostgreSQL,
including the empty-instruction case that blocked ticket 23. No memory authority is
expanded. The resubmission rationale field updates immediately as the user types.

## Verified rollout, October 21

- Published Engineer 4.1.6 (`686842af49f37c99f665896536224ca3d870204c`)
  and Software Developer 2.1.5 (`024715247e270b375fec0bb48e730bd4cc49c1d5`).
  Both Installed Agents detail views confirmed those source commits and Agent ready;
  Adrian's actual hire also selected 4.1.6. Each agent passed 316 tests, its self-test
  and package-version verification; 57 developer-core/parity files match.
- Deployed the API, AgentHost, WorkerHost and App normally through Aspire restarts
  after successful builds. Sixty-two focused workflow regressions passed, plus
  three overlapping-dispatch cases on a disposable, separately provisioned PostgreSQL
  server. That test container was removed after validation.
- Used the new UI action on ticket 23. The durable resubmission bound publication
  `550900eb-997a-447f-96d8-1055b0f00530`, integration receipt
  `8a09a965-20fa-4ebc-924e-09f150009c50`, scope revision 2 and planning/assignment
  revision 3 to unchanged integrated commit `4d3c2c392b2593080beaa485f7898edaf7114edf`.
  No publication record, historical verdict or Git history was rewritten.
- After the overlapping-context fix, the ordinary board Retry started QA attempt
  `726a5b6e-c0d3-4a63-884a-2e3dbacce437` on stage
  `5b75a47c-3cc6-4fb8-8aa3-4624fcb3832d` and runtime
  `601412ee-2ea5-4474-bfaa-adbff490712b`. Actual provider inference started at
  15:10:16 PDT. At 15:11:09, its new `git-history` journal observation read all seven
  commits at the exact assigned SHA, original publication `aa5d12f0…`, with no next
  page. The stored receipt SHA-256 was verified. At this checkpoint QA was Running,
  not blocked; no new QA pass was asserted.
