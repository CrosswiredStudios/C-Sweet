using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Communications;
using CSweet.Contracts.Core;
using CSweet.Contracts.Plugins;
using CSweet.Contracts.WorkManagement;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using W = CSweet.WorkManagement.Contracts;

namespace CSweet.AgentHost.Broker;

public enum McpToolExecutionPolicy
{
    ReadOnly,
    AdvisoryWrite,
    ApprovalCreating,
    PlatformOnly
}

public enum McpToolAvailability
{
    GrantRequired,
    PlatformOnly
}

public sealed record McpToolDescriptor(
    string Capability,
    string Name,
    string Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema,
    McpToolExecutionPolicy ExecutionPolicy,
    McpToolAvailability Availability = McpToolAvailability.GrantRequired,
    bool ModelVisible = true,
    Guid? ProviderInstallationId = null,
    int ExecutionTimeoutSeconds = 30,
    string RiskClass = "standard",
    string ScopeResolver = "organization-and-installation",
    int MaximumInputBytes = 64 * 1024,
    int MaximumOutputBytes = 1024 * 1024,
    string QuotaClass = "standard",
    string ApprovalBehavior = "none",
    string OwningService = "platform");

public sealed class McpToolCatalog(IEnumerable<IPlatformCapabilityHandler> handlers)
{
    private static readonly JsonElement EmptyInput = Schema("""
        { "type": "object", "properties": {}, "additionalProperties": false }
        """);
    private static readonly JsonElement ObjectOutput = Schema("""
        { "type": "object" }
        """);
    private static readonly JsonElement ArrayOutput = Schema("""
        { "type": "array", "items": { "type": "object" } }
        """);

    private static readonly IReadOnlyList<McpToolDescriptor> Tools =
    [
        Read(PlatformCapabilities.BusinessProfileRead, "read_business_profile",
            "Read the authoritative business profile for this organization."),
        Write(PlatformCapabilities.BusinessProfileUpdateExplicit, "update_explicit_business_profile",
            "Save low-risk facts explicitly stated by the owner, with conversation and message provenance."),
        Approval(PlatformCapabilities.BusinessProfileProposeUpdate, "propose_business_profile_update",
            "Propose inferred or sensitive business-profile changes for owner approval."),
        Read(PlatformCapabilities.OrganizationSnapshotRead, "read_organization_snapshot",
            "Read current staff, roles, reporting lines, objectives, workstreams, workers, and operating signals."),
        Read(PlatformCapabilities.TeamRosterRead, "read_team_roster",
            "Read only this agent employee's active team roster, with bounded teammate identity and role facts."),
        HiddenRead(PlatformCapabilities.AgentOperatingStateRead, "read_agent_operating_state",
            "Read this installation's revision-controlled operating assessment checkpoint."),
        HiddenWrite(PlatformCapabilities.AgentOperatingStateWrite, "write_agent_operating_state",
            "Write this installation's bounded operating assessment checkpoint with optimistic concurrency."),
        Read(PlatformCapabilities.BusinessPatternSearch, "search_business_patterns",
            "Find stage-appropriate operating patterns from broker-approved sources."),
        Approval(PlatformCapabilities.WorkstreamPlanPropose, "propose_workstream_plan",
            "Propose a managed workstream with one accountable manager."),
        Read(W.WorkstreamCapabilityNames.ReadV1, "read_workstream",
            "Read one visible Workstream, including its immutable profile binding and current revision."),
        Approval(W.WorkstreamCapabilityNames.PlanProposeV2, "propose_profiled_workstream",
            "Propose a profile-bound Workstream, accountable manager, authority envelope, milestones, and gates."),
        Approval(W.WorkstreamCapabilityNames.ChangeProposeV1, "propose_workstream_change",
            "Propose an optimistic-concurrency Workstream change bound to its current profile digest."),
        Read(W.WorkstreamCapabilityNames.GateReadV1, "read_workstream_gates",
            "Read lifecycle gates for a visible Workstream."),
        Write(W.WorkstreamCapabilityNames.GateSubmitV1, "submit_workstream_gate",
            "Submit an exact gate revision with structured evidence references."),
        Write(W.WorkstreamCapabilityNames.GateDecideV1, "decide_workstream_gate",
            "Decide a submitted gate within the Workstream authority envelope, with structured findings."),
        Read(W.WorkstreamCapabilityNames.PortfolioReadV1, "read_management_portfolio",
            "Read all Workstreams assigned to this employee for management or supervision without requiring team membership."),
        Read(W.WorkstreamCapabilityNames.TeamRosterReadV2, "read_scoped_team_roster",
            "Read the active team roster for a visible Workstream or an explicitly scoped team."),
        Write(W.DecisionCapabilityNames.RequestV1, "request_workstream_decision",
            "Create one durable, correlated decision request with options, evidence, deadline, and blocking impact."),
        Read(W.DecisionCapabilityNames.ReadV1, "read_workstream_decisions",
            "Read durable decisions by id or Workstream."),
        Write(W.DecisionCapabilityNames.DecideV1, "decide_workstream_decision",
            "Select an option for a pending decision when authorized by the Workstream authority envelope."),
        Read(W.DeliveryEvidenceCapabilityNames.ToolchainCatalogReadV2, "read_eligible_toolchains",
            "Read only automation-certified toolchain adapters compatible with requested targets and operations."),
        Write(W.DeliveryEvidenceCapabilityNames.BuildRequestV2, "request_delivery_build",
            "Request a reproducible build from an exact source revision using a certified adapter."),
        Read(W.DeliveryEvidenceCapabilityNames.BuildReadV2, "read_delivery_builds",
            "Read build status, immutable source provenance, and output evidence for a visible Workstream."),
        HiddenWrite(W.DeliveryEvidenceCapabilityNames.BuildClaimV1, "claim_delivery_build",
            "Claim a queued build with an optimistic revision and expiring execution lease."),
        HiddenWrite(W.DeliveryEvidenceCapabilityNames.BuildHeartbeatV1, "heartbeat_delivery_build",
            "Renew the active build execution lease while preserving claim ownership."),
        HiddenWrite(W.DeliveryEvidenceCapabilityNames.BuildReportV2, "report_delivery_build",
            "Report revision-bound build outputs, provenance, and validations as the certified adapter provider."),
        Write(W.DeliveryEvidenceCapabilityNames.BuildCancelV1, "cancel_delivery_build",
            "Request cancellation of a queued or running build and retain the audit reason."),
        Read(W.DeliveryEvidenceCapabilityNames.ValidationReadV2, "read_delivery_validations",
            "Read structured validation findings and evidence for builds in a visible Workstream."),
        Write(W.DeliveryEvidenceCapabilityNames.PreviewCreateV2, "create_delivery_preview",
            "Create a time-bounded preview from a successful published build."),
        Read(W.DeliveryEvidenceCapabilityNames.PreviewReadV2, "read_delivery_previews",
            "Read preview status and bounded access references for a visible Workstream."),
        Write(W.DeliveryEvidenceCapabilityNames.EvaluationPlanV1, "plan_evaluation_session",
            "Plan a consent-governed evaluation session bound to a Workstream and optional build."),
        Read(W.DeliveryEvidenceCapabilityNames.EvaluationReadV1, "read_evaluation_sessions",
            "Read evaluation plans, status, evidence, and provenance for a visible Workstream."),
        Write(W.DeliveryEvidenceCapabilityNames.EvaluationReportV1, "report_evaluation_session",
            "Complete an evaluation session with a revision-bound structured report and evidence."),
        Read(W.DeliveryEvidenceCapabilityNames.ReleaseReadinessReadV1, "read_release_readiness",
            "Read release-readiness evidence and blocking findings for a visible Workstream."),
        Write(W.DeliveryEvidenceCapabilityNames.ReleaseReadinessSubmitV1, "submit_release_readiness",
            "Submit typed release-readiness evidence; blocking findings prevent Ready status."),
        Approval(W.DeliveryEvidenceCapabilityNames.PublicationProposeV1, "propose_publication",
            "Propose a public publication from a Ready release record. Public mutation always remains human-gated."),
        Read(PlatformCapabilities.WorkforceSearch, "search_workforce",
            "Search current staff and connected human workforce providers. Installable agent listings require the separate agent-catalog grant."),
        Read(AgentCatalogCapabilities.Search, "get_available_agents",
            "Search organization-installed, local-directory, first-party, and marketplace agents without importing, installing, hiring, or spending."),
        Approval(PlatformCapabilities.WorkforcePlanPropose, "propose_workforce_plan",
            "Propose a workforce plan without installing, hiring, contacting, or spending."),
        Read(PlatformCapabilities.FinanceProfileRead, "read_finance_profile",
            "Read authoritative financial goals and workforce controls."),
        Approval(PlatformCapabilities.FinanceProfileProposeUpdate, "propose_finance_profile_update",
            "Propose changes to financial goals or controls for owner approval."),
        Write(PlatformCapabilities.BudgetEvaluate, "evaluate_budget",
            "Evaluate a proposed cost against enforceable budgets; reservations remain platform controlled."),
        Approval(PlatformCapabilities.ApprovalPropose, "propose_approval",
            "Create a durable, separately gated action proposal."),
        Read(PlatformCapabilities.ManagementCycleRead, "read_management_cycle",
            "Read management cadence, executive briefing schedule, and quiet hours."),
        Write(CommunicationHubCapabilities.AskUser, "ask_user",
            "Ask the user one structured multiple-choice question with two to four mutually exclusive options. Put the recommended option first. The UI automatically adds Something else with a free-text response."),
        HiddenWrite(CommunicationCapabilities.ChatCreate, "create_communication_chat",
            "Create or reuse a granted direct chat, or create a granted group chat."),
        HiddenRead(CommunicationCapabilities.ChatRead, "read_communication_chat",
            "Read the caller's communication directory or one visible chat."),
        HiddenWrite(CommunicationCapabilities.ChatModify, "modify_communication_chat",
            "Modify a granted group chat."),
        HiddenWrite(CommunicationCapabilities.ChatDelete, "archive_communication_chat",
            "Archive a granted group chat while retaining its history."),
        HiddenWrite(CommunicationCapabilities.MessageSend, "send_communication_message",
            "Persist a message and start the recipient turn when the direct recipient is an agent."),
        HiddenWrite(CommunicationCapabilities.CoordinationStart, "start_agent_coordination",
            "Start a durable, bounded-authority collaboration with one exact agent employee."),
        HiddenWrite(CommunicationCapabilities.CoordinationStartWork, "start_work_item_coordination",
            "Start a six-turn technical-support collaboration pinned to an exact work assignment."),
        HiddenWrite(CommunicationCapabilities.CoordinationStartBoard, "start_board_coordination",
            "Start or resume bounded collaboration scoped to one exact shared board."),
        HiddenWrite(CommunicationCapabilities.CoordinationRespond, "respond_to_agent_coordination",
            "Continue, complete, or block the current durable coordination turn."),
        HiddenRead(CommunicationCapabilities.CoordinationRead, "read_agent_coordination",
            "Read a coordination session visible to the calling participant."),
        HiddenRead(CommunicationCapabilities.CoordinationList, "list_agent_coordination",
            "List coordination sessions in which the calling agent is a participant."),
        HiddenWrite(CommunicationCapabilities.CoordinationResume, "resume_agent_coordination",
            "Resume the calling initiator's failed or blocked coordination session."),
        HiddenWrite(CommunicationCapabilities.CoordinationCancel, "cancel_agent_coordination",
            "Cancel a coordination session when separately authorized."),
        Write(SuggestedUserActionCapabilities.Suggest, "suggest_user_action",
            "Attach a safe, platform-resolved workflow action to this agent's message or chat turn. Use hiring.marketplace.browse.v1 with a role to let the user browse candidates."),
        Read(HiringCapabilities.ListRecommendations, "list_hiring_recommendations",
            "Read this agent installation's role backlog in priority order."),
        Write(HiringCapabilities.UpsertRecommendation, "upsert_hiring_recommendation",
            "Create or update a prioritized role in this agent installation's hiring backlog. Candidate references may be omitted until sourcing begins."),
        Write(HiringCapabilities.ResolveRecommendation, "resolve_hiring_recommendation",
            "Resolve one hiring recommendation owned by this installation after an unambiguous matching employee hire."),
        Write(HiringCapabilities.WithdrawRecommendation, "withdraw_hiring_recommendation",
            "Withdraw a role suggestion owned by this installation when an approved resource plan no longer needs it."),
        Approval(ResourceChangeCapabilities.Propose, "propose_resource_change",
            "Propose one atomic desired-team change for approval by the requesting employee's current manager."),
        Read(ResourceChangeCapabilities.Read, "read_resource_changes",
            "Read resource-change requests visible to this requester, assigned manager, or active Chief of Staff."),
        Write(ResourceChangeCapabilities.Decide, "decide_resource_change",
            "Approve, request revision of, or reject a resource-change request when this agent is the current manager."),
        HiddenWrite(StaffingReplenishmentCapabilities.Propose, "propose_staffing_replenishment",
            "Submit one deduplicated replacement hiring plan against an approved desired-team baseline."),
        HiddenRead(StaffingReplenishmentCapabilities.Read, "read_staffing_replenishments",
            "Read staffing-replenishment requests visible to this requester or manager."),
        HiddenWrite(StaffingReplenishmentCapabilities.Decide, "decide_staffing_replenishment",
            "Approve, request revision of, or reject a direct report's replacement hiring plan."),
        Approval(HiringCapabilities.StageWorkflow, "stage_hiring_workflow",
            "Stage a combined install-and-hire proposal for explicit organization-owner approval. This does not install or hire directly."),
        Read(WorkBoardActions.Read, "list_work_boards",
            "List operational boards covered by this installation's scoped board grants."),
        Read(WorkItemActions.Read, "read_work_board",
            "Read a granted board or one assigned work item when itemId is provided."),
        Read(WorkItemActions.ReadTypes, "read_work_item_types",
            "Read the immutable platform work-type, board-profile, and approval-policy catalog."),
        Write(WorkBoardActions.Create, "create_work_board",
            "Create an operational board with default To Do and Done columns."),
        Write(WorkBoardActions.Configure, "configure_work_board",
            "Configure a managed board's name and description using optimistic concurrency."),
        Write(WorkBoardActions.ConfigureColumns, "configure_work_board_columns",
            "Configure an exact ordered workflow on a granted board using optimistic concurrency."),
        Write(WorkItemActions.Create, "create_work_item",
            "Create a catalog-typed work item on a compatible granted board; the platform derives its hierarchy kind."),
        HiddenWrite(WorkItemActions.RevisePlanning, "revise_work_item_planning",
            "Apply one architecture-relevant planning revision with optimistic concurrency."),
        HiddenWrite(WorkItemActions.DecideApproval, "decide_work_item_approval",
            "Approve, request changes, or explicitly waive one current work-item approval policy."),
        Write(WorkItemActions.Comment, "comment_on_work_item",
            "Add a durable comment to a work item on a granted board."),
        Read(WorkItemActions.ReadComments, "read_work_item_comments",
            "Read correlated comments and architecture guidance linked to a granted work item."),
        Write(WorkItemActions.Estimate, "estimate_work_item",
            "Set or clear a work item's story-point estimate."),
        Write(WorkItemActions.Move, "move_work_item",
            "Move a non-terminal work item between non-terminal workflow columns."),
        Write(WorkItemActions.Start, "start_work_item",
            "Claim an assigned work item by moving it to the board's first In Progress column."),
        Write(WorkItemActions.Complete, "complete_work_item",
            "Complete a work item by moving it to a Done column."),
        Write(WorkItemActions.Cancel, "cancel_work_item",
            "Cancel a work item by moving it to a Cancelled column."),
        Write(WorkItemActions.Reopen, "reopen_work_item",
            "Reopen a completed or cancelled work item into To Do or In Progress."),
        Write(WorkItemActions.Transfer, "transfer_work_item",
            "Transfer one canonical, non-hierarchical work item between two granted boards."),
        Read(PersonalTodoActions.Read, "list_personal_todos",
            "List this employee's personal board and boards in its recursive reporting subtree."),
        Write(PersonalTodoActions.Add, "add_personal_todo",
            "Add durable personal work for this employee or one reporting descendant. Omit targetOrganizationUserId for self."),
        Write(PersonalTodoActions.Reorder, "reorder_personal_todo",
            "Reorder ready personal work on a granted personal board."),
        Write(PersonalTodoActions.Requeue, "requeue_personal_todo",
            "Return a blocked personal work item to the ready queue."),
        Write(PersonalTodoActions.Activate, "activate_personal_todo",
            "Promote one backlog personal work item into the ready queue."),
        Write(PersonalTodoActions.Update, "update_personal_todo",
            "Update this agent's canonical personal work item."),
        Write(PersonalTodoActions.Archive, "archive_personal_todo",
            "Archive this agent's personal work item while retaining history."),
        Write(PersonalTodoActions.Restore, "restore_personal_todo",
            "Restore an archived personal work item."),
        HiddenWrite(PersonalTodoActions.Claim, "claim_personal_todo",
            "Atomically claim the next personal work item for SDK-managed execution."),
        HiddenWrite(PersonalTodoActions.Complete, "complete_personal_todo",
            "Complete SDK-managed personal work."),
        HiddenWrite(PersonalTodoActions.Block, "block_personal_todo",
            "Block SDK-managed personal work with a durable reason."),
        HiddenWrite(PersonalTodoActions.Release, "release_personal_todo",
            "Release SDK-managed personal work after a retryable callback failure."),
        HiddenWrite(PersonalTodoActions.Defer, "defer_personal_todo",
            "Defer SDK-managed personal work until a platform-scheduled review."),
        Write(ArtifactPlatformCapabilities.Create, "create_artifact",
            "Create one Markdown document and receive read, revise, and submit grants only for that new file."),
        Read(ArtifactPlatformCapabilities.Read, "get_artifact",
            "List explicitly granted documents or read one exact granted document."),
        Write(ArtifactPlatformCapabilities.Revise, "create_artifact_revision",
            "Create an immutable Markdown revision using optimistic concurrency."),
        Write(ArtifactPlatformCapabilities.Submit, "submit_artifact_revision",
            "Submit the latest document revision and enqueue its reviewer chat cycle."),
        Write(ArtifactPlatformCapabilities.Decide, "decide_artifact_revision",
            "Accept or request changes to one exact submitted revision when explicitly granted."),
        Write(ArtifactPlatformCapabilities.DecideV2, "decide_artifact_revision_structured",
            "Decide an exact revision and digest with a typed rubric, structured findings, blocking severity, and follow-up references."),
        Approval(ArtifactPlatformCapabilities.RequestAccess, "request_artifact_access",
            "Request human approval for exact actions on one exact document."),
        Write(ArtifactPlatformCapabilities.PackageCreate, "create_artifact_package",
            "Create a package from documents that are all explicitly readable."),
        Read(ArtifactPlatformCapabilities.PackageRead, "get_artifact_package",
            "Read a package only when every member document is explicitly readable."),
        Write(ArtifactPlatformCapabilities.PackageSubmit, "submit_artifact_package",
            "Submit a document package for review when every member is explicitly granted."),
        Write(ArtifactPlatformCapabilities.PackageDecide, "decide_artifact_package",
            "Accept a complete package whose exact member documents are explicitly decidable."),
        Write(GitWorkspaceCapabilities.Prepare, "prepare_git_workspace",
            "Materialize the assigned repository as a credential-free snapshot; Core derives its repository and ref."),
        Write(GitWorkspaceCapabilities.Refresh, "refresh_git_workspace",
            "Refresh an assigned credential-free snapshot against its authorized base and return bounded conflicts."),
        Read(GitWorkspaceCapabilities.Inspect, "inspect_git_workspace",
            "Inspect bounded changes in an assigned credential-free ticket workspace."),
        Write(GitWorkspaceCapabilities.Publish, "publish_git_workspace",
            "Ask trusted GitHost to reconstruct, commit, and publish the assigned change artifact."),
        Write(GitWorkspaceCapabilities.Cleanup, "cleanup_git_workspace",
            "Remove a successful ticket workspace or retain a failed workspace for recovery."),
        Read(SourceControlCapabilities.TeamRepositoryOptions, "list_team_repository_options",
            "List safe repository metadata enabled by the current team's delivery policy."),
        Write(SourceControlCapabilities.ProvisionRepository, "provision_source_control_repository",
            "Request one policy-bounded private Managed GitHub code project for an approved product or workstream."),
        Read(GitMergeCapabilities.Review, "review_git_merge",
            "Review the exact candidate SHA, QA evidence, and required checks as the canonical team lead."),
        Write(GitMergeCapabilities.Authorize, "authorize_git_merge",
            "Approve or reject the exact reviewed candidate SHA as the canonical team lead."),
        Read(WorkSprintActions.Read, "list_work_sprints",
            "List planned, active, and closed sprints on a granted board."),
        Write(WorkSprintActions.Create, "create_work_sprint",
            "Create a planned sprint with an optional goal and schedule."),
        Write(WorkSprintActions.ManageScope, "set_work_item_sprint",
            "Assign a work item to a planned or active sprint, or return it to the backlog."),
        Write(WorkSprintActions.ManageCapacity, "set_work_sprint_capacity",
            "Set or clear the point-capacity target for a planned or active sprint."),
        Write(WorkSprintActions.CarryOver, "carry_over_work_sprint",
            "Move selected or all incomplete current items from a closed sprint into a planned or active sprint."),
        Read(WorkSprintActions.ReadReports, "read_work_sprint_report",
            "Read immutable completion snapshots, burndown history, and velocity/capacity forecasts for a board."),
        Write(WorkOrchestrationActions.ConfigureProfile, "configure_profile_orchestration",
            "Atomically apply a pinned Workstream profile's generic board workflow and publish its immutable orchestration policy revision."),
        Read(WorkFlowMetricActions.Read, "read_work_flow_metrics",
            "Read trusted team and stage-flow metrics computed from authoritative sprint, execution, attempt, event, and work-item records."),
        Write(WorkOrchestrationActions.Preflight, "preflight_work_sprint",
            "Validate that a planned sprint, policy, dependencies, and exact stage assignments are executable."),
        Read(WorkOrchestrationActions.Read, "read_work_orchestration",
            "Read authoritative sprint, item, and stage execution state."),
        Write(WorkOrchestrationActions.Start, "start_orchestrated_work_sprint",
            "Intentionally authorize and start a validated sprint as its assigned board manager."),
        Write(WorkOrchestrationActions.Pause, "pause_orchestrated_work_sprint",
            "Pause dispatch for an active sprint as its assigned board manager."),
        Write(WorkOrchestrationActions.Resume, "resume_orchestrated_work_sprint",
            "Resume dispatch for a paused sprint as its assigned board manager."),
        Write(WorkOrchestrationActions.Cancel, "cancel_orchestrated_work_sprint",
            "Cancel an active or paused sprint and its outstanding work as its assigned board manager."),
        HiddenWrite(WorkOrchestrationActions.Retry, "retry_blocked_work_stage",
            "Request a governed retry for one exact blocked stage and unchanged assignment."),
        Write(WorkOrchestrationActions.ConfigureSoftwareTemplate, "configure_software_delivery_template",
            "Publish the bounded software delivery workflow for a granted team board."),
    ];

    static McpToolCatalog()
    {
        var duplicateNames = Tools.GroupBy(x => x.Name, StringComparer.Ordinal)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToArray();
        var duplicateCapabilities = Tools.GroupBy(x => x.Capability, StringComparer.Ordinal)
            .Where(x => x.Count() > 1).Select(x => x.Key).ToArray();
        if (duplicateNames.Length > 0 || duplicateCapabilities.Length > 0)
            throw new InvalidOperationException(
                $"The capability registry contains duplicates. Tools: {string.Join(", ", duplicateNames)}; capabilities: {string.Join(", ", duplicateCapabilities)}.");
        foreach (var tool in Tools)
        {
            RequireObjectSchema(tool.Capability, "input", tool.InputSchema);
            JsonSchemaValidator.ValidateSchema(tool.InputSchema);
            if (tool.OutputSchema is { } output)
            {
                RequireOutputSchema(tool.Capability, output);
                JsonSchemaValidator.ValidateSchema(output);
            }
        }
    }

    public IReadOnlyList<McpToolDescriptor> List(IReadOnlySet<string> grantedCapabilities) =>
        Tools.Where(tool => tool.Availability != McpToolAvailability.PlatformOnly &&
                             grantedCapabilities.Contains(tool.Capability))
            .Concat(grantedCapabilities
                .Where(capability => Tools.All(x => x.Capability != capability) &&
                                     handlers.Any(x => x.CanHandle(capability)))
                .Select(capability => new McpToolDescriptor(
                    capability,
                    ToToolName(capability),
                    $"Invoke the granted C-Sweet capability {capability}.",
                    Schema("""{"type":"object","additionalProperties":true}"""),
                    ObjectOutput,
                    McpToolExecutionPolicy.PlatformOnly,
                    McpToolAvailability.GrantRequired,
                    ModelVisible: false,
                    OwningService: "platform")))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToList();

    public McpToolDescriptor? Find(string name, IReadOnlySet<string> grantedCapabilities) =>
        List(grantedCapabilities).SingleOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));

    public async Task<IReadOnlyList<McpToolDescriptor>> ListAsync(
        AgentSession session,
        CSweetDbContext db,
        CancellationToken cancellationToken)
    {
        var requesterId = Guid.Parse(session.InstallationId);
        var requesterManifestJson = await db.AgentInstallations.AsNoTracking()
            .Where(x => x.Id == requesterId)
            .Select(x => x.PackageVersion!.ManifestJson)
            .SingleAsync(cancellationToken);
        var requesterManifest = JsonSerializer.Deserialize<PluginManifest>(
            requesterManifestJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var requirements = (requesterManifest?.Requires ?? [])
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Single(), StringComparer.Ordinal);
        var tools = List(session.Grant.RequiredCapabilities)
            .Select(tool => tool with
            {
                ModelVisible = tool.ModelVisible &&
                    requirements.TryGetValue(tool.Capability, out var requirement) &&
                    requirement.ModelVisible
            })
            .ToList();
        var bindings = await db.AgentCapabilityBindings.AsNoTracking()
            .Where(x => x.RequesterInstallationId == requesterId &&
                        x.OrganizationId == session.BusinessId &&
                        x.GrantRevision == session.Grant.Revision &&
                        x.RevokedAt == null &&
                        x.ProviderInstallation != null &&
                        x.ProviderInstallation.IsEnabled &&
                        x.ProviderInstallation.BusinessId == session.BusinessId &&
                        x.ProviderInstallation.RevisionStatus == PluginRevisionStatus.Active)
            .Include(x => x.ProviderInstallation!)
                .ThenInclude(x => x.PackageVersion)
            .ToListAsync(cancellationToken);
        foreach (var binding in bindings)
        {
            if (!session.Grant.RequiredCapabilities.Contains(binding.Capability))
                continue;
            var manifest = JsonSerializer.Deserialize<PluginManifest>(
                binding.ProviderInstallation!.PackageVersion!.ManifestJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var declaration = manifest?.Provides.SingleOrDefault(
                x => string.Equals(x.Name, binding.Capability, StringComparison.Ordinal));
            if (declaration is null ||
                declaration.InputSchema.ValueKind != JsonValueKind.Object ||
                declaration.OutputSchema.ValueKind != JsonValueKind.Object)
                continue;
            JsonSchemaValidator.ValidateSchema(declaration.InputSchema);
            JsonSchemaValidator.ValidateSchema(declaration.OutputSchema);
            tools.Add(new McpToolDescriptor(
                declaration.Name,
                ToToolName(declaration.Name),
                declaration.Description,
                declaration.InputSchema,
                declaration.OutputSchema,
                McpToolExecutionPolicy.AdvisoryWrite,
                ModelVisible: requirements.TryGetValue(binding.Capability, out var requesterRequirement) &&
                              requesterRequirement.ModelVisible,
                ProviderInstallationId: binding.ProviderInstallationId,
                ExecutionTimeoutSeconds: declaration.ExecutionTimeoutSeconds,
                RiskClass: declaration.RiskClass,
                ApprovalBehavior: "policy-dependent",
                OwningService: $"provider:{manifest!.Id}"));
        }
        return tools
            .GroupBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Single())
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<McpToolDescriptor?> FindAsync(
        string name,
        AgentSession session,
        CSweetDbContext db,
        CancellationToken cancellationToken) =>
        (await ListAsync(session, db, cancellationToken))
        .SingleOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));

    private static McpToolDescriptor Read(string capability, string name, string description) =>
        new(capability, name, description, InputFor(capability), OutputFor(capability), McpToolExecutionPolicy.ReadOnly,
            RiskClass: "read-only", OwningService: OwnerFor(capability));

    private static McpToolDescriptor Write(string capability, string name, string description) =>
        new(capability, name, description, InputFor(capability), OutputFor(capability), McpToolExecutionPolicy.AdvisoryWrite,
            RiskClass: "reversible-write", ApprovalBehavior: "policy-dependent", OwningService: OwnerFor(capability));

    private static McpToolDescriptor Approval(string capability, string name, string description) =>
        new(capability, name, description, InputFor(capability), OutputFor(capability), McpToolExecutionPolicy.ApprovalCreating,
            RiskClass: "approval-required", ApprovalBehavior: "always-create-approval", OwningService: OwnerFor(capability));

    private static McpToolDescriptor HiddenRead(string capability, string name, string description) =>
        Read(capability, name, description) with { ModelVisible = false };

    private static McpToolDescriptor HiddenWrite(string capability, string name, string description) =>
        Write(capability, name, description) with { ModelVisible = false };

    private static JsonElement OutputFor(string capability) => capability switch
    {
        WorkBoardActions.Read or
        WorkSprintActions.Read or
        W.WorkstreamCapabilityNames.GateReadV1 or
        W.DecisionCapabilityNames.ReadV1 or
        W.DeliveryEvidenceCapabilityNames.ToolchainCatalogReadV2 or
        W.DeliveryEvidenceCapabilityNames.BuildReadV2 or
        W.DeliveryEvidenceCapabilityNames.ValidationReadV2 or
        W.DeliveryEvidenceCapabilityNames.PreviewReadV2 or
        W.DeliveryEvidenceCapabilityNames.EvaluationReadV1 or
        W.DeliveryEvidenceCapabilityNames.ReleaseReadinessReadV1 or
        SourceControlCapabilities.TeamRepositoryOptions => ArrayOutput,
        _ => ObjectOutput
    };

    private static string OwnerFor(string capability) =>
        capability.StartsWith("communication.", StringComparison.Ordinal) ? "communication-hub" :
        capability.StartsWith("memory.", StringComparison.Ordinal) ? "memory" :
        capability.Contains("hiring", StringComparison.Ordinal) ? "workforce" :
        capability.StartsWith("platform.agent-catalog", StringComparison.Ordinal) ? "marketplace" :
        "platform";

    private static JsonElement InputFor(string capability)
    {
        if (capability == ResourceChangeCapabilities.Propose)
            return Schema("""
                {"type":"object","required":["conversationId","chatTurnId","productGoal","rationale","contextRevision","roles","assumptions","constraints","idempotencyKey"],"properties":{"conversationId":{"type":"string","format":"uuid"},"chatTurnId":{"type":"string","format":"uuid"},"productGoal":{"type":"string","minLength":1,"maxLength":2048},"rationale":{"type":"string","minLength":1,"maxLength":4096},"contextRevision":{"type":"integer"},"teamKey":{"type":["string","null"],"maxLength":200},"teamName":{"type":["string","null"],"maxLength":160},"teamDescription":{"type":["string","null"],"maxLength":2048},"teamId":{"type":["string","null"],"format":"uuid"},"workstreamId":{"type":["string","null"],"format":"uuid"},"expectedTeamRevision":{"type":["integer","null"],"minimum":0},"roles":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"object"}},"evidence":{"type":"array","maxItems":50,"items":{"type":"object"}},"alternativesConsidered":{"type":"array","maxItems":20,"items":{"type":"string","maxLength":2048}},"expectedEffect":{"type":["string","null"],"maxLength":2048},"assumptions":{"type":"array","maxItems":20,"items":{"type":"string"}},"constraints":{"type":"array","maxItems":20,"items":{"type":"string"}},"supersedesRequestId":{"type":["string","null"],"format":"uuid"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
                """);
        if (capability == W.WorkstreamCapabilityNames.PlanProposeV2)
            return Schema("""
                {"type":"object","required":["name","outcome","successCriteria","lifecycleStage","accountableManagerOrganizationUserId","initialSupervisors","requiredCapabilities","rationale","idempotencyKey","profileKey","profileVersion","profileData","authorityEnvelope","initialMilestones","initialEvidence"],"properties":{"name":{"type":"string","minLength":1,"maxLength":240},"outcome":{"type":"string","minLength":1,"maxLength":4000},"successCriteria":{"type":"array"},"lifecycleStage":{"type":"string"},"accountableManagerOrganizationUserId":{"type":"string","format":"uuid"},"initialTeamId":{"type":["string","null"],"format":"uuid"},"initialSupervisors":{"type":"array"},"requiredCapabilities":{"type":"array"},"strategicObjectiveId":{"type":["string","null"],"format":"uuid"},"targetDate":{"type":["string","null"],"format":"date-time"},"proposedBudgetAmount":{"type":["number","null"]},"proposedBudgetCurrency":{"type":["string","null"]},"rationale":{"type":"string"},"idempotencyKey":{"type":"string"},"profileKey":{"type":"string"},"profileVersion":{"type":"integer","minimum":1},"profileData":{"type":"object"},"authorityEnvelope":{"type":"object"},"initialMilestones":{"type":"array"},"initialEvidence":{"type":"array"}},"additionalProperties":false}
                """);
        if (capability == WorkItemActions.Create)
            return Schema("""
                {"type":"object","required":["boardId","title","typeKey","priority","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"title":{"type":"string","minLength":1,"maxLength":512},"description":{"type":["string","null"],"maxLength":8192},"kind":{"type":"string","enum":["Initiative","Epic","Story","Task","Bug"]},"typeKey":{"type":"string","minLength":1,"maxLength":200},"priority":{"type":"string","enum":["Low","Medium","High","Critical"]},"columnId":{"type":["string","null"],"format":"uuid"},"parentItemId":{"type":["string","null"],"format":"uuid"},"dueDate":{"type":["string","null"],"format":"date-time"},"planning":{"type":["object","null"]},"delivery":{"type":["object","null"]},"proposalProvenance":{"type":["object","null"]},"accountableOrganizationUserId":{"type":["string","null"],"format":"uuid"},"stageAssignments":{"type":"array"},"mentions":{"type":"array"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
                """);
        return capability switch
        {
        W.WorkstreamCapabilityNames.ReadV1 => Schema("""
            {"type":"object","required":["workstreamId"],"properties":{"workstreamId":{"type":"string","format":"uuid"}},"additionalProperties":false}
            """),
        W.WorkstreamCapabilityNames.PlanProposeV2 => Schema("""
            {"type":"object","required":["name","outcome","successCriteria","lifecycleStage","accountableManagerOrganizationUserId","initialSupervisors","requiredCapabilities","rationale","idempotencyKey","profileKey","profileVersion","profileData","authorityEnvelope","initialMilestones"],"properties":{"name":{"type":"string","minLength":1,"maxLength":240},"outcome":{"type":"string","minLength":1,"maxLength":4000},"successCriteria":{"type":"array","items":{"type":"string"}},"lifecycleStage":{"type":"string","minLength":1,"maxLength":80},"accountableManagerOrganizationUserId":{"type":"string","format":"uuid"},"initialTeamId":{"type":["string","null"],"format":"uuid"},"initialSupervisors":{"type":"array","items":{"type":"object","required":["supervisorOrganizationUserId","roleKey"],"properties":{"supervisorOrganizationUserId":{"type":"string","format":"uuid"},"roleKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}},"requiredCapabilities":{"type":"array","items":{"type":"string"}},"strategicObjectiveId":{"type":["string","null"],"format":"uuid"},"targetDate":{"type":["string","null"],"format":"date-time"},"proposedBudgetAmount":{"type":["number","null"],"minimum":0},"proposedBudgetCurrency":{"type":["string","null"],"maxLength":8},"rationale":{"type":"string","minLength":1,"maxLength":4000},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200},"profileKey":{"type":"string","minLength":1,"maxLength":200},"profileVersion":{"type":"integer","minimum":1},"profileData":{"type":"object"},"authorityEnvelope":{"type":"object"},"initialMilestones":{"type":"array","items":{"type":"object"}}},"additionalProperties":false}
            """),
        W.WorkstreamCapabilityNames.ChangeProposeV1 => Schema("""
            {"type":"object","required":["workstreamId","expectedRevision","summary","changes","rationale","idempotencyKey"],"properties":{"workstreamId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"summary":{"type":"string","minLength":1,"maxLength":1000},"changes":{"type":"object"},"rationale":{"type":"string","minLength":1,"maxLength":4000},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200}},"additionalProperties":false}
            """),
        W.WorkstreamCapabilityNames.GateReadV1 => Schema("""
            {"type":"object","required":["workstreamId"],"properties":{"workstreamId":{"type":"string","format":"uuid"},"gateId":{"type":["string","null"],"format":"uuid"}},"additionalProperties":false}
            """),
        W.WorkstreamCapabilityNames.GateSubmitV1 => Schema("""
            {"type":"object","required":["workstreamId","gateId","expectedRevision","evidence","summary","idempotencyKey"],"properties":{"workstreamId":{"type":"string","format":"uuid"},"gateId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"evidence":{"type":"array","items":{"type":"object"}},"summary":{"type":"string","minLength":1,"maxLength":4000},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200}},"additionalProperties":false}
            """),
        W.WorkstreamCapabilityNames.GateDecideV1 => Schema("""
            {"type":"object","required":["workstreamId","gateId","expectedRevision","decision","rationale","findings","idempotencyKey"],"properties":{"workstreamId":{"type":"string","format":"uuid"},"gateId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"decision":{"type":"string","enum":["approved","changes-required","rejected"]},"rationale":{"type":"string","minLength":1,"maxLength":4000},"findings":{"type":"array","items":{"type":"object"}},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200}},"additionalProperties":false}
            """),
        W.WorkstreamCapabilityNames.PortfolioReadV1 => Schema("""
            {"type":"object","properties":{"workstreamIds":{"type":["array","null"],"items":{"type":"string","format":"uuid"}},"includeClosed":{"type":"boolean"}},"additionalProperties":false}
            """),
        W.WorkstreamCapabilityNames.TeamRosterReadV2 => Schema("""
            {"type":"object","properties":{"teamId":{"type":["string","null"],"format":"uuid"},"workstreamId":{"type":["string","null"],"format":"uuid"},"page":{"type":"integer","minimum":1},"pageSize":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
            """),
        W.DecisionCapabilityNames.RequestV1 => Schema("""
            {"type":"object","required":["workstreamId","typeKey","summary","authorityRuleKey","options","recommendedOptionId","evidence","blockingImpact","idempotencyKey"],"properties":{"workstreamId":{"type":"string","format":"uuid"},"typeKey":{"type":"string","minLength":1,"maxLength":200},"summary":{"type":"string","minLength":1,"maxLength":4000},"authorityRuleKey":{"type":"string","minLength":1,"maxLength":200},"options":{"type":"array","minItems":2,"items":{"type":"object"}},"recommendedOptionId":{"type":"string"},"evidence":{"type":"array","items":{"type":"object"}},"dueAt":{"type":["string","null"],"format":"date-time"},"blockingImpact":{"type":"string","minLength":1,"maxLength":4000},"supersedesDecisionId":{"type":["string","null"],"format":"uuid"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200}},"additionalProperties":false}
            """),
        W.DecisionCapabilityNames.ReadV1 => Schema("""
            {"type":"object","properties":{"decisionId":{"type":["string","null"],"format":"uuid"},"workstreamId":{"type":["string","null"],"format":"uuid"},"pendingOnly":{"type":"boolean"}},"additionalProperties":false}
            """),
        W.DecisionCapabilityNames.DecideV1 => Schema("""
            {"type":"object","required":["decisionId","expectedRevision","selectedOptionId","rationale","idempotencyKey"],"properties":{"decisionId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"selectedOptionId":{"type":"string","minLength":1},"rationale":{"type":"string","minLength":1,"maxLength":4000},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200}},"additionalProperties":false}
            """),
        ArtifactPlatformCapabilities.Create => Schema("""
            {"type":"object","required":["title","content","documentType","idempotencyKey"],"properties":{"title":{"type":"string","minLength":1,"maxLength":512},"content":{"type":"string","minLength":1,"maxLength":131072},"documentType":{"type":"string","minLength":1,"maxLength":160},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200},"folderId":{"type":["string","null"],"format":"uuid"},"packageId":{"type":["string","null"],"format":"uuid"},"originConversationId":{"type":["string","null"],"format":"uuid"},"originWorkItemId":{"type":["string","null"],"format":"uuid"},"stewardOrganizationUserId":{"type":["string","null"],"format":"uuid"},"workstreamId":{"type":["string","null"],"format":"uuid"},"teamId":{"type":["string","null"],"format":"uuid"}},"additionalProperties":false}
            """),
        ArtifactPlatformCapabilities.Read => Schema("""
            {"type":"object","properties":{"artifactId":{"type":["string","null"],"format":"uuid"},"includeArchived":{"type":"boolean"}},"additionalProperties":false}
            """),
        ArtifactPlatformCapabilities.Revise => Schema("""
            {"type":"object","required":["artifactId","expectedBaseRevisionId","content","idempotencyKey"],"properties":{"artifactId":{"type":"string","format":"uuid"},"expectedBaseRevisionId":{"type":"string","format":"uuid"},"content":{"type":"string","minLength":1,"maxLength":131072},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200}},"additionalProperties":false}
            """),
        ArtifactPlatformCapabilities.Submit => Schema("""
            {"type":"object","required":["artifactId","revisionId","idempotencyKey"],"properties":{"artifactId":{"type":"string","format":"uuid"},"revisionId":{"type":"string","format":"uuid"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200},"conversationId":{"type":["string","null"],"format":"uuid"},"reviewerOrganizationUserId":{"type":["string","null"],"format":"uuid"}},"additionalProperties":false}
            """),
        ArtifactPlatformCapabilities.Decide => Schema("""
            {"type":"object","required":["artifactId","revisionId","decision","idempotencyKey"],"properties":{"artifactId":{"type":"string","format":"uuid"},"revisionId":{"type":"string","format":"uuid"},"decision":{"type":"string","enum":["accept","approve","reject","request-revision"]},"comment":{"type":["string","null"],"maxLength":4096},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200},"evidenceConversationMessageId":{"type":["string","null"],"format":"uuid"}},"additionalProperties":false}
            """),
        ArtifactPlatformCapabilities.DecideV2 => Schema("""
            {"type":"object","required":["artifactId","revisionId","revisionDigest","rubricTypeKey","disposition","findings","idempotencyKey"],"properties":{"artifactId":{"type":"string","format":"uuid"},"revisionId":{"type":"string","format":"uuid"},"revisionDigest":{"type":"string","minLength":64,"maxLength":64},"rubricTypeKey":{"type":"string","minLength":1,"maxLength":200},"disposition":{"type":"string","enum":["accepted","accepted-with-findings","changes-required","rejected"]},"findings":{"type":"array","items":{"type":"object","required":["code","section","severity","blocking","summary"],"properties":{"code":{"type":"string"},"section":{"type":"string"},"severity":{"type":"string","enum":["Information","Minor","Major","Critical"]},"blocking":{"type":"boolean"},"summary":{"type":"string"},"requiredFollowUp":{"type":["string","null"]}},"additionalProperties":false}},"comment":{"type":["string","null"],"maxLength":4096},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200},"evidenceConversationMessageId":{"type":["string","null"],"format":"uuid"}},"additionalProperties":false}
            """),
        ArtifactPlatformCapabilities.RequestAccess => Schema("""
            {"type":"object","required":["artifactId","actions","justification","idempotencyKey"],"properties":{"artifactId":{"type":"string","format":"uuid"},"actions":{"type":"array","minItems":1,"maxItems":4,"items":{"type":"string","enum":["artifact.read","artifact.revise","artifact.submit","artifact.decide"]}},"justification":{"type":"string","minLength":1,"maxLength":2048},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200},"expiresAt":{"type":["string","null"],"format":"date-time"}},"additionalProperties":false}
            """),
        ArtifactPlatformCapabilities.PackageCreate => Schema("""
            {"type":"object","required":["name","packageType","members","idempotencyKey"],"properties":{"name":{"type":"string","minLength":1,"maxLength":256},"packageType":{"type":"string","minLength":1,"maxLength":160},"members":{"type":"array","minItems":1,"maxItems":50,"items":{"type":"object","required":["artifactId","position","requiredDocumentType"],"properties":{"artifactId":{"type":"string","format":"uuid"},"position":{"type":"integer","minimum":0},"requiredDocumentType":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200}},"additionalProperties":false}
            """),
        ArtifactPlatformCapabilities.PackageRead => Schema("""
            {"type":"object","required":["packageId"],"properties":{"packageId":{"type":"string","format":"uuid"}},"additionalProperties":false}
            """),
        ArtifactPlatformCapabilities.PackageSubmit or ArtifactPlatformCapabilities.PackageDecide => Schema("""
            {"type":"object","required":["packageId","idempotencyKey"],"properties":{"packageId":{"type":"string","format":"uuid"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":200}},"additionalProperties":false}
            """),
        PlatformCapabilities.BusinessProfileRead or
        PlatformCapabilities.OrganizationSnapshotRead or
        PlatformCapabilities.FinanceProfileRead or
        PlatformCapabilities.ManagementCycleRead or
        HiringCapabilities.ListRecommendations => EmptyInput,
        CommunicationCapabilities.ChatRead => Schema("""
            {"type":"object","properties":{"chatId":{"type":["string","null"],"format":"uuid"}},"additionalProperties":false}
            """),
        CommunicationCapabilities.ChatCreate => Schema("""
            {"type":"object","required":["isDirect","isPrivate","participantOrganizationUserIds"],"properties":{"title":{"type":["string","null"],"maxLength":256},"description":{"type":["string","null"],"maxLength":2048},"isDirect":{"type":"boolean"},"isPrivate":{"type":"boolean"},"participantOrganizationUserIds":{"type":"array","maxItems":250,"items":{"type":"string","format":"uuid"}},"audienceRoleIds":{"type":["array","null"],"items":{"type":"string","format":"uuid"}},"audienceWorkstreamIds":{"type":["array","null"],"items":{"type":"string","format":"uuid"}}},"additionalProperties":false}
            """),
        CommunicationCapabilities.ChatModify => Schema("""
            {"type":"object","required":["chatId","title","isPrivate","participantOrganizationUserIds"],"properties":{"chatId":{"type":"string","format":"uuid"},"title":{"type":"string","minLength":1,"maxLength":256},"description":{"type":["string","null"],"maxLength":2048},"isPrivate":{"type":"boolean"},"participantOrganizationUserIds":{"type":"array","maxItems":250,"items":{"type":"string","format":"uuid"}},"audienceRoleIds":{"type":["array","null"],"items":{"type":"string","format":"uuid"}},"audienceWorkstreamIds":{"type":["array","null"],"items":{"type":"string","format":"uuid"}}},"additionalProperties":false}
            """),
        CommunicationCapabilities.ChatDelete => Schema("""
            {"type":"object","required":["chatId"],"properties":{"chatId":{"type":"string","format":"uuid"}},"additionalProperties":false}
            """),
        CommunicationCapabilities.MessageSend => Schema("""
            {"type":"object","required":["chatId","content"],"properties":{"chatId":{"type":"string","format":"uuid"},"content":{"type":"string","minLength":1,"maxLength":32768},"idempotencyKey":{"type":["string","null"],"maxLength":160}},"additionalProperties":false}
            """),
        CommunicationCapabilities.CoordinationStart => Schema("""
            {"type":"object","required":["targetOrganizationUserId","subject","objective","successCriteria","initialMessage","sourceConversationId","sourceChatTurnId","sourceMessageId","idempotencyKey"],"properties":{"targetOrganizationUserId":{"type":"string","format":"uuid"},"subject":{"type":"string","minLength":1,"maxLength":256},"objective":{"type":"string","minLength":1,"maxLength":4096},"successCriteria":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"string","minLength":1,"maxLength":2048}},"initialMessage":{"type":"string","minLength":1,"maxLength":32768},"sourceConversationId":{"type":"string","format":"uuid"},"sourceChatTurnId":{"type":"string","format":"uuid"},"sourceMessageId":{"type":"string","format":"uuid"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160},"artifact":{"type":["object","null"],"required":["type","schemaVersion","key","pageOrdinal","isFinalPage","payload"],"properties":{"type":{"type":"string","minLength":1,"maxLength":200},"schemaVersion":{"type":"string","minLength":1,"maxLength":50},"key":{"type":"string","minLength":1,"maxLength":500},"pageOrdinal":{"type":"integer","minimum":0},"isFinalPage":{"type":"boolean"},"payload":{}},"additionalProperties":false},"workContext":{"type":["object","null"],"required":["organizationId","workstreamId","correlationId"],"properties":{"organizationId":{"type":"string","format":"uuid"},"workstreamId":{"type":"string","format":"uuid"},"teamId":{"type":["string","null"],"format":"uuid"},"boardId":{"type":["string","null"],"format":"uuid"},"workItemId":{"type":["string","null"],"format":"uuid"},"milestoneId":{"type":["string","null"],"format":"uuid"},"gateId":{"type":["string","null"],"format":"uuid"},"correlationId":{"type":"string","format":"uuid"},"causationId":{"type":["string","null"],"format":"uuid"},"profileKey":{"type":["string","null"],"maxLength":200}},"additionalProperties":false}},"additionalProperties":false}
            """),
        CommunicationCapabilities.CoordinationStartWork => Schema("""
            {"type":"object","required":["targetOrganizationUserId","boardId","itemId","sprintExecutionId","stageExecutionId","assignmentRevision","subject","objective","successCriteria","initialMessage","idempotencyKey"],"properties":{"targetOrganizationUserId":{"type":"string","format":"uuid"},"boardId":{"type":"string","format":"uuid"},"itemId":{"type":"string","format":"uuid"},"sprintExecutionId":{"type":"string","format":"uuid"},"stageExecutionId":{"type":"string","format":"uuid"},"assignmentRevision":{"type":"integer","minimum":1},"subject":{"type":"string","minLength":1,"maxLength":256},"objective":{"type":"string","minLength":1,"maxLength":4096},"successCriteria":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"string","minLength":1,"maxLength":2048}},"initialMessage":{"type":"string","minLength":1,"maxLength":32768},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160},"artifact":{"type":["object","null"],"required":["type","schemaVersion","key","pageOrdinal","isFinalPage","payload"],"properties":{"type":{"type":"string","minLength":1,"maxLength":200},"schemaVersion":{"type":"string","minLength":1,"maxLength":50},"key":{"type":"string","minLength":1,"maxLength":500},"pageOrdinal":{"type":"integer","minimum":0},"isFinalPage":{"type":"boolean"},"payload":{}},"additionalProperties":false}},"additionalProperties":false}
            """),
        CommunicationCapabilities.CoordinationStartBoard => Schema("""
            {"type":"object","required":["targetOrganizationUserId","boardId","subject","objective","successCriteria","initialMessage","idempotencyKey"],"properties":{"targetOrganizationUserId":{"type":"string","format":"uuid"},"boardId":{"type":"string","format":"uuid"},"subject":{"type":"string","minLength":1,"maxLength":256},"objective":{"type":"string","minLength":1,"maxLength":4096},"successCriteria":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"string","minLength":1,"maxLength":2048}},"initialMessage":{"type":"string","minLength":1,"maxLength":32768},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160},"artifact":{"type":["object","null"]}},"additionalProperties":false}
            """),
        CommunicationCapabilities.CoordinationRespond => Schema("""
            {"type":"object","required":["sessionId","expectedRevision","expectedTurnOrdinal","disposition","content","idempotencyKey"],"properties":{"sessionId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"expectedTurnOrdinal":{"type":"integer","minimum":1},"disposition":{"type":"string","enum":["Continue","Completed","Blocked"]},"content":{"type":"string","minLength":1,"maxLength":32768},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160},"artifact":{"type":["object","null"],"required":["type","schemaVersion","key","pageOrdinal","isFinalPage","payload"],"properties":{"type":{"type":"string","minLength":1,"maxLength":200},"schemaVersion":{"type":"string","minLength":1,"maxLength":50},"key":{"type":"string","minLength":1,"maxLength":500},"pageOrdinal":{"type":"integer","minimum":0},"isFinalPage":{"type":"boolean"},"payload":{}},"additionalProperties":false}},"additionalProperties":false}
            """),
        CommunicationCapabilities.CoordinationRead => Schema("""
            {"type":"object","required":["sessionId"],"properties":{"sessionId":{"type":"string","format":"uuid"}},"additionalProperties":false}
            """),
        CommunicationCapabilities.CoordinationList => Schema("""
            {"type":"object","properties":{"chatId":{"type":["string","null"],"format":"uuid"},"activeOnly":{"type":"boolean"}},"additionalProperties":false}
            """),
        CommunicationCapabilities.CoordinationResume => Schema("""
            {"type":"object","required":["sessionId","expectedRevision","reason","idempotencyKey"],"properties":{"sessionId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"reason":{"type":"string","minLength":1,"maxLength":2048},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        CommunicationCapabilities.CoordinationCancel => Schema("""
            {"type":"object","required":["sessionId","expectedRevision","reason","idempotencyKey"],"properties":{"sessionId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"reason":{"type":"string","minLength":1,"maxLength":2048},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        PlatformCapabilities.BusinessPatternSearch => Schema("""
            {"type":"object","properties":{"businessType":{"type":["string","null"]},"lifecycleStage":{"type":["string","null"]},"jurisdictions":{"type":["array","null"],"items":{"type":"string"}},"maximumResults":{"type":"integer","minimum":1,"maximum":10}},"additionalProperties":false}
            """),
        PlatformCapabilities.WorkforceSearch => Schema("""
            {"type":"object","required":["requiredCapabilities","humanRequired"],"properties":{"requiredCapabilities":{"type":"array","items":{"type":"string"},"minItems":1},"requiredCredentials":{"type":["array","null"],"items":{"type":"string"}},"neededBy":{"type":["string","null"],"format":"date-time"},"maximumBudget":{"type":["number","null"],"minimum":0},"currency":{"type":["string","null"]},"humanRequired":{"type":"boolean"},"workstreamId":{"type":["string","null"]},"maximumResults":{"type":"integer","minimum":1,"maximum":25}},"additionalProperties":false}
            """),
        AgentCatalogCapabilities.Search => Schema("""
            {"type":"object","properties":{"role":{"type":["string","null"],"maxLength":160},"searchString":{"type":["string","null"],"maxLength":500},"requiredCapabilities":{"type":["array","null"],"items":{"type":"string"}},"category":{"type":["string","null"],"maxLength":160},"maximumPrice":{"type":["number","null"],"minimum":0},"currency":{"type":["string","null"],"maxLength":8},"sort":{"type":["string","null"],"enum":["relevance","rating","price-low","name",null]},"limit":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
            """),
        PlatformCapabilities.BusinessProfileUpdateExplicit => Schema("""
            {"type":"object","required":["expectedRevision","conversationId","messageId","userId","changes","idempotencyKey"],"properties":{"expectedRevision":{"type":"integer"},"conversationId":{"type":"string"},"messageId":{"type":"string"},"userId":{"type":"string"},"changes":{"type":"object"},"idempotencyKey":{"type":"string"}},"additionalProperties":false}
            """),
        PlatformCapabilities.BudgetEvaluate => Schema("""
            {"type":"object","required":["scopeType","amount","currency","purpose","reserve","idempotencyKey"],"properties":{"scopeType":{"type":"string"},"scopeId":{"type":["string","null"]},"amount":{"type":"number","minimum":0},"currency":{"type":"string"},"purpose":{"type":"string"},"reserve":{"type":"boolean"},"idempotencyKey":{"type":"string"}},"additionalProperties":false}
            """),
        CommunicationHubCapabilities.AskUser => Schema("""
            {"type":"object","required":["conversationId","prompt","options","recommendedOptionId","idempotencyKey"],"properties":{"conversationId":{"type":"string","format":"uuid"},"chatTurnId":{"type":["string","null"],"format":"uuid"},"conversationMessageId":{"type":["string","null"],"format":"uuid"},"prompt":{"type":"string","minLength":1,"maxLength":2048},"options":{"type":"array","minItems":2,"maxItems":4,"items":{"type":"object","required":["id","label"],"properties":{"id":{"type":"string","minLength":1,"maxLength":80},"label":{"type":"string","minLength":1,"maxLength":160},"description":{"type":["string","null"],"maxLength":500}},"additionalProperties":false}},"recommendedOptionId":{"type":"string","minLength":1,"maxLength":80},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        SuggestedUserActionCapabilities.Suggest => Schema("""
            {"type":"object","required":["workflowType","label","parameters","idempotencyKey"],"properties":{"messageId":{"type":["string","null"],"format":"uuid"},"chatTurnId":{"type":["string","null"],"format":"uuid"},"workflowType":{"type":"string","enum":["hiring.marketplace.browse.v1"]},"label":{"type":"string","minLength":1,"maxLength":120},"description":{"type":["string","null"],"maxLength":500},"parameters":{"type":"object","required":["role"],"properties":{"role":{"type":"string","minLength":1,"maxLength":160},"recommendationId":{"type":["string","null"],"format":"uuid"}},"additionalProperties":false},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        HiringCapabilities.UpsertRecommendation => Schema("""
            {"type":"object","required":["title","objective","priority","idempotencyKey"],"properties":{"title":{"type":"string","minLength":1,"maxLength":256},"objective":{"type":"string","minLength":1,"maxLength":2048},"priority":{"type":"integer","minimum":1,"maximum":100,"description":"1 is the highest priority"},"roleKey":{"type":["string","null"],"maxLength":160},"headcount":{"type":"integer","minimum":1,"maximum":100},"sourceResourceChangeRequestId":{"type":["string","null"],"format":"uuid"},"teamId":{"type":["string","null"],"format":"uuid"},"workstreamId":{"type":["string","null"],"format":"uuid"},"candidateReferences":{"type":["array","null"],"maxItems":3,"items":{"type":"string"}},"recommendedCandidateReference":{"type":["string","null"]},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        HiringCapabilities.ResolveRecommendation => Schema("""
            {"type":"object","required":["recommendationId","resultOrganizationUserId","idempotencyKey"],"properties":{"recommendationId":{"type":"string","format":"uuid"},"resultOrganizationUserId":{"type":"string","format":"uuid"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        HiringCapabilities.WithdrawRecommendation => Schema("""
            {"type":"object","required":["recommendationId","reason","idempotencyKey"],"properties":{"recommendationId":{"type":"string","format":"uuid"},"reason":{"type":"string","minLength":1,"maxLength":2048},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        ResourceChangeCapabilities.Propose => Schema("""
            {"type":"object","required":["conversationId","chatTurnId","productGoal","rationale","contextRevision","roles","assumptions","constraints","idempotencyKey"],"properties":{"conversationId":{"type":"string","format":"uuid"},"chatTurnId":{"type":"string","format":"uuid"},"productGoal":{"type":"string","minLength":1,"maxLength":2048},"rationale":{"type":"string","minLength":1,"maxLength":4096},"contextRevision":{"type":"integer"},"teamKey":{"type":["string","null"],"maxLength":200},"teamName":{"type":["string","null"],"maxLength":160},"teamDescription":{"type":["string","null"],"maxLength":2048},"roles":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"object","required":["roleKey","roleCategoryKey","team","title","purpose","headcount","priority","timing","requiredCapabilities","humanRequired"],"properties":{"roleKey":{"type":"string","minLength":1,"maxLength":160},"roleCategoryKey":{"type":"string","pattern":"^[a-z0-9]+(?:-[a-z0-9]+)*$","maxLength":160},"preferredSpecializationKeys":{"type":"array","maxItems":20,"uniqueItems":true,"items":{"type":"string","pattern":"^[a-z0-9]+(?:-[a-z0-9]+)*$","maxLength":160}},"team":{"type":"string","minLength":1,"maxLength":160},"title":{"type":"string","minLength":1,"maxLength":256},"purpose":{"type":"string","minLength":1,"maxLength":2048},"headcount":{"type":"integer","minimum":1,"maximum":100},"priority":{"type":"integer","minimum":1,"maximum":100},"timing":{"type":"string","minLength":1,"maxLength":32},"requiredCapabilities":{"type":"array","minItems":1,"maxItems":25,"items":{"type":"string"}},"humanRequired":{"type":"boolean"},"reportsToOrganizationUserId":{"type":["string","null"],"format":"uuid"},"reportsToRoleKey":{"type":["string","null"],"maxLength":160},"teamId":{"type":["string","null"],"format":"uuid"}},"additionalProperties":false}},"assumptions":{"type":"array","maxItems":20,"items":{"type":"string"}},"constraints":{"type":"array","maxItems":20,"items":{"type":"string"}},"supersedesRequestId":{"type":["string","null"],"format":"uuid"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        ResourceChangeCapabilities.Read => Schema("""
            {"type":"object","properties":{"requestId":{"type":["string","null"],"format":"uuid"},"statuses":{"type":["array","null"],"items":{"type":"string"}}},"additionalProperties":false}
            """),
        ResourceChangeCapabilities.Decide => Schema("""
            {"type":"object","required":["requestId","decision","idempotencyKey"],"properties":{"requestId":{"type":"string","format":"uuid"},"decision":{"type":"string","enum":["Approve","RequestRevision","Reject"]},"comment":{"type":["string","null"],"maxLength":4000},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        StaffingReplenishmentCapabilities.Propose => Schema("""
            {"type":"object","required":["sourceResourceChangeRequestId","teamId","conversationId","gaps","operationalImpact","interimControls","decisionFingerprint","idempotencyKey"],"properties":{"sourceResourceChangeRequestId":{"type":"string","format":"uuid"},"teamId":{"type":"string","format":"uuid"},"conversationId":{"type":"string","format":"uuid"},"gaps":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"object","required":["roleKey","roleTitle","desiredHeadcount","effectiveHeadcount","missingHeadcount","eligibilityEvidence"],"properties":{"roleKey":{"type":"string","minLength":1,"maxLength":160},"roleTitle":{"type":"string","minLength":1,"maxLength":256},"desiredHeadcount":{"type":"integer","minimum":1},"effectiveHeadcount":{"type":"integer","minimum":0},"missingHeadcount":{"type":"integer","minimum":1},"eligibilityEvidence":{"type":"array","maxItems":20,"items":{"type":"string","minLength":1,"maxLength":1024}}},"additionalProperties":false}},"operationalImpact":{"type":"string","minLength":1,"maxLength":4096},"interimControls":{"type":"array","maxItems":20,"items":{"type":"string","minLength":1,"maxLength":1024}},"decisionFingerprint":{"type":"string","minLength":1,"maxLength":128},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        StaffingReplenishmentCapabilities.Read => Schema("""
            {"type":"object","properties":{"requestId":{"type":["string","null"],"format":"uuid"},"sourceResourceChangeRequestId":{"type":["string","null"],"format":"uuid"},"statuses":{"type":["array","null"],"items":{"type":"string"}}},"additionalProperties":false}
            """),
        StaffingReplenishmentCapabilities.Decide => Schema("""
            {"type":"object","required":["requestId","decision","idempotencyKey"],"properties":{"requestId":{"type":"string","format":"uuid"},"decision":{"type":"string","enum":["Approve","RequestRevision","Reject"]},"comment":{"type":["string","null"],"maxLength":4000},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        PlatformCapabilities.TeamRosterRead => Schema("""
            {"type":"object","properties":{"page":{"type":"integer","minimum":1,"maximum":10000},"pageSize":{"type":"integer","minimum":1,"maximum":100}},"additionalProperties":false}
            """),
        PlatformCapabilities.AgentOperatingStateRead => Schema("""
            {"type":"object","required":["stateKey"],"properties":{"stateKey":{"type":"string","minLength":1,"maxLength":160,"pattern":"^[A-Za-z0-9._/:\\-]+$"}},"additionalProperties":false}
            """),
        PlatformCapabilities.AgentOperatingStateWrite => Schema("""
            {"type":"object","required":["stateKey","schemaId","schemaVersion","status","sourceRevisions","conditionCodes","decisionFingerprint","openCommitmentCorrelations","attentionReviewId","payload","idempotencyKey"],"properties":{"stateKey":{"type":"string","minLength":1,"maxLength":160},"schemaId":{"type":"string","minLength":1,"maxLength":160},"schemaVersion":{"type":"integer","minimum":1},"status":{"type":"string","minLength":1,"maxLength":80},"sourceRevisions":{"type":"object","maxProperties":32,"additionalProperties":true},"conditionCodes":{"type":"array","maxItems":32,"items":{"type":"string","minLength":1,"maxLength":80}},"decisionFingerprint":{"type":"string","minLength":1,"maxLength":128},"openCommitmentCorrelations":{"type":"array","maxItems":32,"items":{"type":"string","minLength":1,"maxLength":200}},"attentionReviewId":{"type":"string","format":"uuid"},"payload":{"type":"object"},"expectedRevision":{"type":["integer","null"],"minimum":0},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        HiringCapabilities.StageWorkflow => Schema("""
            {"type":"object","required":["recommendationId","candidateReference","roleTitle","conversationId","chatTurnId","idempotencyKey"],"properties":{"recommendationId":{"type":"string","format":"uuid"},"candidateReference":{"type":"string"},"roleTitle":{"type":"string","minLength":1,"maxLength":160},"reportsToOrganizationUserId":{"type":["string","null"],"format":"uuid"},"requiredGrants":{"type":["array","null"],"items":{"type":"string"}},"conversationId":{"type":"string","format":"uuid"},"chatTurnId":{"type":"string","format":"uuid"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        WorkBoardActions.Read => Schema("""
            {"type":"object","properties":{"search":{"type":["string","null"],"maxLength":160},"includeArchived":{"type":"boolean"}},"additionalProperties":false}
            """),
        WorkItemActions.Read => Schema("""
            {"type":"object","required":["boardId"],"properties":{"boardId":{"type":"string","format":"uuid"},"itemId":{"type":["string","null"],"format":"uuid"}},"additionalProperties":false}
            """),
        WorkItemActions.ReadTypes => Schema("""
            {"type":"object","properties":{"boardProfileKey":{"type":["string","null"],"maxLength":200},"boardId":{"type":["string","null"],"format":"uuid"}},"additionalProperties":false}
            """),
        WorkBoardActions.Create => Schema("""
            {"type":"object","required":["name","profileKey","idempotencyKey"],"properties":{"name":{"type":"string","minLength":1,"maxLength":160},"description":{"type":["string","null"],"maxLength":2048},"teamId":{"type":["string","null"],"format":"uuid"},"workstreamId":{"type":["string","null"],"format":"uuid"},"key":{"type":["string","null"],"minLength":2,"maxLength":12},"profileKey":{"type":"string","minLength":1,"maxLength":200},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        WorkBoardActions.Configure => Schema("""
            {"type":"object","required":["boardId","expectedRevision","name","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"name":{"type":"string","minLength":1,"maxLength":160},"description":{"type":["string","null"],"maxLength":2048},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        WorkBoardActions.ConfigureColumns => Schema("""
            {"type":"object","required":["boardId","expectedRevision","columns","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"columns":{"type":"array","minItems":1,"maxItems":50,"items":{"type":"object","required":["name","category","wipPolicy"],"properties":{"id":{"type":["string","null"],"format":"uuid"},"name":{"type":"string","minLength":1,"maxLength":160},"category":{"type":"string","enum":["ToDo","InProgress","Done","Cancelled"]},"wipPolicy":{"type":"string","enum":["Disabled","Warning","HardLimit"]},"wipLimit":{"type":["integer","null"],"minimum":1}},"additionalProperties":false}},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        WorkItemActions.Create => Schema("""
            {"type":"object","required":["boardId","title","typeKey","priority","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"title":{"type":"string","minLength":1,"maxLength":512},"typeKey":{"type":"string","minLength":1,"maxLength":200},"priority":{"type":"string","enum":["Low","Medium","High","Critical"]},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":true}
            """),
        WorkItemActions.RevisePlanning => Schema("""
            {"type":"object","required":["boardId","itemId","title","planning","expectedRevision","expectedPlanningRevision","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"itemId":{"type":"string","format":"uuid"},"title":{"type":"string","minLength":1,"maxLength":512},"description":{"type":["string","null"],"maxLength":8192},"parentItemId":{"type":["string","null"],"format":"uuid"},"planning":{"type":"object"},"expectedRevision":{"type":"integer","minimum":1},"expectedPlanningRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160},"proposalProvenance":{"type":["object","null"]}},"additionalProperties":false}
            """),
        WorkItemActions.DecideApproval => Schema("""
            {"type":"object","required":["boardId","itemId","policyKey","decision","expectedPlanningRevision","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"itemId":{"type":"string","format":"uuid"},"policyKey":{"type":"string","enum":["software.architecture-review.v1"]},"decision":{"type":"string","enum":["Approved","ChangesRequested","Waived"]},"expectedPlanningRevision":{"type":"integer","minimum":1},"rationale":{"type":["string","null"],"maxLength":4096},"managerWaiverSource":{"type":["string","null"],"maxLength":300},"coordinationSessionId":{"type":["string","null"],"format":"uuid"},"artifactDigest":{"type":["string","null"],"maxLength":128},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        WorkItemActions.Comment => Schema("""
            {"type":"object","required":["boardId","itemId","body","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"itemId":{"type":"string","format":"uuid"},"body":{"type":"string","minLength":1,"maxLength":8192},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160},"kind":{"type":["string","null"],"maxLength":80},"coordinationSessionId":{"type":["string","null"],"format":"uuid"},"causationId":{"type":["string","null"],"maxLength":160},"artifactDigest":{"type":["string","null"],"maxLength":128}},"additionalProperties":false}
            """),
        WorkItemActions.ReadComments => Schema("""
            {"type":"object","required":["boardId","itemId"],"properties":{"boardId":{"type":"string","format":"uuid"},"itemId":{"type":"string","format":"uuid"},"kind":{"type":["string","null"],"maxLength":80},"page":{"type":"integer","minimum":1},"pageSize":{"type":"integer","minimum":1,"maximum":200}},"additionalProperties":false}
            """),
        WorkItemActions.Estimate => Schema("""
            {"type":"object","required":["boardId","itemId","expectedItemRevision","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"itemId":{"type":"string","format":"uuid"},"estimatePoints":{"type":["number","null"],"minimum":0,"maximum":999999.99},"expectedItemRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        WorkItemActions.Move => Schema("""
            {"type":"object","required":["boardId","itemId","targetColumnId","expectedRevision","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"itemId":{"type":"string","format":"uuid"},"targetColumnId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        WorkItemActions.Start or WorkItemActions.Complete or WorkItemActions.Cancel or WorkItemActions.Reopen => Schema("""
            {"type":"object","required":["boardId","itemId","expectedRevision","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"itemId":{"type":"string","format":"uuid"},"targetColumnId":{"type":["string","null"],"format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        WorkItemActions.Transfer => Schema("""
            {"type":"object","required":["boardId","itemId","targetBoardId","expectedRevision","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"itemId":{"type":"string","format":"uuid"},"targetBoardId":{"type":"string","format":"uuid"},"targetColumnId":{"type":["string","null"],"format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        PersonalTodoActions.Read => EmptyInput,
        PersonalTodoActions.Add => Schema("""
            {"type":"object","required":["title","priority","idempotencyKey"],"properties":{"title":{"type":"string","minLength":1,"maxLength":512},"description":{"type":["string","null"],"maxLength":8192},"priority":{"type":"string","enum":["Low","Medium","High","Critical"]},"dueDate":{"type":["string","null"],"format":"date-time"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160},"targetOrganizationUserId":{"type":["string","null"],"format":"uuid"},"sourceConversationId":{"type":["string","null"],"format":"uuid"},"sourceMessageId":{"type":["string","null"],"format":"uuid"},"correlationId":{"type":["string","null"],"maxLength":160},"causationId":{"type":["string","null"],"maxLength":160},"mentions":{"type":["array","null"],"maxItems":100,"items":{"type":"object","required":["organizationUserId","field","offset","length"],"properties":{"organizationUserId":{"type":"string","format":"uuid"},"field":{"type":"string","enum":["Title","Description"]},"offset":{"type":"integer","minimum":0},"length":{"type":"integer","minimum":1}},"additionalProperties":false}},"startInBacklog":{"type":"boolean"},"workContext":{"type":["object","null"],"properties":{"workstreamId":{"type":["string","null"],"format":"uuid"},"teamId":{"type":["string","null"],"format":"uuid"},"boardId":{"type":["string","null"],"format":"uuid"},"workItemId":{"type":["string","null"],"format":"uuid"},"sprintId":{"type":["string","null"],"format":"uuid"},"gateId":{"type":["string","null"],"format":"uuid"},"decisionId":{"type":["string","null"],"format":"uuid"},"coordinationSessionId":{"type":["string","null"],"format":"uuid"},"sourceFingerprint":{"type":["string","null"],"maxLength":128}},"additionalProperties":false}},"additionalProperties":false}
            """),
        PersonalTodoActions.Reorder => Schema("""
            {"type":"object","required":["itemId","expectedRevision","idempotencyKey"],"properties":{"itemId":{"type":"string","format":"uuid"},"beforeItemId":{"type":["string","null"],"format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        PersonalTodoActions.Requeue => Schema("""
            {"type":"object","required":["itemId","expectedRevision","idempotencyKey"],"properties":{"itemId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        PersonalTodoActions.Activate => Schema("""
            {"type":"object","required":["itemId","expectedRevision","idempotencyKey"],"properties":{"itemId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        PersonalTodoActions.Update => Schema("""
            {"type":"object","required":["itemId","title","priority","expectedRevision","idempotencyKey"],"properties":{"itemId":{"type":"string","format":"uuid"},"title":{"type":"string","minLength":1,"maxLength":512},"description":{"type":["string","null"],"maxLength":8192},"priority":{"type":"string","enum":["Low","Medium","High","Critical"]},"dueDate":{"type":["string","null"],"format":"date-time"},"expectedRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160},"mentions":{"type":["array","null"],"maxItems":100,"items":{"type":"object","required":["organizationUserId","field","offset","length"],"properties":{"organizationUserId":{"type":"string","format":"uuid"},"field":{"type":"string","enum":["Title","Description"]},"offset":{"type":"integer","minimum":0},"length":{"type":"integer","minimum":1}},"additionalProperties":false}},"workContext":{"type":["object","null"],"properties":{"workstreamId":{"type":["string","null"],"format":"uuid"},"teamId":{"type":["string","null"],"format":"uuid"},"boardId":{"type":["string","null"],"format":"uuid"},"workItemId":{"type":["string","null"],"format":"uuid"},"sprintId":{"type":["string","null"],"format":"uuid"},"gateId":{"type":["string","null"],"format":"uuid"},"decisionId":{"type":["string","null"],"format":"uuid"},"coordinationSessionId":{"type":["string","null"],"format":"uuid"},"sourceFingerprint":{"type":["string","null"],"maxLength":128}},"additionalProperties":false}},"additionalProperties":false}
            """),
        PersonalTodoActions.Archive or PersonalTodoActions.Restore => Schema("""
            {"type":"object","required":["itemId","expectedRevision","idempotencyKey"],"properties":{"itemId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        PersonalTodoActions.Claim => Schema("""
            {"type":"object","required":["eventId","idempotencyKey"],"properties":{"eventId":{"type":"string","format":"uuid"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        PersonalTodoActions.Complete => Schema("""
            {"type":"object","required":["itemId","eventId","expectedRevision","summary","idempotencyKey"],"properties":{"itemId":{"type":"string","format":"uuid"},"eventId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"summary":{"type":"string","minLength":1,"maxLength":4096},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        PersonalTodoActions.Block => Schema("""
            {"type":"object","required":["itemId","eventId","expectedRevision","reason","idempotencyKey"],"properties":{"itemId":{"type":"string","format":"uuid"},"eventId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"reason":{"type":"string","minLength":1,"maxLength":4096},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        PersonalTodoActions.Release => Schema("""
            {"type":"object","required":["itemId","eventId","expectedRevision","idempotencyKey"],"properties":{"itemId":{"type":"string","format":"uuid"},"eventId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"keepInProgress":{"type":"boolean"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        PersonalTodoActions.Defer => Schema("""
            {"type":"object","required":["itemId","eventId","expectedRevision","nextReviewAt","reason","idempotencyKey"],"properties":{"itemId":{"type":"string","format":"uuid"},"eventId":{"type":"string","format":"uuid"},"expectedRevision":{"type":"integer","minimum":1},"nextReviewAt":{"type":"string","format":"date-time"},"reason":{"type":"string","minLength":1,"maxLength":2048},"waitingOnOrganizationUserId":{"type":["string","null"],"format":"uuid"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        GitWorkspaceCapabilities.Prepare => Schema("""
            {"type":"object","required":["workItemId","assignmentRevision","idempotencyKey"],"properties":{"workItemId":{"type":"string","format":"uuid"},"assignmentRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        GitWorkspaceCapabilities.Refresh => Schema("""
            {"type":"object","required":["workspaceId","assignmentRevision","idempotencyKey"],"properties":{"workspaceId":{"type":"string","format":"uuid"},"assignmentRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        GitWorkspaceCapabilities.Inspect => Schema("""
            {"type":"object","required":["workspaceId","assignmentRevision"],"properties":{"workspaceId":{"type":"string","format":"uuid"},"assignmentRevision":{"type":"integer","minimum":1}},"additionalProperties":false}
            """),
        GitWorkspaceCapabilities.Publish => Schema("""
            {"type":"object","required":["workspaceId","assignmentRevision","commitMessage","proposedChangeTitle","proposedChangeBody","idempotencyKey"],"properties":{"workspaceId":{"type":"string","format":"uuid"},"assignmentRevision":{"type":"integer","minimum":1},"commitMessage":{"type":"string","minLength":1,"maxLength":512},"proposedChangeTitle":{"type":"string","minLength":1,"maxLength":256},"proposedChangeBody":{"type":"string","maxLength":32768},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        GitWorkspaceCapabilities.Cleanup => Schema("""
            {"type":"object","required":["workspaceId","assignmentRevision"],"properties":{"workspaceId":{"type":"string","format":"uuid"},"assignmentRevision":{"type":"integer","minimum":1},"retainOnFailure":{"type":"boolean"}},"additionalProperties":false}
            """),
        SourceControlCapabilities.TeamRepositoryOptions => Schema("""
            {"type":"object","required":["teamId"],"properties":{"teamId":{"type":"string","format":"uuid"}},"additionalProperties":false}
            """),
        SourceControlCapabilities.ProvisionRepository => Schema("""
            {"type":"object","required":["productOrWorkstreamId","projectDisplayName","templateId","idempotencyKey"],"properties":{"productOrWorkstreamId":{"type":"string","format":"uuid"},"projectDisplayName":{"type":"string","minLength":1,"maxLength":160},"description":{"type":["string","null"],"maxLength":2048},"templateId":{"type":"string","format":"uuid"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        GitMergeCapabilities.Review => Schema("""
            {"type":"object","required":["workItemId","assignmentRevision","idempotencyKey"],"properties":{"workItemId":{"type":"string","format":"uuid"},"assignmentRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        GitMergeCapabilities.Authorize => Schema("""
            {"type":"object","required":["workItemId","assignmentRevision","publicationId","candidateCommitSha","decision","idempotencyKey"],"properties":{"workItemId":{"type":"string","format":"uuid"},"assignmentRevision":{"type":"integer","minimum":1},"publicationId":{"type":"string","format":"uuid"},"candidateCommitSha":{"type":"string","minLength":40,"maxLength":64},"decision":{"type":"string","enum":["Approve","Reject"]},"feedback":{"type":["string","null"],"maxLength":4000},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        WorkSprintActions.Read => Schema("""
            {"type":"object","required":["boardId"],"properties":{"boardId":{"type":"string","format":"uuid"}},"additionalProperties":false}
            """),
        WorkSprintActions.Create => Schema("""
            {"type":"object","required":["boardId","name","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"name":{"type":"string","minLength":1,"maxLength":160},"goal":{"type":["string","null"],"maxLength":2048},"startsAt":{"type":["string","null"],"format":"date-time"},"endsAt":{"type":["string","null"],"format":"date-time"},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        WorkSprintActions.ManageScope => Schema("""
            {"type":"object","required":["boardId","itemId","expectedItemRevision","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"itemId":{"type":"string","format":"uuid"},"sprintId":{"type":["string","null"],"format":"uuid"},"expectedItemRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        WorkSprintActions.ManageCapacity => Schema("""
            {"type":"object","required":["boardId","sprintId","expectedSprintRevision","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"sprintId":{"type":"string","format":"uuid"},"capacityPoints":{"type":["number","null"],"minimum":0,"maximum":999999.99},"expectedSprintRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        WorkSprintActions.CarryOver => Schema("""
            {"type":"object","required":["boardId","sourceSprintId","targetSprintId","expectedSourceSprintRevision","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"sourceSprintId":{"type":"string","format":"uuid"},"targetSprintId":{"type":"string","format":"uuid"},"itemIds":{"type":["array","null"],"maxItems":500,"items":{"type":"string","format":"uuid"}},"expectedSourceSprintRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        WorkSprintActions.ReadReports => Schema("""
            {"type":"object","required":["boardId"],"properties":{"boardId":{"type":"string","format":"uuid"}},"additionalProperties":false}
            """),
        WorkOrchestrationActions.Preflight or WorkOrchestrationActions.Start => Schema("""
            {"type":"object","required":["boardId","sprintId","expectedSprintRevision","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"sprintId":{"type":"string","format":"uuid"},"expectedSprintRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        WorkOrchestrationActions.Read => Schema("""
            {"type":"object","required":["boardId"],"properties":{"boardId":{"type":"string","format":"uuid"},"sprintId":{"type":["string","null"],"format":"uuid"},"sprintExecutionId":{"type":["string","null"],"format":"uuid"}},"additionalProperties":false}
            """),
        WorkOrchestrationActions.Pause or WorkOrchestrationActions.Resume or WorkOrchestrationActions.Cancel => Schema("""
            {"type":"object","required":["boardId","sprintId","expectedSprintRevision","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"sprintId":{"type":"string","format":"uuid"},"expectedSprintRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160},"reason":{"type":["string","null"],"maxLength":2048}},"additionalProperties":false}
            """),
        WorkOrchestrationActions.Retry => Schema("""
            {"type":"object","required":["boardId","sprintExecutionId","stageExecutionId","expectedAssignmentRevision","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"sprintExecutionId":{"type":"string","format":"uuid"},"stageExecutionId":{"type":"string","format":"uuid"},"expectedAssignmentRevision":{"type":"integer","minimum":1},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160},"reason":{"type":["string","null"],"maxLength":2048}},"additionalProperties":false}
            """),
        WorkOrchestrationActions.ConfigureSoftwareTemplate => Schema("""
            {"type":"object","required":["boardId","readyColumnId","developmentColumnId","devCompleteColumnId","qualityColumnId","readyToMergeColumnId","doneColumnId","mergeMode","maximumQualityCycles","idempotencyKey"],"properties":{"boardId":{"type":"string","format":"uuid"},"readyColumnId":{"type":"string","format":"uuid"},"developmentColumnId":{"type":"string","format":"uuid"},"devCompleteColumnId":{"type":"string","format":"uuid"},"qualityColumnId":{"type":"string","format":"uuid"},"readyToMergeColumnId":{"type":"string","format":"uuid"},"doneColumnId":{"type":"string","format":"uuid"},"mergeMode":{"type":"string","enum":["ManagerApproval","Automatic"]},"maximumQualityCycles":{"type":"integer","minimum":1,"maximum":10},"idempotencyKey":{"type":"string","minLength":1,"maxLength":160}},"additionalProperties":false}
            """),
        _ => Schema("""
            {"type":"object","description":"Arguments are validated by the broker capability handler."}
            """)
        };
    }

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void RequireObjectSchema(string capability, string direction, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            type.GetString() != "object")
            throw new InvalidOperationException(
                $"Capability '{capability}' has an invalid {direction} schema. Registry schemas must have an object root.");
    }

    private static void RequireOutputSchema(string capability, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            type.GetString() is not ("object" or "array"))
            throw new InvalidOperationException(
                $"Capability '{capability}' has an invalid output schema. Registry outputs must have an object or array root.");
    }

    private static string ToToolName(string capability)
    {
        var withoutVersion = capability.EndsWith(".v1", StringComparison.Ordinal) ||
                             capability.EndsWith(".v2", StringComparison.Ordinal)
            ? capability[..^3]
            : capability;
        return string.Concat(withoutVersion.Select(x => char.IsLetterOrDigit(x) ? char.ToLowerInvariant(x) : '_'))
            .Trim('_');
    }
}
