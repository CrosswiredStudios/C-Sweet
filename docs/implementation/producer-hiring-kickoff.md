# Producer hiring kickoff recovery

The Video Game Producer hire stalled at two independent boundaries:

- Definition-based hiring did not activate the Creative Director's imported Workstream profile.
  The local profile `video-game-production.v2` version 4 remained `Previewed`; the Director's
  `platform.workstream.plan.propose.v2` calls failed with "The requested Workstream profile is not active."
- Producer 2.3.2 ignored its onboarding event. Delivery work completed, but the lifecycle outbox
  remained pending because no acknowledgement or introduction was sent.

Installation and definition-based hiring now share profile activation, including updates. Recovery
reconciles previewed profiles only against enabled, active, approved installations with built and signed
packages. It checks the immutable profile key/version and provider identity. The original importing
package version stays as provenance; a newer package may reuse the same immutable profile.
Retired profiles and uninstalled preview imports remain inactive.

Producer 2.3.3 contacts its authoritative manager and owner conversation, persists kickoff state,
and then acknowledges onboarding. Stable source-event message keys make partial retries safe.
The dispatcher scopes delivery deduplication to a package version while retaining the original event
identity, so an ignored onboarding event can reach an updated package.

Creative Director 1.6.3 accepts a kickoff from the approved team's active, eligible Producer before
project creation. It verifies broker-authenticated sender context and the governed roster, then resumes
the existing setup and handoff workflow. No creative acceptance or spending authority changes.
The Producer's existing document refinement workflow records the shared brief and accepted handoff
before workload-backed staffing proposals.

Deploy the updated host and reimport both agent packages to apply this to existing hires. Host
reconciliation repairs installed previewed profiles; pending onboarding is delivered to the new package.
An actual project setup approval may still be required before the scoped brief discussion begins.
The repair does not approve that proposal or send messages by directly modifying database rows.
## Follow-up: SDK chat schema and terminal delivery recovery

A fresh business running Producer 2.3.3 and Creative Director 1.6.3 had an active project
profile, but chat creation failed before the handler ran: `$.workstreamId is not allowed`.
The SDK includes nullable `workstreamId` and `teamId` on its typed chat request. The host schema
now accepts both fields, with UUID validation, matching the existing handler contract.
Regression tests invoke the SDK direct-agent-message helper and validate its actual outbound
chat-create and message-send payloads against the host catalog.

The onboarding dispatcher now inspects existing delivery state. A retryable dead-letter delivery
can resume within the lifecycle retry budget, retaining the same work ID, source event ID,
idempotency keys, and attempt history. Its delivery deadline is renewed for recovery; it does
not require recreating the business or hiring another Producer. Polls no longer count as actual
delivery attempts. Terminal failures display their error instead of claiming to await acknowledgement;
a newer package can still receive a fresh delivery. Delivered onboarding remains terminal.

This follow-up requires rebuilding/restarting AgentHost, not another agent package import.
The current project's pending setup proposal must still be approved before scoped planning starts.

## Follow-up: project board creation after the Producer introduction

The next live attempt reached the Producer introduction and Director welcome, and the project
setup proposal was approved. The Director then repeatedly failed with HTTP 400:
`JSON Schema validation failed: $.key is too long.` Its board key was 17 characters;
board creation permits 2-12 alphanumeric characters beginning with a letter. The Producer's
fallback key also contained an invalid hyphen.

Director 1.6.4 and Producer 2.3.4 now generate uppercase alphanumeric board keys within that
limit. The Director retries foundation cards even when a prior attempt already saved the
board ID, using the existing stable per-card idempotency keys. A regression test interrupts
seeding after the first card, resumes from persisted state, and verifies one board and all
three planning cards. The board remains the prerequisite for the scoped pitch-refinement
session, retained production brief, and subsequent staffing proposal.

Validation: 70 Director tests and 18 Producer tests passed; both self-tests passed; both
NuGet packages were built and their nuspec versions verified. These agent source changes
must be imported into the existing definitions; rebuilding AgentHost alone does not apply
them. The live update check currently fails with a GitHub 504. Actual brief convergence
and a resulting hiring proposal remain unverified until the new agent packages run.

The Producer now creates its planning/staffing commitment directly after persisting the exact
accepted handoff and before completing coordination. Attention review uses the same shared
helper and fingerprint, preserving task identity. The collaboration regression checks no task
while questions remain, a project/board/session-linked task upon approval, and one task after
approval replay. This avoids depending on the next periodic review to turn agreement into work.

## Current workflow correction: documentation and staffing before the team board

The rebuilt business was running Director 1.6.4 and Producer 2.3.4, with project setup
approved. Runtime evidence showed `work.board.create` denied because the Director is a
project supervisor, not its accountable board manager. Thus fixing key syntax alone could
not unblock this workflow.

Director 1.6.5 creates personal documentation tasks from Producer requests, retrieves or
reconstructs saved documentation, and shares the reviewed documents through coordination.
It no longer creates the team board or requires that board before documentation handoff.
Producer 2.3.5 creates an initial staffing commitment from the accepted shared brief with
nullable board context. It proposes the missing Technical Director using exact brief evidence.
The Director's initial-hire review accepts only that single missing role, with matching
confident Producer review, Director acceptance, and accepted project document revision.
After technical leadership is available, the Producer creates the board and draft sprint;
subsequent staffing still uses board evidence. This supersedes the earlier foundation-card
seeding approach. Human hiring approval remains separate.

## Direct-chat identity and interrupted collaboration recovery

Direct chats have one canonical identity per organization and unordered participant pair. Project and team scope belong to the coordination session. The hub serializes pair creation across request scopes and PostgreSQL hosts, and the database enforces a unique active participant key. Onboarding and external communication routing reuse that same identity.

`CanonicalDirectChats` combines existing duplicate histories and typed conversation references without deleting messages. The former chat IDs remain archived aliases; message reads, sends, read cursors, coordination source references, and the communications page resolve them to the canonical chat. Project/team context remains on each coordination session.

The host accepts the SDK decision request's optional `typeData`, including null. Previously, rejected project decisions could generate repeated failed calls while the Producer's handoff exhausted its delivery retries. Recovery now retries the actual failed speaker, retaining the transcript and finalization state. Automatic recovery requires active participants and ready installations, waits at least one minute, and stops after twelve attempts for the same logical turn. Permission failures, semantic blockers, cancelled/completed sessions, and retired installations are not automatically resumed. Failure status remains visible in the authorized agent inspection perspective.

Verification includes SDK schema tests, concurrent reverse-direction DM creation, alias/history/read-cursor tests, migration/model consistency, and bounded coordination recovery tests. The PostgreSQL migration was also exercised in a rollback-only temporary schema with duplicate introductions/handoffs, attachments, and coordination/source references; both histories and all links were retained, and the unique-pair constraint rejected another duplicate. Rebuild/restart the local host to apply this migration and load recovery/schema changes; no agent package upgrade is required for these host fixes.
