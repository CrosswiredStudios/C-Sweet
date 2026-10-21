# Project names and continuity

## Initial game project identity

Creative Director `InferProjectIdentityAsync` (`ProjectIdentityInference.cs`) is a required step in
`EnsureProjectFoundationAsync` before starting new Producer project-foundation coordination. It
infers the actual game title and a player-facing marketing blurb from the exact accepted vision.
The blurb conveys the hook, gameplay and appeal within accepted scope, rather than summarizing the
pitch or describing delivery work. Heading labels such as “Game Pitch” do not become game names.
Bounded plain-text validation permits one correction; invalid output blocks setup. Results are saved
before coordination under the accepted document/revision/hash and content identity, so duplicate
wakes, restarts and interrupted handoffs reuse the same result. Failed attempts are retained per
model configuration; configuration correction permits another bounded attempt.

`ProjectFoundationPayload` includes `projectIdentity` alongside the template name and outcome.
Producer `ProposeProjectFoundationAsync` verifies that binding and preserves the inferred fields
for the initial submission while refining delivery planning. Legacy handoffs and explicit manager
revision feedback retain their previous behavior. Approved projects and existing proposals are not
renamed automatically. Regression coverage lives in `ProjectIdentityInferenceTests` and Producer
`ProjectFoundationTests`. Deploy Creative Director 2.1.7 and Producer 4.0.8 through published releases
and Installed Agents updates.

## Renames and continuity

`Projects.razor` shows `ProjectNameEditor` beside the project heading for an authorized human manager. The editor supports save/cancel, submits the revision captured when editing starts, and reloads inspection after a conflict. A rename is a display-name change; the project brief remains separate.

`ProjectSetupEndpoints` exposes `PUT /api/core/organizations/{organizationId}/project-setup/projects/{projectId}/name`. `ProjectSetupService.RenameAsync` requires an active human organization manager or the project's accountable human manager, validates a trimmed name of 1–512 characters, and rejects stale revisions. A same-name retry produces no additional effects. Legacy projects can be renamed without provisioning a board or delivery binding.

`ProjectSetupService.QueueNameChangeAsync` is shared with approval-bound name changes in `WorkstreamManagedActionExecutor.ChangeAsync`. It records `workstream.renamed.v1` with old/new names, revision and actor in the durable audit outbox. It updates matching project board display names while preserving custom board names. Board labels use the first 160 characters to respect their smaller database limit; the project retains its full name. It queues project realtime refreshes and retains all project, board, team, repository and work-item IDs. The API's organization transaction and advisory lock commit the state and outboxes together. The managed-action path uses its approval transaction.

The direct editor publishes the existing `WorkstreamEventNames.ChangedV2` wake with the same project ID and new revision. Approved agent changes retain their existing changed event. Events are hints: agents recover current names through authorized project/portfolio reads. `AgentEmployeeIdentityResolver.ReadProjectAssignmentAsync` resolves the current name by the persisted participant/project IDs. The Producer handles the changed event through its current-state attention review; the rename does not create another project, grant authority or issue another execution assignment.

Memory continuity uses stable identity rather than rewriting remembered text. `EmployeeMemoryNamespaces` keys memory by organization, employee/user or canonical work-item IDs, not project names. Renaming does not migrate, delete, re-extract or broaden access to those records. Historical memories and artifacts may still contain the original title. `MemoryRecallDispatchEvidence.CaptureMetadataAsync` supplies the current `projectId` and `projectName` for project-scoped chats and includes the name and revision in frozen dispatch evidence. A changed name invalidates stale prompt metadata through the existing dispatch checks. This is canonical context plus an audit record, not a new model-extracted memory or a semantic alias/merge operation.

Verification lives in `ProjectSetupTests.Naming`, `ProjectNamingEndpointTests`, `ProjectNameEditorTests`, `ProjectsWorkspaceTests.Rename_action_is_shown_only_to_authorized_project_editors`, and `AgentMemoryServiceTests.Project_chat_metadata_uses_current_name_without_changing_the_memory_or_conversation_identity`. Coverage includes current agent assignment reads, preserved execution claims and grants, repeated renames with an actual SQLite case-memory recall, approved agent changes, legacy projects, API authorization/validation, editor cancellation and stale revisions, and refreshed receipt-bound chat metadata.
