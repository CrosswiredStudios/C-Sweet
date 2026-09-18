# Direct developer applications

Daniel 1.4.1 and SDK 3.44.0 provide the general chat-to-code-to-Docker workflow with runtime-local source transfer. The configured model interprets requests and authors code; the previous fixed Hello World intake is retained only for already queued legacy work.

## Test flow

1. Update Core and install Daniel 1.4.1, reviewing its added `git.workspace.sync.v1` declaration.
2. Ask: **Build a Tetris clone and deploy it. Create your own tickets.** Without a ticket preference, Daniel presents a ticket-ownership question.
3. Follow the personal task on Work. It records repository preparation, coding/testing, source transfer, Docker build, health checking, or a specific blocker.
4. When prompted, the business owner opens Compute and grants the instance a local test link. Daniel resumes automatically and returns an application link that opens in a new tab, plus the source commit and expiry.

Source is stored in a deterministic private internal repository. Core checks the current personal-task claim and owner, installation approval, repository policy/quota, team membership and repository permissions. The agent cannot supply a remote, credential or arbitrary repository to the new `source-control.personal-work.prepare.v1` capability. Manager-created assigned work retains its existing workflow.

Personal repository creation uses the authenticated `/agent-broker/v2/workspaces/personal-repository` route from AgentHost to Core. Core resolves the persisted reservation from the owned ticket, rechecks current claim and policy, and calls GitHost using its own service credentials. AgentHost is deliberately not configured with the Core-to-GitHost credential. A lost creation response leaves the same deterministic reservation available for retry; it does not allocate another repository.

The path returned by preparation is not a shared host mount. SDK `Git.MaterializeAsync` downloads the authorized snapshot into the isolated runtime's temporary workspace, preserving existing edits. `Git.UploadAsync` transfers changed source back before publication. Requeued older tasks replace their stale broker-only path with this materialized path; lost runtimes restore the last uploaded snapshot. Transfers are bounded to 512 KiB compressed, 16 MiB content and 4,096 files. Traversal and redirected paths are rejected, and `.git`/local `.csweet` data are excluded from uploads.

Personal-task mutations now save `com.csweet.app.work-board.changed.v1` UI notifications with their state transitions, including requeue, claim and block. The board rereads current state on these hints and after reconnect, including the personal-board dialog. Notifications contain IDs/revisions rather than task content and target current authorized human readers.

## Network authority

Compute defaults do not include network grants. Untouched automatic inbound/publish grants from the earlier Hello World MVP are retired; manually edited grants are preserved. The owner-only local-link action persists instance-scoped inbound and publish-port grants and a compute-change wake event in one transaction. The grant permits only port 8080 until the VM lease expires. Broker and provider independently check authority, and browser connections revalidate access.

Outbound, private-network, inbound and public-endpoint authority are separate actions. The current Hyper-V provider supports disconnected VMs with a brokered loopback link; it does not support guest outbound internet or public endpoints. Approving a manifest is not a network grant.

## Preparation and recovery

The shared Linux-image build installs Docker and caches digest-pinned Python/Node base images. Runtime VMs have no network adapter. Image construction is trusted platform setup, not an agent network session. Upgrading the old template waits for confirmed teardown of existing resources, disables admission against the old template, and invokes the automatic installer/UAC. Provider identity and replay journals are preserved, with payload backup/rollback on failure. Existing VMs are not modified to add Docker.

Each workflow stage, command request and publication generation is retained before dispatch. Durable compute events wake only the owning correlated personal task; the SDK claims it and reads current state. A five-minute deadline recovers missed events. Known Docker build/health-check failures return to the coding model for two repair attempts. Unknown command outcomes do not authorize another command.

## MVP limits and verification

One-hour ephemeral Linux instances; 16 MiB source bundles; 30-second guest commands with 8 KiB output; cached `csweet/python:3.12` and `csweet/node:22` bases; HTTP on port 8080. Source is published before deployment. Missing dependencies and incompatible existing images produce blockers. Long builds, guest outbound dependency installation and public deployments require further provider work.

Regression coverage includes chat ticket ownership and restart recovery, personal repository ownership and claim checks, explicit network grants/revocation, automatic template upgrade gating, exact command/publication replay, build repair and links opening in a new tab. Package verification must use SDK 3.50.0 with sibling SDK references disabled. The Docker-ready image and complete live model-to-VM deployment still require hardware acceptance; the earlier live Hello World success does not verify this new path.

## Claimed planning and project source continuity

`SoftwareDeveloperAgent.EvaluatePersonalTodoClaimAsync` checks assigned compute only. The SDK's
`DrainPersonalTodoAsync` claims the ticket before invoking `HandlePersonalTodoAsync`, so the durable
Running state and Doing column precede planning inference. `PrepareDevelopmentPlanAsync` requires
Running, checkpoints the staged draft, then creates the backlog under the live claim. Repository
preparation derives a new project's name from that saved plan; reservation is no longer a prerequisite
to claiming work.

`HandleDirectWorkMessageAsync` discovers recent completed development roots on the caller's own
personal board and retains the chosen `SourceWorkItemId` in direct-work terms. The model must identify
an existing project for bug fixes/enhancements or explicitly classify a new application. Unclear or
unknown project references produce a clarification without scheduling work. Compute environment IDs
are never source identity. Ownership-choice replies preserve the retained source task.

`GitWorkspaceCapabilityHandler.PreparePersonalAsync` accepts the optional source task ID (SDK 3.50.0).
`ResolvePersonalSourceAsync` verifies same organization, installation and personal board, a completed
unarchived source task, and a valid server-written repository binding. It derives the stable original
project root. Repositories with merged task publications continue from main. Legacy delivered apps
whose main branch is still empty continue from the latest completed published source, retaining that
exact baseline until their first reviewed task merge.
`PersonalSourceBinding` is stored in `WorkTask.DevelopmentBriefJson` with the repository, root, selected
source task and pinned source commit. The live task claim authorizes this write. Later attempts cannot
change that binding. `RequirePersonalAssignmentAsync` checks the persisted root and repository policy
before source access; `PrepareAsync` uses a separate deterministic task branch and requires GitHost to
materialize the pinned commit. Missing source or revoked access fails rather than provisioning a blank
replacement. A request with no source task still uses its own deterministic repository.

Roll out the platform with Software Developer 1.10.0; old hosts ignore the additive source field.
Existing prepared work keeps its original repository. This change intentionally does not merge or
remove earlier duplicate repositories, rewrite history, or guess a source for already accepted requests.
Regression coverage: `PersonalDevelopmentWorkspaceTests`, agent `ComputeChatTests`,
`PlanningExecutionTests`, `StagedPlanningTests`, and SDK `PersonalGitWorkspaceTests`.

## Task branches, testing, and merge preferences

Implemented by `GitWorkspaceCapabilityHandler.PreparePersonalPlanTaskAsync`, `TaskDeliveryService`,
`TaskDeliveryCapabilityHandler`, and the API's `TaskDeliveryWorker`. SDK 3.50.0 exposes typed
`PlatformSourceControlClient` review and preference methods. Software Developer 1.10.0 and Software
QA 1.1.0 consume them.

The personal epic remains the coordinator. Each planned task gets its own deterministic branch and
workspace in the bound project repository, including validation and deployment tasks. Once any task
has merged, subsequent branches start from main, including tasks in later stories. Legacy root
checkpoints are retained as a baseline where no reviewed merge exists. Reusing a repository does not
merge or delete historical duplicate repositories.

`SubmitAsync` binds `TaskDeliveryReview` to a task publication and exact commit, transitions the child
task to `WaitingForApproval` (shown as Testing), and discovers active QA on the repository team. QA
gets a durable `ReviewRequested` event; it reads current assignment state and prepares that exact
commit. QA workspace authority permits inspection and snapshot transfer, never publication. A QA
failure returns the same task to Doing with evidence. Repairs are bounded and refresh the task branch
against the integration branch before retesting. Developer validation is not recorded as independent QA.

Without QA, the review records `NotAssigned` and requires the manager's decision. After QA passes,
merge permission is still checked. `PresentDecisionAsync` supplies source links, deployment review
links when available, and four choices: this task, this story, this epic, or review first. Task-only
approval is bound to the candidate commit. Story and epic approval are stored in
`TaskMergePreference`; an explicit story value overrides the epic. Missing preferences mean Ask.

`HandleDirectWorkMessageAsync` classifies preference inquiries and changes separately from development
requests. It reads current scoped settings, supports Ask/Auto/Inherit, and uses the actual retained
human message as authority. The platform checks the current manager, conversation participation,
message freshness, scope ownership and expected preference revision. Ordinary dialogue can also
approve a held task after reviewing it. Changes are audited in `WorkItemActivities`; pending approvals
and obsolete cards are invalidated. Already started merges are reconciled with their original durable
operation key, and completed merges are not undone.

`AdvanceMergeAsync` serializes authorization and preference changes through an organization advisory
lock, persists Merging before the provider call, and sends the exact candidate SHA with a stable
idempotency key. Current task/epic status, active developer/team/repository access, manager authority,
QA and merge policy are checked before starting. Provider uncertainty remains Merging for recovery;
conflicts return actionable rework. A task can complete only after a confirmed merge. The final running
review URL is retained and delivered before the deployment task and epic complete.

Review state and agent/UI outbox notifications commit together. Events are wake hints; authorized
current-state reads and bounded review discovery support duplicate/missed events. The platform worker
rotates bounded discovery, while developer deferred-work recovery and QA attention reviews recover
missed agent wakes without model polling loops.

Rollout: apply `TaskDeliveryReviewsAndMergePreferences`, rebuild/restart the platform, and upgrade
Software Developer/QA through the normal capability-grant review. The added review/preference grants
and event subscriptions are required; existing installation grants are not silently expanded. The
older team-board `WorkOrchestrator` continues to enforce its existing stage and repository policies.

Verification: `TaskDeliveryTests` covers manager scopes, review holds, preference overrides/revocation,
exact-source checks, current authority, QA evidence, conflicts and uncertain merge recovery.
`PersonalDevelopmentWorkspaceTests` covers task branch separation, source continuity and ownership.
Agent `PlanCompletionRecoveryTests`, `ComputeChatTests`, and QA event tests cover restart waits,
conversation setting changes and stale QA wake hints. Package-only builds verify the release does not
depend on sibling SDK project references. Live model/VM acceptance remains a separate deployment check.

## Source publication diagnostics

`WorkspaceOperationErrors` carries the original bounded error, a specific code, recovery action,
HTTP status and stable diagnostic ID from GitHost's internal snapshot endpoint through
`TrustedSourceControlHostClient`, `AgentWorkspaceBrokerEndpoints`, and `CoreWorkspaceBrokerClient`.
Server logs retain the exception under the same diagnostic ID. Credentials, endpoint URLs and host
paths are redacted from agent-visible messages. A legacy or proxy response reports its HTTP status
and missing diagnostic instead of falsely asserting an assignment conflict.

`SoftwareDeveloperAgent.ReadWorkspaceFailure` (Software Developer 1.10.1) unwraps the broker error
and renders separate reported-error, failure-code, diagnostic-ID and recovery fields in ticket
comments. A publication key reused with different content is reported as
`workspace.publication_content_changed`; stale assignments, superseded publications and changed
branches have different codes and actions. This requires the updated GitHost, Core and AgentHost
alongside the agent. Existing historical comments are not rewritten. Regression coverage:
`WorkspaceOperationDiagnosticsTests`, `InternalGitPublicationTests`, and agent `DeploymentDiagnosticTests`.
