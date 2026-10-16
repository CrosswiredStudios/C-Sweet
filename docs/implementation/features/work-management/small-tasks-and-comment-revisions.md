# Small tasks and comment revisions

The Producer and Technical Director plan independently deliverable increments. If a task
can deliver two verifiable changes, split it, even when the second depends on the first.
Keep the tests that prove each increment with that increment. Ticket-count preferences must
not turn several deliverables into one broad implementation ticket.

## Implementation briefs

Technical Director `ImplementationBrief` defines the description sections: `Outcome`,
`Context`, `Pseudocode`, `Assumptions`, and `Verification`. Context identifies relevant
paths, types and members and distinguishes verified facts from proposed locations.
Assumptions name dependency results and evidence that would invalidate the plan.
`GeneratePlanningAsync` uses bounded corrections for missing sections. The lightweight
`ProjectDeliveryPlanning` path receives the same guidance through `ImplementationPlanningClient`
and rejects incomplete briefs before returning a proposal. Structural checks establish
presence, not the semantic quality of pseudocode or the smallest possible decomposition.

Producer `TaskSizingDirection` applies to both formal creative-handoff planning and the
lightweight project flow. Existing accepted plans are not rewritten merely by upgrading.

## Comment-based correction

Engineer and Software Developer `AssignedDevelopmentService.BuildAssignmentPrompt` includes
the current discussion with author and revision metadata. Developers compare the ticket's
assumptions with actual code and accepted dependency results before editing and on resumption.
`TicketDiscussionTools.request_ticket_revision` records the obsolete instruction, observed
evidence and proposed correction or split in a durable `discussion.request` comment.
It targets the currently assigned technical reviewer or board manager, uses the existing
attempt idempotency key and pauses the attempt. `AttachPauseGuard` prevents subsequent tool
calls from mutating the workspace; the existing checkpoint path preserves completed edits.
Recovery reconstructs the wait from the comment, including its revision-request marker.

Technical Director and Producer `TaskRevisionComments` format an explicit comment decision:

- `continue`: identify superseded implementation guidance and give replacement context,
  pseudocode, assumptions and checks. This comment takes precedence over the identified
  description guidance within unchanged accepted outcomes and criteria.
- `clarify`: state the missing evidence or focused question. The original work stays blocked.
- `replace`: explain why the original task is obsolete or too broad and describe small
  replacement tickets and their dependencies. The original work stays blocked.

Producer `AnsweredDiscussionRetry` requires the exact current request revision, expected
reply author and correlation. A revision request additionally requires an explicit
`continue` decision; prose acknowledgements, clarification questions, ambiguous decisions
and replacement recommendations cannot restart the old task. A resumed developer rechecks
the correction against current code; a comment never grants platform authority or waives
acceptance criteria, tests, independent review or security requirements.

## Substantial changes

`ReplacementDiscussionEscalation` immediately routes an authenticated replacement
recommendation through the Producer's existing durable management escalation path;
`RetryDirectedTicketAsync` rejects a plain retry while that recommendation is unresolved.
The desired replacement procedure is to create small linked tickets, preserve valid work,
redirect dependent work, refresh estimates and readiness, then close the old ticket as
superseded/cancelled rather than completed. Preserve comment history and links in both
directions. Accepted-scope changes still use the authorized amendment workflow.

This release implements the request, correction and safe resumption path. Replacement
recommendations do **not** automatically cancel active executions or create a replacement
backlog; they require governed replanning. Neither comments nor installing an agent update
mutate an already executing assignment snapshot.

## Verification

Technical Director `ImplementationBriefTests` covers brief correction and typed comment
decisions. Producer `DiscussionRecoveryTests` covers correction-only resumption, exact
correlations, blocked replacements and escalation. Both developer repositories carry
`TicketDiscussionToolsTests` for authorized recipients, duplicate/restart recovery and
blocking file tools after a wait. `scripts/Test-DeveloperAgentCoreParity.ps1` checks core parity.
