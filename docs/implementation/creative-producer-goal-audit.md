# Creative Director / Producer completion audit

The goal remains active. Host unit tests alone do not prove an agent can complete its job.

## Required workflow and proof

- Hiring activates the actual installed agent and creates one direct conversation per pair. Verify persisted lifecycle acknowledgement, participant identity, and visible messages.
- The Director supplies the exact accepted pitch and GDD, creates personal work for missing documents, and keeps independent project decisions from aborting documentation delivery. Verify personal board state, document revisions, access grants, and collaboration transcript.
- The Producer and Director ask/answer questions and coauthor an accepted production brief. Verify real provider-backed turns and exact revision acceptance, including recovery after interruption.
- Project understanding persists beyond an invocation/restart. Verify operating state and memory entries/recall against accepted sources, not just statements in chat.
- The Producer recommends justified staffing; the Director reviews it; human hiring authority remains intact. Verify the actual proposal, evidence, review decision, and approval UI. Technical leadership precedes team-board execution.
- Both agents can execute their advertised responsibilities using installed grants, host schemas, project authority, and runtime/provider readiness. Exercise the real SDK/host boundary; manifest-only and fake-capability tests are insufficient.
- Task failures, semantic blockers, dependency waits, and retryable infrastructure failures are distinct. Verify stable task/session identity, bounded retries, explicit recovery, readable causes, notification delivery, and authorized inspector views.

## Current authoritative findings (2026-09-08 UTC)

The live organization is `a948db9b-a2e4-4ce8-bb56-6a51be966958`. It has Director 1.6.5 and Producer 2.3.5. Coordination session `4d79945b-b3c7-4839-9a18-6f77ccf331ef` failed during inference.

- The Producer's `platform.llm.chat-stream.v1` requirement **is granted**. Provider diagnostics show HTTP 400: **No model loaded**. The SDK currently turns streamed inference failures into generic `platform.capability.unavailable` with `retryable=false`; the safe work envelope drops the useful explanation.
- The Director's pending asset-strategy decision requests `routine-project-production-strategy`, but its project proposal never included that action in the requested authority envelope. The host correctly rejects it. Do not bypass the approved authority or mutate live approval data to conceal this defect.
- Personal-task exception handling releases the claim to Ready. The host retried even explicitly non-retryable failures, then personal-board reconciliation created fresh delivery events for the same Ready task. This caused an ongoing error loop instead of an actionable blocker.

## Changes validated locally

The host honors explicit non-retryability on the first failed attempt. Personal-board reconciliation moves a terminal non-retryable task to the Blocked column, supplies a safe explanation, and uses existing blocked-task notifications rather than repeatedly emitting replacement wakes. Explicit pending wakes remain usable for deliberate retries. Known provider errors retain actionable sanitized explanations in inference results/run diagnostics without echoing raw provider bodies.

Failed collaboration cards now explain inference readiness failures using the persisted failure envelope, including older sessions. Human managers can explicitly retry a Failed/Blocked session with revision and idempotency checks. Retry preserves the session/transcript, resolves the actual failed speaker, validates participant grants, and records the human action separately from agent messages.

Director 1.6.6 requests the two production authority actions its implementation uses. Asset and toolchain decisions now have separate durable personal commitments, so a blocked decision cannot abort documentation delivery. Pending decisions still require approved authority; a matching externally resolved decision is reused without attempting a second approval. Decision events wake the corresponding commitment.

Validation: 79 focused host tests and 79 Director tests passed; Director self-test passed. Built and packed `CSweet.Agent.CreativeDirector.VideoGame.1.6.6.nupkg` and verified its nuspec version. These are source/package validations, not proof of live end-to-end completion.

The inference server now reports the configured model loaded. The existing collaboration remains Failed, demonstrating the need for deliberate recovery after dependency repair.

## Activation handoff

The user explicitly chose: **Keep changes local; I will publish and restart.** Do not publish or restart on their behalf. The running installation remains Director 1.6.5; Director 1.6.6 and the host fixes are local. A source ZIP import creates a different package source and is not an update for the existing repository-backed installation.

After publication, update the existing Director installation to 1.6.6 through C-Sweet's normal update/approval flow and restart the rebuilt local host. Preserve the existing business. Inspect the failed collaboration and use Retry collaboration after confirming provider readiness. Older project authority is not silently widened: its pending asset decision still needs authorized resolution, while independent documentation work should proceed.

## Remaining completion gates

1. Verify canonical direct-chat migration and uniqueness in the running business, with both histories preserved.
2. Run real Director/Producer document coauthoring through exact revision acceptance after retry. Verify the same session and task identities without duplicated side effects.
3. Verify memory writes and recall against accepted sources across an invocation/restart.
4. Verify justified staffing recommendations, Director review, and human hiring approval; verify Technical Director participation before team execution.
5. Validate independent production commitments and governed resolution of the existing project's pending decision against the real host.
6. Audit auto-requeue helpers and the SDK queued-inference error chain; generic capability envelopes still lose the precise provider cause even though safe host diagnostics and UI explanations now improve visibility.

The goal remains active until these runtime gates pass. A successful package build and unit suite do not demonstrate that the agents can complete the full workflow.
## Runtime follow-up (2026-09-08 13:50 UTC)

Director 1.6.6 is now installed and active. The authenticated retry preserved coordination session 4d79945b-b3c7-4839-9a18-6f77ccf331ef. After installation interrupted its first attempt, the Director completed real inference and persisted turn 2 at 06:13:54 UTC. Producer work 829f369a-f87c-4455-8003-4b9106e42f52 claimed the next turn, but failed: attempt 1 reported runtime.rate_limited, attempts 2 and 3 reported cancellation/deadline expiration after roughly 126 seconds. The persisted work deadline was 07:19 UTC and the grant permits 86400 seconds, so those cancellations are not explained by either limit. The session is Failed at revision 6; no staffing or full-workflow completion claim is justified.

Local follow-up: inference result pages now carry up to 256 chunks, bounded by 64 KiB of payload except an individually larger chunk, and reads coalesce for one second. This replaces tight polling of 16-chunk pages that can exhaust the 240-request/minute session limiter. Six queue/paging tests passed, including ordered lossless delivery of 17699 chunks in at most 70 pages, byte-boundary behavior, queue cancellation and expired-lease rejection. This host change is not yet activated. The remaining two-minute cancellation source needs runtime/transport diagnosis; it is not claimed fixed by batching.

## Exact post-inference blocker (2026-09-08 15:04 UTC)

After the paging fix was activated, Producer work 353ce0ff-8e73-4f43-96dc-ef86fa942f12 completed inference in 160 seconds without rate limiting or the prior cancellation. Audit records prove the Producer wrote its cached review, created a revised artifact, and saved the review state. Turn publication then failed. PostgreSQL logs identify the exact cause: unique constraint IX_AgentCoordinationTurns_SessionId_ArtifactType_ArtifactKey_A~ rejects the second video-game.production.pitch-review.v1 for the same pitch key and page 1. Subsequent fast retries replay the saved result and hit the same deterministic database error, incorrectly classified as transient transport failure.

Migration 20260908151000_AllowCoordinationArtifactRevisions changes the artifact lookup index to non-unique; turn ordinal, idempotency key, and event identity remain unique. PostgreSQL rollback-only verification against the existing session successfully inserted a revised artifact and rejected a duplicate turn ordinal, then rolled back every change. No live schema or conversation data was altered. Activation requires the user's next rebuilt-host/migrator restart. The cached Producer result should allow recovery without repeating its successful model generation. Full acceptance, memory recall and staffing verification remain open.

## Staffing handoff blocker

The live brief collaboration reached Completed at revision 19, with Producer turn 7 confirming readiness and Director turn 8 completing the handoff. No Producer ResourceChangeRequest was created. Personal-task delivery 46f8688c-d1c2-4da2-b2ed-aa5f38ac8485 failed permanently at platform.management.resource-change.propose.v1. The audit gives the exact rejection: Only the current team lead may propose a team-scoped capacity change. The Director leads team 3333e6d1-6710-4743-a214-5e2bb68deb4c; the Producer is an active member reporting to the Director.

ResourceChangeService now permits an active member reporting to the current lead to submit a proposal to that lead. Existing manager-conversation, revision, workstream linkage, evidence, and approval checks remain. Reporting targets inherited from the approved baseline remain valid; new roles still require requester/proposed-role reporting. Regression coverage includes a preserved baseline plus one addition, manager-targeted pending approval, inactive membership rejection, wrong-lead rejection, and rejection of unauthorized new reporting. Sixteen resource-change/replenishment tests pass. These host changes are local and require activation before retrying the blocked Producer personal task. Do not retry the already-completed brief collaboration as a substitute for the staffing task.

Chief of Staff receipt and hiring recommendation publication remain unverified until a real Producer plan is accepted by the Director. The earlier Director-to-Chief initial-hire event had a separate replenishment failure; do not assume the downstream path works solely from the resource-change unit tests.

## Director pending-plan recovery

Producer staffing request 520a1f78-f245-460d-b1cd-5e040b053727 was successfully submitted after the host authorization fix. Its requested event completed without a decision. Director code deserialized the camel-case host event with case-sensitive default options, leaving IDs unset and returning silently. Director review also retained the obsolete team-lead-only requester condition.

Local Director 1.6.7 fixes event deserialization, recognizes the assigned Producer proposing to the Director-led team while retaining exact accepted-brief/evidence gates, and recovers addressed pending staffing requests during attention reviews. 83 agent tests and the self-test passed; packed nuspec version verified 1.6.7. User retains publishing/update responsibility. Chief of Staff delivery remains an outstanding live verification gate after this plan receives a real Director decision.

### Hiring-plan presentation correction (2026-09-08)

The approved request `520a1f78-f245-460d-b1cd-5e040b053727` retains the existing Producer position (`Unchanged`, reporting to Naomi) and adds one Technical Director reporting to Gabriel. Its one workforce recommendation matches the approved delta. Producer AdaptiveDelivery deliberately requests technical leadership before implementation hiring; the chart's two desired-role nodes were not two new hiring requests.

The UI now separates retained positions from the proposed-change graph, preserving existing role ancestors where needed to explain nested changes. Catalog role names replace raw-key titles in graphs and approval role lists while preserving custom titles and unknown-key fallbacks. Existing approved records are not rewritten. Approvals now loads the same catalog presentation scope as Communications and Employees. The earlier literal Razor parameter fix remains in place.

Local validation: graph and catalog presentation tests cover retained roles, new roles, reporting ancestry, catalog naming, custom titles, and fallback behavior. Runtime visual verification still requires the user's rebuild/restart. No agent package or approved staffing content was changed for this display correction.

### Technical Director hired; board creation scope mismatch

Live container postgres-pyfpartg confirms Victor Lin is active, installation 37ad5944-3be3-4748-94a4-a6853c7fa765, reporting to Gabriel. Scheduled reviews complete, but only five personal boards exist. Producer failures at 2026-09-08 17:06–17:07 UTC identify work.board.create rejecting workstream scope. Producer already has an unrevoked team-scoped board-create grant for team 3333e6d1-6710-4743-a214-5e2bb68deb4c.

WorkManagementCapabilityHandler now permits that existing team grant when the requested workstream has a current, started assignment to that team and the installation belongs to an active employee with active team membership. Existing team-active, workstream existence, profile, and idempotency checks remain. No live grants or business records were changed. Regression coverage verifies successful assigned-team creation plus unrelated workstream, ended assignment, future assignment, ended membership, and missing grant denials. Handler suite: 15 passed, 3 pre-existing skipped.

Activation requires the user's rebuild/restart. The failed planning commitment may need normal UI requeue afterward. Additionally, AgentEmployeeIdentityResolver derives ManagedWorkstreams from supervision assignments; Producer attention reviews exit early if that collection is empty. Inspect the current assignment and intended management-authority model before claiming continuous autonomous planning. Team backlog, phased plan, further justified staffing, repository/branch/PR/testing flow, and playable local review remain unverified.

### Periodic planning discovery recovery

The next source audit confirmed Producer attention reviews returned immediately when ManagedWorkstreams was empty. Host identity intentionally exposes formal supervision only, while hired Producer is a team member. Host portfolio discovery also omitted team-assigned workstreams despite individual workstream visibility allowing those team members.

ReadPortfolio now includes current team assignments with started/non-ended assignment, active membership, non-archived team, and matching organization. This does not add supervision or decision authority. Nine host roster/portfolio tests pass, including exclusion of unrelated, foreign, future, ended, archived, and former-member workstreams.

Producer 2.3.6 now discovers review workstream IDs from durable accepted handoffs as well as formal supervision, then relies on the host to filter current visibility. Empty visible portfolios exit without new planning side effects. Nineteen Producer tests pass, including a real review invocation with a persisted accepted handoff and no identity supervision. Self-test passed; local packed nuspec verified 2.3.6. No SDK or shared-contract changes. User retains publishing, installed-package update, and rebuilt-host restart. Live recovery remains unverified until activation; the full goal is active.

### Execution fidelity audit

Source inspection after planning-discovery fixes found an additional end-state gap: Video Game Engineer inherits a specialist executor that generates Markdown, submits an artifact and marks the stage Completed; it does not run a coding workspace or publish a PR. Existing generic Software Developer/Architect source provides brokered workspace and delivery-finalization paths to adapt. See video-game-execution-gap-audit.md for the requirement matrix and exact source paths. This contradicts any assumption that successful game planning alone yields a working game. Full goal remains active; implementation and runtime verification of code delivery, lead review, QA and local preview are still required.

### Live activation check after local execution changes

Container postgres-cjrtgjjt is now running. Current business still has Producer 2.3.5, Technical Director 2.3.2 and Creative Director 1.6.7. No team board exists; only personal boards. Planning task 8e72de13-801b-4b19-85db-130d55933f1e remains Blocked; no Pending/Leased work was present at inspection. Recent scheduled review deliveries completed without advancing planning, consistent with the installed Producer's supervision-only discovery path.

Attempted normal UI inspection for a requeue, but the initial tab was stale (displayed Doing while the database recorded Blocked). Reload revealed a startup failure: browser logs report Failed to fetch dynamically imported module /_framework/dotnet.smbkzf2na4.js. Independent HTTP checks: root 200, hashed runtime 404, unversioned /_framework/dotnet.js 200. The hashed file exists on disk; Debug static-web-assets manifest includes it and was updated at 11:57:24 local, after current Aspire startup at 11:41:35. A stale running static-asset mapping is the likely cause, not a proven source-code failure. User retains rebuild/restart responsibility. No requeue or business mutation occurred.

Needed activation remains Producer 2.3.6 and Technical Director 2.4.0 plus rebuilt host; Engineer 2.2.2 is local for the later engineering hire. Do not claim live execution or silently install/publish these packages. Review/merge/QA workflow implementation can continue independently of this activation blocker.
