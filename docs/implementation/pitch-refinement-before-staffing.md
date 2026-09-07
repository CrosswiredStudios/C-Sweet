# Pitch refinement before staffing

Creative Director 1.6.0 and Producer 2.3.0 replace the immediate vision acknowledgement with
an iterative conversation. Technical Director 2.3.1 recognizes the platform's Approved package
status when consuming the resulting planning evidence.

1. The Creative Director supplies the actual accepted pitch and high-level GDD as exact document,
   revision, and hash references in `video-game.production.pitch-brief.v1`.
2. The Producer reads both sources, drafts a project-owned production brief, and returns focused
   questions and its planning assessment in `video-game.production.pitch-review.v1`.
3. The Creative Director reads that exact draft and responds with answers, proposed wording, or
   revision requests in `video-game.production.pitch-reply.v1`.
4. The Producer incorporates the feedback into a new revision of the same document. This repeats
   until it reports justified readiness and no remaining planning questions. The document covers
   scope, player experience, non-goals, acceptance criteria, deliverables, constraints, risks, and
   open questions. Technical investigations may remain planned work; unresolved product questions
   cannot be disguised as confidence.
5. The Creative Director accepts only the exact submitted revision that faithfully captures the
   accepted direction. The Producer independently verifies that acceptance matches its own
   question-free review before recording a handoff and enabling its staffing/planning reconciliation.
6. The existing resource-change flow then routes staffing to the Creative Director for review,
   followed by Chief of Staff hiring suggestions after approval.

The working document contains immutable source appendices populated from the accepted documents,
including their IDs, revision IDs and hashes. The original pitch is not rewritten. Both agents
contribute to the working content; the Producer serializes the document edits to prevent concurrent
overwrites. Conversation messages link to the shared document. Model responses are cached by
session and turn before document mutations, and mutations use stable idempotency keys.

Chat coordination supports an explicit `documentReferences` list (up to eight exact document,
revision, and hash tuples). The host validates that the sender is the creator or steward and has
active read access before granting the other authenticated participant read access to those files.
It does not confer edit or decision access. Submitting a document for review can grant read/decide
access to the creator's active direct manager when that manager is also its assigned steward.
Naming an unrelated reviewer confers no such authority. The existing manifest capability checks
remain required. The Producer now declares artifact revision capability.

Planning-package creation accepts and preserves `acceptedRevisionId` rather than dropping the
SDK's supplied revision. Both agent and human package decisions retain validated pins. The package
contains the accepted project-owned collaborative brief, including its source appendices, so it
does not mix intake-document and project-document scope or require the Producer to submit the
Creative Director's original pitch on its behalf.

Host regression tests cover document-sharing authority, manager-review authority, package pins,
and existing coordination/document behavior. Agent tests exercise question/answer/revision/
acceptance, replay recovery, invalid acceptance, and the mutual readiness gate. These tests use
deterministic model decisions and do not establish live model convergence.

Deploy the host changes and reimport the updated agents to exercise this flow. Existing active
legacy handoff sessions need to be restarted through normal coordination controls with the new
pitch-brief payload; existing completed staffing decisions are not retroactively revoked. Project
setup approval remains a prerequisite for the project-scoped conversation. No live hiring, approval,
conversation or document was changed during implementation.
