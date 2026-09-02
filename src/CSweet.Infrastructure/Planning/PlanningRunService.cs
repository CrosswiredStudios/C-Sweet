using CSweet.AI.AgentFramework;
using CSweet.AI.Providers;
using CSweet.Application.Llm;
using CSweet.Application.Planning;
using CSweet.Contracts.Llm;
using CSweet.Contracts.Planning;
using CSweet.Domain.Planning;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Planning;

public sealed class PlanningRunService : IPlanningRunService
{
    private readonly CSweetDbContext _dbContext;
    private readonly IAgentRunner _agentRunner;
    private readonly IPlanningWorkflowService _workflowService;

    public PlanningRunService(
        CSweetDbContext dbContext,
        IAgentRunner agentRunner,
        IPlanningWorkflowService workflowService)
    {
        _dbContext = dbContext;
        _agentRunner = agentRunner;
        _workflowService = workflowService;
    }

    public async Task<PlanningActionResponse> StartAsync(StartPlanningRunRequest request, CancellationToken cancellationToken = default)
    {
        var org = await _dbContext.CoreOrganizations
            .SingleOrDefaultAsync(x => x.Id == request.OrganizationId, cancellationToken);

        if (org is null)
            return Failure("organization_not_found", "Organization was not found.");

        var workflowEntity = await _dbContext.Set<PlanningWorkflow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(w => w.Key == request.WorkflowKey, cancellationToken);

        if (workflowEntity is null)
            return Failure("workflow_not_found", $"Workflow '{request.WorkflowKey}' was not found.");

        if (!workflowEntity.IsEnabled)
            return Failure("workflow_disabled", "This workflow is currently disabled.");

        // Check if a run already exists for this org + workflow
        var existingTasks = await _dbContext.Set<PlanningTask>()
            .Where(t => t.OrganizationId == request.OrganizationId && t.TaskKey.StartsWith(request.WorkflowKey + ":", StringComparison.Ordinal))
            .ToListAsync(cancellationToken);

        if (existingTasks.Any())
        {
            var hasPendingOrRunning = existingTasks.Any(t =>
                t.Status == PlanningTaskStatus.Pending || t.Status == PlanningTaskStatus.Queued || t.Status == PlanningTaskStatus.Running);

            if (hasPendingOrRunning)
                return Failure("run_in_progress", "A planning run is already in progress for this organization and workflow.");
        }

        // Parse task definitions from workflow
        var taskDefinitions = ParseTaskDefinitions(workflowEntity.TaskDefinitionJson);

        var now = DateTimeOffset.UtcNow;
        var tasks = new List<PlanningTask>();

        foreach (var (index, def) in taskDefinitions.Select((t, i) => (i, t)))
        {
            tasks.Add(new PlanningTask
            {
                Id = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                TaskKey = $"{request.WorkflowKey}:{def.Key}",
                DisplayName = def.DisplayName,
                Status = PlanningTaskStatus.Pending,
                ProviderProfileId = request.ProviderProfileId,
                AgentKey = def.AgentKey,
                SystemPrompt = def.SystemPrompt,
                UserPrompt = def.UserPrompt,
                SortOrder = index,
                IsRequired = def.IsRequired,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        _dbContext.Set<PlanningTask>().AddRange(tasks);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var runResponse = new PlanningRunResponse(
            request.OrganizationId,
            request.WorkflowKey,
            tasks.Select(t => t.ToResponse()).ToList(),
            now);

        return new PlanningActionResponse(true, null, null, runResponse);
    }

    public async Task<PlanningStatusResponse?> GetStatusAsync(Guid organizationId, string workflowKey, CancellationToken cancellationToken = default)
    {
        var tasks = await _dbContext.Set<PlanningTask>()
            .Where(t => t.OrganizationId == organizationId && t.TaskKey.StartsWith(workflowKey + ":", StringComparison.Ordinal))
            .OrderBy(t => t.SortOrder)
            .ToListAsync(cancellationToken);

        if (!tasks.Any())
            return null;

        var completed = tasks.Count(t => t.Status == PlanningTaskStatus.Completed);
        var failed = tasks.Count(t => t.Status == PlanningTaskStatus.Failed);
        var pending = tasks.Count(t => t.Status is PlanningTaskStatus.Pending or PlanningTaskStatus.Queued);
        var running = tasks.Count(t => t.Status == PlanningTaskStatus.Running);

        return new PlanningStatusResponse(
            organizationId,
            workflowKey,
            tasks.Count,
            completed,
            failed,
            pending,
            running,
            pending == 0 && running == 0,
            failed > 0,
            tasks.Select(t => t.ToResponse()).ToList());
    }

    public async Task<PlanningActionResponse> RunNextTaskAsync(Guid organizationId, string workflowKey, CancellationToken cancellationToken = default)
    {
        var org = await _dbContext.CoreOrganizations
            .SingleOrDefaultAsync(x => x.Id == organizationId, cancellationToken);

        if (org is null)
            return Failure("organization_not_found", "Organization was not found.");

        var nextTask = await _dbContext.Set<PlanningTask>()
            .Where(t => t.OrganizationId == organizationId
                && t.TaskKey.StartsWith(workflowKey + ":", StringComparison.Ordinal)
                && t.Status == PlanningTaskStatus.Pending)
            .OrderBy(t => t.SortOrder)
            .FirstOrDefaultAsync(cancellationToken);

        if (nextTask is null)
            return Failure("no_pending_tasks", "No pending tasks found. The run may be complete.");

        if (string.IsNullOrWhiteSpace(nextTask.AgentKey) || string.IsNullOrWhiteSpace(nextTask.SystemPrompt))
            return Failure("task_incomplete", "Task definition is incomplete.");

        // Build context from organization
        var context = BuildOrganizationContext(org);

        // Build user prompt with context from previously completed tasks
        var userPrompt = await BuildUserPrompt(nextTask, org, workflowKey, cancellationToken);

        // Update task status to running
        nextTask.Status = PlanningTaskStatus.Running;
        nextTask.StartedAt = DateTimeOffset.UtcNow;
        nextTask.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var request = new AgentRunRequest(
                ProviderProfileId: nextTask.ProviderProfileId!.Value,
                AgentKey: nextTask.AgentKey,
                SystemPrompt: nextTask.SystemPrompt,
                UserPrompt: userPrompt,
                Context: context,
                Options: new AgentRunOptions(
                    Temperature: 0.7,
                    MaxOutputTokens: 8192,
                    RequireStructuredOutput: false,
                    OutputSchemaJson: null));

            var result = await _agentRunner.RunAsync(request, cancellationToken);

            nextTask.Status = result.Succeeded ? PlanningTaskStatus.Completed : PlanningTaskStatus.Failed;
            nextTask.OutputContent = result.Content;
            nextTask.OutputStructuredJson = result.StructuredJson;
            nextTask.FailureMessage = result.FailureMessage;
            nextTask.CompletedAt = DateTimeOffset.UtcNow;
            nextTask.UpdatedAt = DateTimeOffset.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);

            return new PlanningActionResponse(
                result.Succeeded,
                result.Succeeded ? null : "agent_failure",
                result.Succeeded ? $"Task '{nextTask.DisplayName}' completed." : result.FailureMessage);
        }
        catch (Exception ex)
        {
            nextTask.Status = PlanningTaskStatus.Failed;
            nextTask.FailureMessage = ex.Message;
            nextTask.CompletedAt = DateTimeOffset.UtcNow;
            nextTask.UpdatedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            return Failure("execution_error", $"Failed to execute task: {ex.Message}");
        }
    }

    public async Task<PlanningActionResponse> CancelAsync(Guid organizationId, string workflowKey, CancellationToken cancellationToken = default)
    {
        var tasks = await _dbContext.Set<PlanningTask>()
            .Where(t => t.OrganizationId == organizationId
                && t.TaskKey.StartsWith(workflowKey + ":", StringComparison.Ordinal)
                && (t.Status == PlanningTaskStatus.Pending || t.Status == PlanningTaskStatus.Queued || t.Status == PlanningTaskStatus.Running))
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var task in tasks)
        {
            task.Status = PlanningTaskStatus.Cancelled;
            task.CompletedAt = now;
            task.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new PlanningActionResponse(true, null, $"Cancelled {tasks.Count} pending/running tasks.");
    }

    public async Task<PlanningActionResponse> ResetAsync(Guid organizationId, string workflowKey, CancellationToken cancellationToken = default)
    {
        var tasks = await _dbContext.Set<PlanningTask>()
            .Where(t => t.OrganizationId == organizationId && t.TaskKey.StartsWith(workflowKey + ":", StringComparison.Ordinal))
            .ToListAsync(cancellationToken);

        _dbContext.Set<PlanningTask>().RemoveRange(tasks);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new PlanningActionResponse(true, null, $"Reset {tasks.Count} tasks for workflow '{workflowKey}'.");
    }

    #region Private Helpers

    static PlanningActionResponse Failure(string errorCode, string message) =>
        new PlanningActionResponse(false, errorCode, message);

    static Dictionary<string, string> BuildOrganizationContext(CSweet.Domain.Core.Organization org)
    {
        var context = new Dictionary<string, string>
        {
            ["OrganizationName"] = org.Name,
        };

        if (!string.IsNullOrWhiteSpace(org.Industry))
            context["Industry"] = org.Industry;
        if (!string.IsNullOrWhiteSpace(org.Stage))
            context["Stage"] = org.Stage;
        if (!string.IsNullOrWhiteSpace(org.Mission))
            context["Mission"] = org.Mission;
        if (!string.IsNullOrWhiteSpace(org.PrimaryGoal))
            context["PrimaryGoal"] = org.PrimaryGoal;
        if (!string.IsNullOrWhiteSpace(org.ConstraintsJson))
            context["Constraints"] = org.ConstraintsJson;

        return context;
    }

    async Task<string> BuildUserPrompt(PlanningTask task, CSweet.Domain.Core.Organization org, string workflowKey, CancellationToken cancellationToken)
    {
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(task.UserPrompt))
            sb.AppendLine(task.UserPrompt);

        // Include output from previously completed tasks as context
        if (!string.IsNullOrWhiteSpace(org.Mission))
        {
            sb.AppendLine($"\n--- Business Mission ---\n{org.Mission}");
        }

        if (!string.IsNullOrWhiteSpace(org.PrimaryGoal))
        {
            sb.AppendLine($"\n--- Primary Goal ---\n{org.PrimaryGoal}");
        }

        var previousTasks = await _dbContext.Set<PlanningTask>()
            .Where(t => t.OrganizationId == org.Id
                && t.TaskKey.StartsWith(workflowKey + ":", StringComparison.Ordinal)
                && t.Status == PlanningTaskStatus.Completed
                && t.SortOrder < task.SortOrder)
            .OrderBy(t => t.SortOrder)
            .ToListAsync(cancellationToken);

        if (previousTasks.Any())
        {
            sb.AppendLine("\n--- Previous Task Outputs ---");
            foreach (var prev in previousTasks)
            {
                sb.AppendLine($"\n[{prev.DisplayName}]:");
                var preview = prev.OutputContent?.Length > 4000
                    ? prev.OutputContent[..4000] + "..."
                    : prev.OutputContent;
                sb.AppendLine(preview);
            }
        }

        return sb.ToString();
    }

    static List<(string Key, string DisplayName, string AgentKey, string SystemPrompt, string UserPrompt, bool IsRequired)>
        ParseTaskDefinitions(string? json)
    {
        // Default workflow if no JSON definition
        return DefaultBusinessPlanningWorkflow();
    }

    static List<(string Key, string DisplayName, string AgentKey, string SystemPrompt, string UserPrompt, bool IsRequired)>
        DefaultBusinessPlanningWorkflow()
    {
        return new List<(string, string, string, string, string, bool)>
        {
            (
                "situation-analysis",
                "Situation Analysis",
                "business-strategist",
                BusinessStrategistAgentProfile.SystemPrompt,
                "Perform a comprehensive situation analysis for this organization. Assess the current state, market position, internal capabilities, and external environment. Provide a clear, structured analysis with actionable insights.",
                true),
            (
                "swot",
                "SWOT Analysis",
                "business-strategist",
                BusinessStrategistAgentProfile.SystemPrompt,
                "Create a detailed SWOT analysis (Strengths, Weaknesses, Opportunities, Threats) based on the organization context and any previous analysis. Be specific and actionable.",
                true),
            (
                "market-analysis",
                "Market Analysis",
                "business-strategist",
                BusinessStrategistAgentProfile.SystemPrompt,
                "Analyze the target market including size, growth trends, customer segments, and market dynamics. Provide data-driven insights where possible.",
                true),
            (
                "competitive-landscape",
                "Competitive Landscape",
                "business-strategist",
                BusinessStrategistAgentProfile.SystemPrompt,
                "Map the competitive landscape. Identify direct and indirect competitors, their strengths, and the organization's competitive positioning.",
                false),
            (
                "strategic-direction",
                "Strategic Direction",
                "business-strategist",
                BusinessStrategistAgentProfile.SystemPrompt,
                "Define the strategic direction including vision, mission, strategic priorities, and key objectives for the next 1-3 years.",
                true),
            (
                "operating-model",
                "Operating Model",
                "business-strategist",
                BusinessStrategistAgentProfile.SystemPrompt,
                "Design the operating model including organizational structure, key processes, technology needs, and resource requirements.",
                false),
            (
                "financial-projections",
                "Financial Projections",
                "business-strategist",
                BusinessStrategistAgentProfile.SystemPrompt,
                "Create financial projections including revenue forecasts, cost structure, key financial metrics, and funding requirements.",
                false),
            (
                "risk-assessment",
                "Risk Assessment",
                "business-strategist",
                BusinessStrategistAgentProfile.SystemPrompt,
                "Identify and assess key risks including strategic, operational, financial, and market risks. Provide mitigation strategies for each.",
                true),
            (
                "implementation-roadmap",
                "Implementation Roadmap",
                "business-strategist",
                BusinessStrategistAgentProfile.SystemPrompt,
                "Create a detailed implementation roadmap with phases, milestones, timelines, resource allocation, and success metrics.",
                true),
            (
                "executive-summary",
                "Executive Summary",
                "business-strategist",
                BusinessStrategistAgentProfile.SystemPrompt,
                "Synthesize all previous analyses into a concise executive summary. Highlight key findings, strategic recommendations, and immediate next steps.",
                true)
        };
    }

    #endregion
}
