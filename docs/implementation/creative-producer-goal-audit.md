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

## Changes validated this goal turn

The host now honors explicit non-retryability on the first failed attempt. Personal-board reconciliation moves a terminal non-retryable task to the Blocked column, supplies a safe explanation, and uses existing blocked-task notifications rather than repeatedly emitting replacement wakes. Explicit pending wakes remain usable for deliberate retries. Known provider errors now retain actionable sanitized explanations in inference results/run diagnostics without echoing raw provider bodies.

33 targeted tests passed for the inbox, personal tasks, LLM handler, and error message sanitization. These source changes have not yet been verified in the running application.

## Next work (not completion claims)

1. Correct and validate the Director's project authority proposal and recovery for already-created pending decisions through governed tools.
2. Separate documentation collaboration and independent asset/toolchain decisions into durable commitments so one blocked action does not abort unrelated work or portfolio reconciliation.
3. Preserve classified inference errors through queued jobs, SDK failure envelopes, work diagnostics, and the user-visible task/session view. Ensure readiness checks and recovery respond to a loaded model/configuration change.
4. Verify that an explicit retry after a resolved dependency resumes the same commitment without duplicating side effects; cover auto-requeue helpers that might otherwise repeatedly unblock permanent failures.
5. Load/verify the configured local model and run the full live collaboration, memory, and staffing path. Account for package build/version/publish and host activation requirements; do not mark the goal complete before actual runtime evidence proves the whole workflow.
