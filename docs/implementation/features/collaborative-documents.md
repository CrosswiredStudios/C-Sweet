# Collaborative documents

## Canonical boundary

C-Sweet has one active document system: collaborative document artifacts. A document is an
`Artifact` whose type is `Document`, with immutable content in `ArtifactRevision` records. The
system owns permissions, access requests, stewardship, revision submission and decisions,
packages, audit events, and links back to conversations and work items.

The retired planning-document prototype must not be reintroduced. Do not add a second document
entity, table, API namespace, revision model, or UI client alongside artifacts. The
`RetireLegacyPlanningDocuments` migration removes its obsolete `PlanningDocuments` table. An
operator who needs prototype records must export them before applying that migration; they are not
compatible with the collaborative authorship and authorization model and are not migrated
automatically.

## Implementation rules

- Human UI and API work uses `IArtifactDocumentService` and
  `/api/organizations/{organizationId}/documents`.
- Agents use the governed `platform.artifact.*` capabilities. Capability handlers must enforce the
  installed agent identity, grants, idempotency, and audit requirements.
- Document creation and revision always include stable idempotency keys. When available, preserve
  `OriginConversationId`, `OriginWorkItemId`, workstream, team, package, and steward context.
- A generated planning-task result remains task output unless an authorized human or agent
  intentionally publishes it as a collaborative artifact. Services and programs must never write
  directly to artifact tables.
- Multiple-choice, review, and approval messages may initiate artifact actions, but the durable
  artifact or revision ID remains the source of truth; rendered chat content is not a document.
- The Documents page and chat artifact workspace consume the same API and models. A document
  created during a chat must therefore appear on the Documents page without copying or conversion.
- `ApprovalDashboardService.GetAsync` routes a pending artifact to the reviewer in the
  `ArtifactReviewJob` for its submitted revision, falling back to `Artifact.StewardOrganizationUserId`.
  Agent reviews remain with the assigned agent even when queue delivery completes or fails;
  a prior revision's job does not assign a new submission. Only unassigned artifacts fall back
  to organization owners. Organization-wide document access does not make an internal review
  a CEO inbox action. Cards identify the document creator instead of a generic system requester.
- `ArtifactReviewJobWorker.ProcessNextAsync` keeps agent-to-agent review routing silent:
  missing conversation metadata, active reviewer turns, deferred delivery, and idempotent
  redelivery do not create human notifications. The durable submission event remains available
  to the agent reviewer. Missing, inactive, or uninstalled reviewers raise an actionable
  reassignment error with the actual cause. Superseded, decided, and archived submissions
  are retired before checking reviewer availability.
- `ArtifactReviewJobLifecycle.CompleteRevisionAsync` completes queue jobs, clears their errors,
  and dismisses reassignment notifications in the same save as the document decision. Human
  decisions and both agent decision capabilities share it. `AppRealtimeCoordinator` removes
  resolved snackbars on `NotificationUpdated` and ignores late creation events for those IDs.
- `ArtifactCapabilityHandler.GrantAssignedManagerReviewAsync` grants exact-file read and decision
  access atomically at creation and submission when the creator names their active agent manager
  as steward/reviewer. Naming an unrelated employee does not grant authority; creator edit/submit
  access stays separate from manager review access.
- Agent `ArtifactDocumentService.RequestAccessAsync` records an already-satisfied request as
  approved without adding or extending grants or creating a pending inbox action. Expired,
  revoked, and incomplete grants still require the normal access approval flow.

## UI and regression expectations

`DocumentApprovalCard` renders a submitted-revision chip and inline Approve / Request revision
actions in `PendingAgentQuestions` (Overview) and `Approvals`. Both open the existing
`ArtifactWorkspace` through `DocumentApprovalSelection`, without navigating to Documents.
An Approvals `approvalId` deep link opens that item's document pane automatically.

`ArtifactWorkspace.RevisionId` pins the preview and decisions to the same immutable revision.
Without a pin, the workspace previews the current submitted revision during review, otherwise
the latest draft. `ReviewOnly` retains Preview, History and authorized decisions while omitting
editing and access administration. Historical, missing and superseded revisions cannot be
decided. Both card and pane re-read current document state before deciding; feedback is required
for a revision request. Document events and reconnect refresh authorized state, and successful
decisions refresh the host queue. The pane supports Escape and restores focus to its opener.

`ApprovalDashboardService.GetAsync` retains the last decided revision, decision time, actor and
feedback for completed artifact cards, so later drafts do not replace the reviewed evidence.
`DocumentApprovalRenderingTests` covers both surfaces, deep links, exact revision content and
decisions, stale submissions, unavailable documents, feedback and read-only history.

The communications page owns the viewport while its message list owns vertical scrolling. Any
layout that embeds an artifact workspace must keep the parent flex chain bounded with `height`,
`min-height: 0`, and overflow containment; otherwise a long agent message expands the page and
makes later messages unreachable.

Regression coverage should prove that:

1. the collaborative list and detail routes each resolve to exactly one authenticated endpoint;
2. legacy planning-document routes return `404 Not Found`;
3. a chat-created artifact can be read through the canonical Documents API; and
4. the nested communications message list retains a bounded scroll container.
