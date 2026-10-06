# Work item comments

Ticket discussion on the canonical work item: readable, authorable, editable, and retractable by the
subject that wrote it, on both team boards and personal boards.

## Product decision

Comments are the durable conversation attached to one canonical work item. They are not chat: a
comment survives board and sprint changes, carries its author and timestamps, and is retained for
audit even after the author retracts it.

The first implementation shipped comment creation and reads only, and
[work management](./work-management/README.md) recorded comment editing and deletion as an open gap.
This feature closes that gap and extends comments to personal boards.

Authority is deliberately narrow: **a subject may only change the comments it wrote itself**. There is
no moderation grant, so no human or agent can rewrite or erase another subject's words.

## Behaviour

- Reading a thread requires read authority on the owning board. Team boards authorize with
  `work.item.read`; personal boards authorize with `work.personal-todo.read.v1`, so an ordinary work
  item read never opens a private personal board.
- Posting requires `work.item.comment` and the same resource scope as every other board mutation.
- Editing requires `work.item.comment.update.v1`; deleting requires `work.item.comment.delete.v1`.
  Both are additionally restricted to the comment's author.
- Deletion is soft. The body disappears from every read path, while the work-item activity trail and
  the security audit keep the record of who removed what and when.
- Every mutation is revision checked. A stale `ExpectedRevision` is rejected rather than overwriting a
  concurrent edit, and every agent mutation carries a durable idempotency key whose replay returns the
  recorded outcome.
- Each mutation writes work-item activity (`comment.created`, `comment.updated`, `comment.deleted`),
  a grant-filtered realtime board event, and an audit event carrying the authorizing grant revision.
- Archived boards stay readable but accept no new comments.

## Automatic execution feedback

`AgentTicketFeedback` records concise, platform-owned `agent.failure` comments when a ticket's
authenticated execution fails, reports a blocker, or loses its runtime lease. Structured diagnostics
remain in execution logs; comments describe the problem in ordinary language. Personal plan failures
attach to the running task, with the coordinator and running story blocked together on recurrence.

Two consecutive failures with the same fingerprint block the ticket and stop automatic retries.
The fingerprint uses the structured failure code, capability, exception type, and HTTP status,
excluding per-attempt diagnostic IDs; reported blockers use their reported reason.
A private Communications message and notification go to the active reporting manager (or board
manager). Agent managers receive the existing durable message-mention event as well. If no active
manager is configured, the comment and blocked state still persist. The manager can explicitly
requeue after resolving the cause; provider recovery and attention reconciliation do not silently
reopen a repeated-issue block.

`AgentWorkInbox` commits the feedback, blocked state, manager message, and notification outboxes
with the failed attempt. A durable claim activity identifies the personal ticket even when the SDK
releases its claim before reporting the exception. Work-stage attempts identify team tickets.
Per-attempt keys deduplicate feedback; an already-blocked incident does not create another escalation.

`PlatformLlmJobService.RunAsync` adds all current, nondeleted comments to the provider request for
the authenticated execution before generation. It includes the personal coordinator and running plan
items, preserves chronology and edited text, and labels discussion as untrusted user context.
Comments are not silently truncated; the existing provider request limits still apply.
This uses the queued platform LLM path and requires no agent package upgrade.

These are platform-derived execution effects, not new comment grants. Personal agents still cannot
arbitrarily write or edit comments through the general board API.

Validation: `AgentTicketFeedbackTests`, `AgentWorkInboxTests.RepeatedTicketFailureAfterClaimReleaseStopsInboxRetries`,
and `PlatformLlmQueueTests.ClaimedTicketDiscussionIsAddedToProviderContext`.

## Default authority

Commenting is part of ordinary membership rather than a manager-only power. Organization provisioning
grants every active member `work.item.read`, `work.item.comment`, `work.item.comments.read`,
`work.item.comment.update.v1`, and `work.item.comment.delete.v1`. Owners and managers additionally
receive the full item action set. Because updates and deletions are author-scoped in the service,
these grants never let a member reach another subject's comment.

Personal boards grant comment actions only to the board owner, and only when that owner is a human.
Managers keep their existing read access to a reporting chain's personal boards but receive no comment
authority, and agent-owned personal boards receive no comment grants at all: the agent protocol routes
personal boards exclusively through personal-todo actions, so an agent-subject `work.item.*` grant
there could never be exercised.

## Agent surface

| Capability | Tool | Effect |
|---|---|---|
| `work.item.comment` | `comment_on_work_item` | Add a durable comment to a granted work item. |
| `work.item.comments.read` | `read_work_item_comments` | Read correlated comments for a granted work item. |
| `work.item.comment.update.v1` | `update_work_item_comment` | Replace the body of a comment this installation wrote. |
| `work.item.comment.delete.v1` | `delete_work_item_comment` | Soft-delete a comment this installation wrote. |

An installation may only mutate comments whose `AuthorKind` is `AgentInstallation` and whose
`AuthorSubjectId` is its own installation. Comment reads expose `canEdit` and `canDelete` so an agent
can tell its own comments from human review without attempting a denied call.

Both new capabilities must be requested by the package manifest and approved in the installation
grant. Granting them is additive and does not disturb existing approvals.

## Rollout

New organizations receive the actions through ordinary provisioning. Existing data is backfilled by a
data-only migration that mirrors the same rules:

- members holding `work.item.read` at organization or board scope receive the four comment actions at
  the same scope, skipping archived boards and inactive members;
- human owners of a personal board receive the four comment actions on that board.

Backfilled grant identifiers are derived deterministically from subject, scope, and action, so the
migration is idempotent and reversible. Personal-board grant reconciliation is scoped by a single
shared action set, so a reconcile pass revokes a stale comment grant only when it is genuinely
unwanted rather than because the action was overlooked.

## Security invariants

- Comment mutation authority is additive to, never a replacement for, the board-scoped comment grant
  model. Every mutation resolves an action and a resource scope before data changes.
- Author scoping is enforced server-side against the resolved subject, not inferred from the request.
- A retracted comment leaves the activity trail intact; nothing about deletion hides that it happened.
- Comment reads and writes stay inside the organization and board scope that authorized them.

## Deliberately excluded

Comment reactions, attachments, nested thread UI, paging beyond the existing
bounded activity window, hard deletion, and any moderation or impersonation authority.

## Directed agent discussion

`WorkItemDiscussion.QueueAsync` writes `com.csweet.work.item.discussion.changed.v1` outbox rows in the
same save as comment creation, editing, or deletion. Current stage assignees, accountable owner,
board manager, prior commenters and explicitly mentioned teammates receive targeted wake hints if
active and authorized to read both the item and its comments. Item, board, team and organization
grants are supported; personal boards are excluded. The author does not notify itself.

Write `@Full Name: your question` to request an agent response. Names must exactly match an active
teammate and end with punctuation, a newline or the end of the comment; ambiguous names are rejected.
The event contains identifiers, revision and response intent, never the comment body or execution
authority. `AgentPlatformEventDispatcher` retains undelivered discussion hints while installations
are offline or awaiting an approved subscription. Duplicate mutations do not create duplicate hints.

`WorkOrchestrator.RecordReviewDiscussionAsync` copies completed technical, quality and merge review
outcomes into `review.result` comments, preserving findings and the attempt reference. Runtime
failures remain `agent.failure`; a completed rejection is a review decision, not a crashed reviewer.
Existing historical review results remain available in execution history; they are not backfilled.

The independently packaged Software Developer, Technical Director, QA and Producer contain the same
small `TicketConversations.Discussion` protocol helper. They reread authorized current item, comments
and relevant execution outcomes before answering. A reply has kind `discussion.reply`, a
`CausationId` of `<question-guid>:<revision>` and a stable per-recipient idempotency key. Stale/deleted
requests, self replies and already-answered revisions are ignored. Automatic replies never request
another automatic reply. New questions require an explicit new request. This keeps informational
notifications from causing agent chatter. The model decides the substance of the answer.

Attention reconciliation provides bounded missed-event discovery on the current assigned project:
five rotating nonterminal tickets and up to two unanswered requests per ticket, at most ten comment
pages each. Revoked access does not block unrelated attention work. Event delivery is the normal
path; discovery is a fallback, and both use the same deduplicating response path.

`TicketDiscussionTools` gives the developer `read_ticket_discussion` and `request_ticket_response`
inside its existing confined implementation harness. A request names a current ticket participant
from the roster, is durable once per attempt, checkpoints work and returns a correlated
`discussion.response-requested:v1` blocker. Replayed attempts recover their existing request.
`SpecialistAgent.AnsweredDiscussionRetry` lets the board's Producer resume only that blocked stage
when the exact question revision has a reply from the requested installation, within the existing
attempt budget and assignment revision. A reply is input to the next work attempt: it never grants
approval, waives evidence, transfers ownership or completes a ticket. Unanswered or exhausted waits
remain visible to existing producer escalation. No domain-specific review finding is interpreted
by this protocol.

Deployment requires rebuilding C-Sweet and updating all four participating agent packages with their
new approved event subscriptions/read grants. No SDK/contracts package or database migration changed.