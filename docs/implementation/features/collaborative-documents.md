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

## UI and regression expectations

The communications page owns the viewport while its message list owns vertical scrolling. Any
layout that embeds an artifact workspace must keep the parent flex chain bounded with `height`,
`min-height: 0`, and overflow containment; otherwise a long agent message expands the page and
makes later messages unreachable.

Regression coverage should prove that:

1. the collaborative list and detail routes each resolve to exactly one authenticated endpoint;
2. legacy planning-document routes return `404 Not Found`;
3. a chat-created artifact can be read through the canonical Documents API; and
4. the nested communications message list retains a bounded scroll container.
