using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Application.Setup;
using CSweet.Application.Core;
using CSweet.Application.Agents;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using ResolveHiringRecommendationRequest = CSweet.Contracts.Core.ResolveHiringRecommendationRequest;

namespace CSweet.AgentHost.Broker;

public sealed class WorkforcePlatformCapabilityHandler(
    CSweetDbContext db,
    IAuditEventWriter audit,
    IEnumerable<IWorkforceCatalogProvider> workforceCatalogs,
    IEnumerable<IBusinessPatternProvider> businessPatternProviders,
    IHiringService? hiring = null,
    IAgentCatalogService? agentCatalog = null) : IPlatformCapabilityHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> HandledCapabilities = new HashSet<string>(StringComparer.Ordinal)
    {
        PlatformCapabilities.BusinessProfileRead,
        PlatformCapabilities.BusinessProfileUpdateExplicit,
        PlatformCapabilities.BusinessProfileProposeUpdate,
        PlatformCapabilities.OrganizationSnapshotRead,
        PlatformCapabilities.BusinessPatternSearch,
        PlatformCapabilities.WorkstreamPlanPropose,
        PlatformCapabilities.WorkforceSearch,
        AgentCatalogCapabilities.Search,
        PlatformCapabilities.WorkforcePlanPropose,
        PlatformCapabilities.FinanceProfileRead,
        PlatformCapabilities.FinanceProfileProposeUpdate,
        PlatformCapabilities.BudgetEvaluate,
        PlatformCapabilities.ApprovalPropose,
        PlatformCapabilities.ManagementCycleRead,
        HiringCapabilities.ListRecommendations,
        HiringCapabilities.UpsertRecommendation,
        HiringCapabilities.ResolveRecommendation,
        HiringCapabilities.StageWorkflow
    };
    private static readonly HashSet<string> ExplicitFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "businessType", "industry", "description", "targetCustomers",
        "offerings", "revenueModel", "jurisdictions", "operatingStyle", "constraints", "tools",
        "timeZone"
    };

    public bool CanHandle(string capability) => HandledCapabilities.Contains(capability);

    public async IAsyncEnumerable<CapabilityResult> HandleAsync(
        AgentSession session,
        RequestCapability request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await HandleCoreAsync(session, request, cancellationToken);
    }

    private async Task<CapabilityResult> HandleCoreAsync(AgentSession session, RequestCapability request, CancellationToken token)
    {
        if (session.Grant.RequestedCapabilities?.Contains(request.Capability) != true)
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, $"The installation is not granted {request.Capability}.");
        if (!Guid.TryParse(session.BusinessId, out var organizationId) ||
            !await db.CoreOrganizations.AsNoTracking().AnyAsync(x => x.Id == organizationId, token))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound, "The organization was not found.");
        if (!Guid.TryParse(session.InstallationId, out var installationId))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, "The installation identity is invalid.");

        try
        {
            return request.Capability switch
            {
                PlatformCapabilities.BusinessProfileRead => Success(request.RequestId, await ReadBusinessProfileAsync(organizationId, token)),
                PlatformCapabilities.BusinessProfileUpdateExplicit => await UpdateExplicitProfileAsync(request, organizationId, token),
                PlatformCapabilities.BusinessProfileProposeUpdate => await PersistProposalAsync<ProposedProfileUpdateRequest>(request, organizationId, installationId, "business-profile.update", "Sensitive business-profile update", "StrategicChange", token),
                PlatformCapabilities.OrganizationSnapshotRead => Success(request.RequestId, await ReadSnapshotAsync(organizationId, token)),
                PlatformCapabilities.BusinessPatternSearch => Success(request.RequestId, await SearchPatternsAsync(Read<BusinessPatternSearchRequest>(request), token)),
                PlatformCapabilities.WorkstreamPlanPropose => await PersistProposalAsync<WorkstreamPlanProposalRequest>(request, organizationId, installationId, "workstream.create", "Create a managed workstream", "OrganizationalChange", token),
                PlatformCapabilities.WorkforceSearch => Success(request.RequestId, await SearchWorkforceAsync(
                    organizationId,
                    Read<WorkforceSearchRequest>(request),
                    session.Grant.RequestedCapabilities?.Contains(AgentCatalogCapabilities.Search) == true,
                    token)),
                AgentCatalogCapabilities.Search => Success(request.RequestId, await SearchAgentsAsync(
                    organizationId,
                    Read<AvailableAgentSearchQuery>(request),
                    token)),
                PlatformCapabilities.WorkforcePlanPropose => await PersistProposalAsync<WorkforcePlanProposalRequest>(request, organizationId, installationId, "workforce-plan.apply", "Apply a workforce plan", "Hiring", token),
                PlatformCapabilities.FinanceProfileRead => Success(request.RequestId, await ReadFinanceAsync(organizationId, token)),
                PlatformCapabilities.FinanceProfileProposeUpdate => await PersistProposalAsync<FinancialProfileProposalRequest>(request, organizationId, installationId, "finance-profile.update", "Update financial operating goals or controls", "Financial", token),
                PlatformCapabilities.BudgetEvaluate => await EvaluateBudgetAsync(request, organizationId, token),
                PlatformCapabilities.ApprovalPropose => await PersistApprovalAsync(request, organizationId, installationId, token),
                PlatformCapabilities.ManagementCycleRead => Success(request.RequestId, await ReadManagementCycleAsync(organizationId, token)),
                HiringCapabilities.ListRecommendations => Success(request.RequestId,
                    new CSweet.Contracts.Core.HiringBacklogResponse(await (hiring ?? throw new InvalidOperationException("The hiring service is unavailable."))
                        .ListRecommendationsForInstallationAsync(organizationId, installationId, token))),
                HiringCapabilities.UpsertRecommendation => Success(request.RequestId,
                    await (hiring ?? throw new InvalidOperationException("The hiring service is unavailable.")).UpsertRecommendationAsync(organizationId, installationId,
                        Read<CSweet.Contracts.Core.UpsertHiringRecommendationRequest>(request), token)),
                HiringCapabilities.ResolveRecommendation => Success(request.RequestId,
                    await (hiring ?? throw new InvalidOperationException("The hiring service is unavailable.")).ResolveRecommendationAsync(
                        organizationId, installationId,
                        Read<CSweet.Contracts.Core.ResolveHiringRecommendationRequest>(request), token)),
                HiringCapabilities.StageWorkflow => Success(request.RequestId,
                    await (hiring ?? throw new InvalidOperationException("The hiring service is unavailable.")).StageWorkflowAsync(organizationId, installationId,
                        Read<CSweet.Contracts.Core.StageHiringWorkflowRequest>(request), token)),
                _ => Failure(request.RequestId, PlatformCapabilityErrorCode.NotFound, "The platform capability is not implemented.")
            };
        }
        catch (JsonException)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, "The capability payload is not valid JSON.");
        }
        catch (DbUpdateConcurrencyException)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, "The record changed; reload it and retry with the current revision.");
        }
        catch (InvalidOperationException exception)
        {
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, exception.Message);
        }
    }

    private async Task<BusinessProfileResponse> ReadBusinessProfileAsync(Guid organizationId, CancellationToken token)
    {
        var organization = await db.CoreOrganizations.AsNoTracking().SingleAsync(x => x.Id == organizationId, token);
        var profile = await db.BusinessProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId, token);
        return new BusinessProfileResponse(
            organizationId, organization.Name, profile?.BusinessType, organization.Industry, profile?.Description,
            organization.Mission, organization.Stage, ReadList(profile?.TargetCustomersJson), ReadList(profile?.OfferingsJson),
            profile?.RevenueModel, ReadList(profile?.JurisdictionsJson), profile?.OperatingStyle,
            ReadLegacyConstraints(organization.ConstraintsJson), ReadList(profile?.ToolsJson), profile?.RiskPreference,
            profile?.TimeZone ?? "UTC", profile?.Revision ?? 0, profile?.Completeness ?? CalculateCompleteness(organization, profile),
            ReadDictionary<ProfileFieldProvenance>(profile?.ProvenanceJson));
    }

    private async Task<CapabilityResult> UpdateExplicitProfileAsync(RequestCapability request, Guid organizationId, CancellationToken token)
    {
        var input = Read<ExplicitBusinessProfileUpdateRequest>(request);
        if (input.Changes.Count == 0 || input.Changes.Keys.Any(key => !ExplicitFields.Contains(key)))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ApprovalRequired, "Only explicit low-risk business facts may be updated directly.");
        if (!Guid.TryParse(input.ConversationId, out var conversationId) || !Guid.TryParse(input.MessageId, out var messageId) || !Guid.TryParse(input.UserId, out var userId))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, "Conversation, message, and user provenance are required.");
        var sourceExists = await db.CoreConversationMessages.AsNoTracking().AnyAsync(x => x.Id == messageId &&
            x.ConversationId == conversationId && x.SenderOrganizationUserId == userId &&
            x.Conversation!.OrganizationId == organizationId && x.Role == ConversationRole.User, token);
        if (!sourceExists)
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Denied, "The explicit-fact source message could not be verified.");

        var duplicate = await db.AuditEvents.AsNoTracking().AnyAsync(x => x.EventType == "business-profile.explicit-updated" &&
            x.EntityId == organizationId && x.MetadataJson != null && x.MetadataJson.Contains(input.IdempotencyKey), token);
        if (duplicate)
            return Success(request.RequestId, new MutationResponse(true, input.ExpectedRevision, null, "The update was already applied."));

        var organization = await db.CoreOrganizations.SingleAsync(x => x.Id == organizationId, token);
        var profile = await db.BusinessProfiles.SingleOrDefaultAsync(x => x.OrganizationId == organizationId, token);
        if (profile is null)
        {
            profile = new BusinessProfile { Id = Guid.NewGuid(), OrganizationId = organizationId, UpdatedAt = DateTimeOffset.UtcNow };
            db.BusinessProfiles.Add(profile);
        }
        else if (profile.Revision != input.ExpectedRevision)
            return Failure(request.RequestId, PlatformCapabilityErrorCode.Conflict, $"Expected revision {input.ExpectedRevision}, current revision is {profile.Revision}.");

        var provenance = ReadDictionary<ProfileFieldProvenance>(profile.ProvenanceJson).ToDictionary();
        foreach (var change in input.Changes)
        {
            ApplyExplicitChange(organization, profile, change.Key, change.Value);
            provenance[change.Key] = new ProfileFieldProvenance("OwnerMessage", input.ConversationId, input.MessageId, DateTimeOffset.UtcNow);
        }
        profile.Revision = Math.Max(1, profile.Revision + 1);
        profile.ProvenanceJson = JsonSerializer.Serialize(provenance, JsonOptions);
        profile.Completeness = CalculateCompleteness(organization, profile);
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        organization.UpdatedAt = profile.UpdatedAt;
        await db.SaveChangesAsync(token);
        await audit.WriteAsync("business-profile.explicit-updated", nameof(BusinessProfile), organizationId,
            $"Updated explicit business facts: {string.Join(", ", input.Changes.Keys)}.",
            JsonSerializer.Serialize(new { input.IdempotencyKey, input.ConversationId, input.MessageId, fields = input.Changes.Keys }, JsonOptions), token);
        return Success(request.RequestId, new MutationResponse(true, profile.Revision, null, "Explicit business facts were saved."));
    }

    private async Task<OrganizationSnapshotResponse> ReadSnapshotAsync(Guid organizationId, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var organization = await db.CoreOrganizations.AsNoTracking().SingleAsync(x => x.Id == organizationId, token);
        var people = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .Select(x => new OrganizationPerson(x.Id, x.DisplayName, x.EmployeeType.ToString(), x.RoleId, x.ReportsToOrganizationUserId, x.AgentInstallationId, x.IsActive)).ToListAsync(token);
        var roles = await db.CoreRoles.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .Select(x => new OrganizationRole(x.Id, x.Name, x.Description, x.ResponsibilitiesJson)).ToListAsync(token);
        var objectives = await db.CoreStrategicObjectives.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .Select(x => new OrganizationObjective(x.Id, x.Title, x.Description, x.Status.ToString(), x.TargetDate)).ToListAsync(token);
        var workstreams = await db.Workstreams.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .Select(x => new WorkstreamSummary(x.Id, x.Name, x.Outcome, x.Status.ToString(), x.LifecycleStage,
                x.AccountableManagerOrganizationUserId, x.TargetDate, x.BudgetAmount, x.BudgetCurrency)).ToListAsync(token);
        var workers = await db.CoreWorkers.AsNoTracking().Where(x => x.OrganizationId == organizationId || x.OrganizationId == null).ToListAsync(token);
        var signals = new List<OperatingSignal>();
        signals.AddRange(workstreams.Where(x => x.Status == "Blocked")
            .Select(x => new OperatingSignal("Blocker", "Critical", $"Unblock {x.Name}: {x.Outcome}", "Workstream", x.Id, x.TargetDate)));
        signals.AddRange(workstreams.Where(x => x.AccountableManagerOrganizationUserId is null && x.Status is not ("Completed" or "Cancelled"))
            .Select(x => new OperatingSignal("Staffing", "High", $"Assign an accountable manager to {x.Name}.", "Workstream", x.Id, x.TargetDate)));
        signals.AddRange(workstreams.Where(x => x.TargetDate < now && x.Status is not ("Completed" or "Cancelled"))
            .Select(x => new OperatingSignal("Deadline", "Urgent", $"{x.Name} is past its target date.", "Workstream", x.Id, x.TargetDate)));
        var stale = await db.ManagementCheckInRequests.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.Status == "Stale")
            .OrderBy(x => x.DueAt).Take(10).ToListAsync(token);
        signals.AddRange(stale.Select(x => new OperatingSignal("Management", "High", $"A {x.CheckInType} report is stale.",
            "ManagementCheckIn", x.Id, x.DueAt)));
        var resourceNeeds = await db.ResourceNeedReports.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.Status == "Open")
            .OrderByDescending(x => x.ReportedAt).Take(10).ToListAsync(token);
        signals.AddRange(resourceNeeds.Select(x => new OperatingSignal("ResourceNeed",
            x.Urgency.Equals("Critical", StringComparison.OrdinalIgnoreCase) ? "Critical" :
            x.Urgency.Equals("High", StringComparison.OrdinalIgnoreCase) ? "High" : "Important",
            $"Resource needed: {x.BusinessOutcome}", "ResourceNeed", x.Id)));
        var pendingApprovals = await db.ActionProposals.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.Status == ProposalStatus.Pending)
            .OrderBy(x => x.CreatedAt).Take(10).ToListAsync(token);
        signals.AddRange(pendingApprovals.Select(x => new OperatingSignal("Approval", "High", x.Summary, "ActionProposal", x.Id)));
        var budgets = await db.Budgets.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.IsActive && x.PeriodStart <= now && x.PeriodEnd > now)
            .OrderBy(x => x.LimitAmount).ToListAsync(token);
        BudgetPositionSummary? budgetPosition = null;
        if (budgets.Count > 0)
        {
            var limiting = budgets[0];
            var reserved = await db.BudgetReservations.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.BudgetId == limiting.Id &&
                    x.Status == BudgetReservationStatus.Active && x.ExpiresAt > now)
                .SumAsync(x => (decimal?)x.Amount, token) ?? 0m;
            budgetPosition = new BudgetPositionSummary(limiting.Currency, limiting.LimitAmount, reserved,
                Math.Max(0m, limiting.LimitAmount - reserved), [$"{limiting.ScopeType} budget is the most restrictive active limit."]);
            if (reserved >= limiting.LimitAmount)
                signals.Add(new OperatingSignal("Budget", "Critical", $"The active {limiting.ScopeType} budget is fully reserved.",
                    "Budget", limiting.Id, limiting.PeriodEnd, reserved, limiting.Currency));
            else if (reserved >= limiting.LimitAmount * 0.8m)
                signals.Add(new OperatingSignal("Budget", "High", $"The active {limiting.ScopeType} budget is at least 80% reserved.",
                    "Budget", limiting.Id, limiting.PeriodEnd, reserved, limiting.Currency));
        }
        return new OrganizationSnapshotResponse(organizationId, organization.Status.ToString(), people, roles, objectives, workstreams,
            workers.Select(x => new OrganizationWorker(x.Id, x.Name, x.WorkerType.ToString(), ReadList(x.CapabilitiesJson), x.IsEnabled)).ToList(), now)
        {
            OperatingSignals = signals,
            BudgetPosition = budgetPosition
        };
    }

    private async Task<BusinessPatternSearchResponse> SearchPatternsAsync(BusinessPatternSearchRequest request, CancellationToken token)
    {
        var matches = BuiltInPatterns.All
            .Where(x => request.LifecycleStage is null || x.LifecycleStage.Equals(request.LifecycleStage, StringComparison.OrdinalIgnoreCase))
            .Where(x => request.BusinessType is null || x.BusinessTypes.Count == 0 || x.BusinessTypes.Any(t => request.BusinessType.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Clamp(request.MaximumResults, 1, 10))
            .Select(x => new BusinessPatternMatch(x.Id, x.Version, x.Name, x.LifecycleStage, x.Workstreams, x.Risks,
                x.FinancialConsiderations, "C-Sweet curated pattern catalog", x.ReviewedAt, 0.9m)).ToList();
        var unavailable = new List<string>();
        foreach (var provider in businessPatternProviders)
        {
            try
            {
                var result = await provider.SearchAsync(request, token);
                matches.AddRange(result.Matches);
                if (!string.IsNullOrWhiteSpace(result.UnavailableReason)) unavailable.Add($"{provider.ProviderKey}: {result.UnavailableReason}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                unavailable.Add($"{provider.ProviderKey}: unavailable");
            }
        }
        matches = matches.OrderByDescending(x => x.MatchScore).Take(Math.Clamp(request.MaximumResults, 1, 10)).ToList();
        var reason = matches.Count == 0
            ? "No curated or plugin pattern matched; broker-approved cited research is required before proposing an unverified pattern."
            : unavailable.Count == 0 ? null : string.Join(" ", unavailable);
        return new BusinessPatternSearchResponse(matches, matches.Count == 0, reason);
    }

    private async Task<WorkforceSearchResponse> SearchWorkforceAsync(
        Guid organizationId,
        WorkforceSearchRequest request,
        bool includeAgentCatalog,
        CancellationToken token)
    {
        var required = request.RequiredCapabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<CSweet.Agent.SDK.WorkforceCandidate>();
        var rejected = new List<RejectedWorkforceCandidate>();
        var workers = await db.CoreWorkers.AsNoTracking().Where(x => x.IsEnabled && (x.OrganizationId == organizationId || x.OrganizationId == null)).ToListAsync(token);
        var assignedWorkerIds = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.IsActive && x.WorkerId != null)
            .Select(x => x.WorkerId!.Value).ToListAsync(token);
        foreach (var worker in workers)
        {
            var capabilities = ReadList(worker.CapabilitiesJson);
            var missing = required.Except(capabilities, StringComparer.OrdinalIgnoreCase).ToList();
            var isCurrentStaff = assignedWorkerIds.Contains(worker.Id);
            if (!isCurrentStaff && worker.WorkerType != WorkerType.Human && !includeAgentCatalog)
                continue;
            var source = isCurrentStaff ? "CurrentStaff" : "InstalledWorker";
            if (missing.Count > 0)
            {
                rejected.Add(new RejectedWorkforceCandidate(worker.Id.ToString(), worker.Name, source, missing.Select(x => $"Missing capability {x}").ToList()));
                continue;
            }
            if (request.HumanRequired && worker.WorkerType != WorkerType.Human)
            {
                rejected.Add(new RejectedWorkforceCandidate(worker.Id.ToString(), worker.Name, source, ["A verified human is required."]));
                continue;
            }
            var cost = ReadCost(worker.CostModelJson);
            if (request.MaximumBudget is { } maximum && cost is { } value && value > maximum)
            {
                rejected.Add(new RejectedWorkforceCandidate(worker.Id.ToString(), worker.Name, source, ["Estimated cost exceeds the requested budget."]));
                continue;
            }
            candidates.Add(new CSweet.Agent.SDK.WorkforceCandidate(worker.Id.ToString(), source, worker.WorkerType.ToString(), worker.Name,
                capabilities, [], cost, request.Currency, source == "CurrentStaff" ? 1.0m : 0.85m,
                source == "CurrentStaff" ? "Already on staff and meets all required capabilities." : "Installed locally and meets all required capabilities.", worker.RequiresHumanApproval));
        }

        if (includeAgentCatalog)
        {
            var installations = await db.AgentInstallations.AsNoTracking().Include(x => x.PackageVersion).Include(x => x.Grant)
                .Where(x => x.BusinessId == organizationId.ToString() && x.IsEnabled).ToListAsync(token);
            var assignedInstallations = await db.CoreOrganizationUsers.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.AgentInstallationId != null)
                .Select(x => x.AgentInstallationId!.Value).ToListAsync(token);
            foreach (var installation in installations.Where(x => !assignedInstallations.Contains(x.Id) && x.PackageVersion != null && x.Grant != null))
            {
                var capabilities = ReadList(installation.Grant!.ProvidedCapabilitiesJson);
                var missing = required.Except(capabilities, StringComparer.OrdinalIgnoreCase).ToList();
                if (missing.Count > 0)
                {
                    rejected.Add(new RejectedWorkforceCandidate(installation.Id.ToString(), installation.PackageVersion!.AgentName, "InstalledPlugin", missing.Select(x => $"Missing capability {x}").ToList()));
                    continue;
                }
                candidates.Add(new CSweet.Agent.SDK.WorkforceCandidate(installation.Id.ToString(), "InstalledPlugin", "LocalAgent", installation.PackageVersion!.AgentName,
                    capabilities, [], null, request.Currency, 0.8m, "Installed agent plugin with all required capabilities.", true));
            }
        }

        var marketplaceAvailable = false;
        var unavailableReasons = new List<string>();
        var providers = workforceCatalogs.OrderBy(x => x.CatalogKind).ToList();
        var digitalProviders = includeAgentCatalog
            ? providers.Where(x => x.CatalogKind is not WorkforceCatalogKind.HumanMarketplace).ToList()
            : [];
        var humanProviders = providers.Where(x => x.CatalogKind is WorkforceCatalogKind.HumanMarketplace or WorkforceCatalogKind.HybridMarketplace).ToList();
        if (request.HumanRequired)
            marketplaceAvailable |= await SearchCatalogsAsync(humanProviders, request, candidates, rejected, unavailableReasons, token);
        else
        {
            marketplaceAvailable |= await SearchCatalogsAsync(digitalProviders, request, candidates, rejected, unavailableReasons, token);
            if (candidates.Count == 0)
                marketplaceAvailable |= await SearchCatalogsAsync(
                    humanProviders.Where(x => digitalProviders.All(d => d.ProviderKey != x.ProviderKey)),
                    request, candidates, rejected, unavailableReasons, token);
        }
        if (providers.Count == 0)
            unavailableReasons.Add("No marketplace provider is connected; results include current staff and installed local resources only.");
        var ranked = candidates.OrderByDescending(x => x.Score)
            .Take(Math.Clamp(request.MaximumResults, 1, 25)).ToList();
        var opaque = new List<CSweet.Agent.SDK.WorkforceCandidate>(ranked.Count);
        foreach (var candidate in ranked)
        {
            var snapshot = new CSweet.Domain.Core.WorkforceCandidate
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, Source = candidate.Source,
                ExternalCandidateId = candidate.CandidateId, DisplayName = candidate.Name,
                CapabilitiesJson = JsonSerializer.Serialize(candidate.Capabilities, JsonOptions),
                Score = candidate.Score, EstimatedCost = candidate.EstimatedCost, Currency = candidate.Currency,
                IsHuman = candidate.ResourceType.Equals("Human", StringComparison.OrdinalIgnoreCase),
                IsAvailable = true,
                ExplanationJson = JsonSerializer.Serialize(new
                {
                    candidate.ResourceType, candidate.Credentials, candidate.Rationale,
                    candidate.RequiresSeparateApproval, candidate.RepositoryUrl
                }, JsonOptions)
            };
            db.WorkforceCandidates.Add(snapshot);
            opaque.Add(candidate with { CandidateId = $"candidate:{snapshot.Id:N}" });
        }
        if (opaque.Count > 0) await db.SaveChangesAsync(token);
        return new WorkforceSearchResponse(opaque, rejected, marketplaceAvailable,
            unavailableReasons.Count == 0 ? null : string.Join(" ", unavailableReasons));
    }

    private async Task<AvailableAgentSearchResult> SearchAgentsAsync(
        Guid organizationId,
        AvailableAgentSearchQuery request,
        CancellationToken token)
    {
        var catalog = agentCatalog ?? throw new InvalidOperationException("The agent catalog service is unavailable.");
        var result = await catalog.GetAvailableAgentsAsync(organizationId, request, token);
        var materialized = new List<AvailableAgent>(result.Agents.Count);
        foreach (var agent in result.Agents)
        {
            var source = agent.Source switch
            {
                AgentCatalogSource.Installed => "InstalledPlugin",
                AgentCatalogSource.LocalDirectory => "LocalDirectoryCatalog",
                AgentCatalogSource.FirstPartyCatalog => "CSweetEmbeddedCatalog",
                AgentCatalogSource.Marketplace => "CSweetMarketplace",
                _ => throw new InvalidOperationException("The agent catalog source is unsupported.")
            };
            var externalId = agent.InstallationId?.ToString("D") ?? agent.AgentReference;
            var snapshot = new CSweet.Domain.Core.WorkforceCandidate
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                Source = source,
                ExternalCandidateId = externalId,
                DisplayName = agent.Name,
                CapabilitiesJson = JsonSerializer.Serialize(agent.Capabilities, JsonOptions),
                Score = agent.Score,
                EstimatedCost = agent.Price,
                Currency = agent.Currency,
                IsHuman = false,
                IsAvailable = agent.Availability is AgentAvailabilityState.AvailableToInstall or
                    AgentAvailabilityState.InstalledEnabled,
                ExplanationJson = JsonSerializer.Serialize(new
                {
                    ResourceType = "Agent",
                    Credentials = Array.Empty<string>(),
                    Rationale = $"{agent.Trust}. Catalog source: {agent.Source}.",
                    RequiredGrants = Array.Empty<string>(),
                    agent.RepositoryUrl,
                    CatalogReference = agent.AgentReference
                }, JsonOptions)
            };
            db.WorkforceCandidates.Add(snapshot);
            materialized.Add(agent with { AgentReference = $"candidate:{snapshot.Id:N}" });
        }
        if (materialized.Count > 0) await db.SaveChangesAsync(token);
        return new(materialized, result.Sources);
    }

    private static async Task<bool> SearchCatalogsAsync(
        IEnumerable<IWorkforceCatalogProvider> providers,
        WorkforceSearchRequest request,
        ICollection<CSweet.Agent.SDK.WorkforceCandidate> candidates,
        ICollection<RejectedWorkforceCandidate> rejected,
        ICollection<string> unavailableReasons,
        CancellationToken token)
    {
        var available = false;
        foreach (var provider in providers)
        {
            try
            {
                var result = await provider.SearchAsync(request, token);
                available |= result.MarketplaceAvailable;
                foreach (var candidate in result.Candidates) candidates.Add(candidate);
                foreach (var alternative in result.Rejected) rejected.Add(alternative);
                if (!string.IsNullOrWhiteSpace(result.UnavailableReason)) unavailableReasons.Add($"{provider.ProviderKey}: {result.UnavailableReason}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                unavailableReasons.Add($"{provider.ProviderKey}: unavailable");
            }
        }
        return available;
    }

    private async Task<FinancialOperatingProfileResponse> ReadFinanceAsync(Guid organizationId, CancellationToken token)
    {
        var profile = await db.FinancialOperatingProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId, token);
        return new FinancialOperatingProfileResponse(organizationId, profile?.BaseCurrency ?? "USD", profile?.RevenueTarget,
            profile?.ProfitTarget, profile?.OwnerCompensationTarget, profile?.MinimumRunwayMonths,
            profile?.MaximumMonthlyWorkforceSpend, profile?.PerEngagementCap, profile?.MaximumConcurrentHires,
            profile?.RoutingPreference ?? "Balanced", profile?.Revision ?? 0);
    }

    private async Task<CapabilityResult> EvaluateBudgetAsync(RequestCapability request, Guid organizationId, CancellationToken token)
    {
        var input = Read<BudgetEvaluationRequest>(request);
        if (input.Amount < 0 || string.IsNullOrWhiteSpace(input.Currency))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, "A non-negative amount and currency are required.");
        var now = DateTimeOffset.UtcNow;
        var budgets = await db.Budgets.Where(x => x.OrganizationId == organizationId && x.IsActive && x.PeriodStart <= now && x.PeriodEnd > now &&
            (x.ScopeType == BudgetScopeType.Organization || x.ScopeType.ToString() == input.ScopeType && x.ScopeId == input.ScopeId)).ToListAsync(token);
        if (budgets.Count == 0)
            return Failure(request.RequestId, PlatformCapabilityErrorCode.BudgetExceeded, "No enforceable budget is configured for this paid action.");
        if (budgets.Any(x => !string.Equals(x.Currency, input.Currency, StringComparison.OrdinalIgnoreCase)))
            return Failure(request.RequestId, PlatformCapabilityErrorCode.ValidationFailed, "Currency conversion is not supported; use the configured budget currency.");
        var evaluations = new List<(Budget Budget, decimal Available)>();
        foreach (var budget in budgets)
        {
            var reserved = await db.BudgetReservations.Where(x => x.BudgetId == budget.Id && x.Status == BudgetReservationStatus.Active && x.ExpiresAt > now).SumAsync(x => (decimal?)x.Amount, token) ?? 0;
            evaluations.Add((budget, budget.LimitAmount - reserved));
        }
        var mostRestrictive = evaluations.OrderBy(x => x.Available).First();
        if (input.Amount > mostRestrictive.Available)
            return Success(request.RequestId, new BudgetEvaluationResponse(false, mostRestrictive.Available, input.Currency, null, ["The most restrictive applicable budget does not have enough available funds."]));
        Guid? reservationId = null;
        if (input.Reserve && input.Amount > 0)
        {
            var existing = await db.BudgetReservations.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IdempotencyKey == input.IdempotencyKey, token);
            if (existing is null)
            {
                existing = new BudgetReservation { Id = Guid.NewGuid(), OrganizationId = organizationId, BudgetId = mostRestrictive.Budget.Id,
                    Amount = input.Amount, Currency = input.Currency.ToUpperInvariant(), Purpose = input.Purpose, IdempotencyKey = input.IdempotencyKey,
                    CreatedAt = now, ExpiresAt = now.AddHours(24) };
                db.BudgetReservations.Add(existing); await db.SaveChangesAsync(token);
                await audit.WriteAsync("budget.reserved", nameof(BudgetReservation), existing.Id,
                    $"Reserved {existing.Amount} {existing.Currency} for {existing.Purpose}.",
                    JsonSerializer.Serialize(new { organizationId, existing.BudgetId, existing.IdempotencyKey }, JsonOptions), token);
            }
            reservationId = existing.Id;
        }
        return Success(request.RequestId, new BudgetEvaluationResponse(true, mostRestrictive.Available - input.Amount, input.Currency, reservationId, []));
    }

    private async Task<ManagementCycleResponse> ReadManagementCycleAsync(Guid organizationId, CancellationToken token)
    {
        var cycle = await db.ManagementCycles.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IsEnabled, token);
        var profile = await db.BusinessProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.OrganizationId == organizationId, token);
        return new ManagementCycleResponse(cycle?.Id, cycle?.TimeZone ?? profile?.TimeZone ?? "UTC", cycle?.DailyCheckInLocalTime ?? "09:00",
            cycle?.DailyDueLocalTime ?? "11:00", cycle?.WeeklyReviewDay ?? "Friday", cycle?.WeeklyReviewLocalTime ?? "15:00",
            cycle?.QuietHoursStart ?? "18:00", cycle?.QuietHoursEnd ?? "08:00", cycle?.NextReviewAt)
        {
            ExecutiveBriefing = new ExecutiveBriefingScheduleResponse(cycle?.ExecutiveBriefingEnabled ?? true,
                cycle?.StartupBriefingEnabled ?? true, cycle?.ExecutiveBriefingCadence ?? "Weekdays",
                cycle?.ExecutiveBriefingWeeklyDay ?? "Friday", cycle?.ExecutiveBriefingLocalTime ?? "09:00",
                cycle?.NextExecutiveBriefingAt)
        };
    }

    private async Task<CapabilityResult> PersistApprovalAsync(RequestCapability request, Guid organizationId, Guid installationId, CancellationToken token)
    {
        var input = Read<ApprovalProposalRequest>(request);
        var proposal = await GetOrCreateProposalAsync(organizationId, installationId, input.ActionType, input.Summary, input.PayloadJson, input.RiskClass, input.IdempotencyKey, token);
        return Success(request.RequestId, new ApprovalProposalResponse(proposal.Id, proposal.Status.ToString(), proposal.CreatedAt));
    }

    private async Task<CapabilityResult> PersistProposalAsync<T>(RequestCapability request, Guid organizationId, Guid installationId,
        string actionType, string summary, string riskClass, CancellationToken token)
    {
        var input = Read<T>(request);
        var property = typeof(T).GetProperty("IdempotencyKey") ?? throw new InvalidOperationException("The proposal requires an idempotency key.");
        var idempotencyKey = property.GetValue(input) as string;
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new InvalidOperationException("The proposal requires an idempotency key.");
        var payload = JsonSerializer.Serialize(input, JsonOptions);
        var proposal = await GetOrCreateProposalAsync(organizationId, installationId, actionType, summary, payload, riskClass, idempotencyKey, token);
        return Success(request.RequestId, new MutationResponse(false, 0, proposal.Id, "The change requires approval."));
    }

    private async Task<ActionProposal> GetOrCreateProposalAsync(Guid organizationId, Guid installationId, string actionType,
        string summary, string payload, string riskClass, string idempotencyKey, CancellationToken token)
    {
        var existing = await db.ActionProposals.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.IdempotencyKey == idempotencyKey, token);
        if (existing is not null) return existing;
        var proposal = new ActionProposal { Id = Guid.NewGuid(), OrganizationId = organizationId, AgentInstallationId = installationId,
            ActionType = actionType, Summary = summary, PayloadJson = payload, RiskClass = riskClass, IdempotencyKey = idempotencyKey,
            CreatedAt = DateTimeOffset.UtcNow };
        db.ActionProposals.Add(proposal); await db.SaveChangesAsync(token);
        await audit.WriteAsync("action.proposed", nameof(ActionProposal), proposal.Id, summary,
            JsonSerializer.Serialize(new { organizationId, installationId, actionType, idempotencyKey }, JsonOptions), token);
        return proposal;
    }

    private static void ApplyExplicitChange(Organization organization, BusinessProfile profile, string key, JsonElement value)
    {
        switch (key.ToLowerInvariant())
        {
            case "name": organization.Name = RequiredString(value, key); break;
            case "industry": organization.Industry = OptionalString(value); break;
            case "lifecyclestage": organization.Stage = OptionalString(value); break;
            case "businesstype": profile.BusinessType = OptionalString(value); break;
            case "description": profile.Description = OptionalString(value); break;
            case "targetcustomers": profile.TargetCustomersJson = JsonSerializer.Serialize(ReadStringArray(value), JsonOptions); break;
            case "offerings": profile.OfferingsJson = JsonSerializer.Serialize(ReadStringArray(value), JsonOptions); break;
            case "revenuemodel": profile.RevenueModel = OptionalString(value); break;
            case "jurisdictions": profile.JurisdictionsJson = JsonSerializer.Serialize(ReadStringArray(value), JsonOptions); break;
            case "operatingstyle": profile.OperatingStyle = OptionalString(value); break;
            case "constraints": organization.ConstraintsJson = JsonSerializer.Serialize(ReadStringArray(value), JsonOptions); break;
            case "tools": profile.ToolsJson = JsonSerializer.Serialize(ReadStringArray(value), JsonOptions); break;
            case "riskpreference": profile.RiskPreference = OptionalString(value); break;
            case "timezone": profile.TimeZone = RequiredString(value, key); break;
            default: throw new InvalidOperationException($"Field '{key}' is not directly writable.");
        }
    }

    private static T Read<T>(RequestCapability request) => JsonSerializer.Deserialize<T>(request.Payload.Span, JsonOptions)
        ?? throw new InvalidOperationException("The capability payload is required.");
    private static IReadOnlyList<string> ReadStringArray(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        : throw new InvalidOperationException("The field must be an array of strings.");
    private static string RequiredString(JsonElement value, string key) => OptionalString(value) is { Length: > 0 } result ? result : throw new InvalidOperationException($"Field '{key}' is required.");
    private static string? OptionalString(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetString()?.Trim();
    private static IReadOnlyList<string> ReadList(string? json) { try { return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<IReadOnlyList<string>>(json, JsonOptions) ?? []; } catch (JsonException) { return []; } }
    private static IReadOnlyDictionary<string, T> ReadDictionary<T>(string? json) { try { return string.IsNullOrWhiteSpace(json) ? new Dictionary<string, T>() : JsonSerializer.Deserialize<IReadOnlyDictionary<string, T>>(json, JsonOptions) ?? new Dictionary<string, T>(); } catch (JsonException) { return new Dictionary<string, T>(); } }
    private static IReadOnlyList<string> ReadLegacyConstraints(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.ValueKind == JsonValueKind.Array ? ReadStringArray(doc.RootElement) : doc.RootElement.TryGetProperty("constraints", out var c) ? ReadStringArray(c) : []; }
        catch (JsonException) { return []; }
    }
    private static decimal? ReadCost(string? json) { if (string.IsNullOrWhiteSpace(json)) return null; try { using var doc = JsonDocument.Parse(json); return doc.RootElement.TryGetProperty("amount", out var value) && value.TryGetDecimal(out var amount) ? amount : null; } catch (JsonException) { return null; } }
    private static decimal CalculateCompleteness(Organization organization, BusinessProfile? profile)
    {
        var values = new[] { organization.Name, profile?.BusinessType, organization.Industry, profile?.Description, organization.Mission,
            organization.Stage, profile?.RevenueModel, profile?.OperatingStyle, profile?.RiskPreference, profile?.TimeZone };
        return Math.Round(values.Count(x => !string.IsNullOrWhiteSpace(x)) / (decimal)values.Length, 2);
    }
    private static CapabilityResult Success<T>(string requestId, T payload) => new() { RequestId = requestId, Succeeded = true,
        ContentType = "application/json", Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions)) };
    private static CapabilityResult Failure(string requestId, PlatformCapabilityErrorCode code, string message) => new() { RequestId = requestId,
        Succeeded = false, ContentType = "application/json", Error = message,
        Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new PlatformCapabilityError(code, message), JsonOptions)) };

    private static class BuiltInPatterns
    {
        private static readonly DateTimeOffset Reviewed = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        public static readonly IReadOnlyList<Pattern> All =
        [
            new("idea-validation", "1.0.0", "Idea validation", "Idea", [],
                [new("Validate the opportunity", "Produce evidence for a go/no-go decision.", "Product Manager", ["research.market-analysis", "product.discovery"], ["Researcher", "Customer Interviewer"], ["Owner decision review"])],
                ["Building before validating demand", "Unclear buyer and problem"], ["Cap discovery spend", "Define a validation threshold"], Reviewed),
            new("validation-evidence", "1.0.0", "Evidence-led validation", "Validation", [],
                [new("Validate demand", "Produce owner-approved evidence for a go/no-go decision.", "Product Manager", ["customer.research", "product.discovery"], ["Researcher", "Customer Interviewer"], ["Evidence quality review", "Owner go/no-go decision"])],
                ["Confirmation bias", "Testing an offer without a defined buyer"], ["Set an experiment budget", "Define evidence thresholds before spending"], Reviewed),
            new("pre-revenue-launch", "1.0.0", "Pre-revenue launch", "Pre-revenue", [],
                [new("Launch the first sellable offer", "Deliver and sell a constrained first offering.", "Product Manager", ["product.definition", "go-to-market", "delivery.planning"], ["Researcher", "Builder", "Marketing"], ["Scope review", "Launch readiness review"])],
                ["Scope expansion", "No measurable acquisition channel"], ["Set a launch budget", "Track runway and time to first revenue"], Reviewed),
            new("launch-readiness", "1.0.0", "Controlled launch", "Launch", [],
                [new("Launch the offering", "Release a supportable offer and establish measurable customer feedback.", "Program Manager", ["launch.coordination", "customer.support", "analytics"], ["Product Manager", "Marketing", "Customer Success"], ["Launch readiness review", "Post-launch decision review"])],
                ["Unowned launch dependencies", "No rollback or customer-response plan"], ["Reserve launch contingency", "Measure acquisition and service cost"], Reviewed),
            new("early-revenue-scale", "1.0.0", "Early revenue operating system", "Early revenue", [],
                [new("Create repeatable delivery", "Turn early sales into a measurable repeatable operation.", "Operations Manager", ["operations.process-design", "finance.unit-economics"], ["Operations", "Finance", "Customer Success"], ["Unit economics review"])],
                ["Founder bottleneck", "Unprofitable custom work"], ["Protect runway", "Measure contribution margin"], Reviewed),
            new("growth-portfolio", "1.0.0", "Growth portfolio management", "Growing", [],
                [new("Manage the growth portfolio", "Prioritize growth investments against capacity and margin.", "Program Manager", ["portfolio.prioritization", "capacity.planning"], ["Product Manager", "Finance", "People Operations"], ["Quarterly portfolio review"])],
                ["Uncoordinated hiring", "Hidden cross-team dependencies"], ["Use workstream budgets", "Forecast hiring impact"], Reviewed),
            new("established-optimization", "1.0.0", "Established business optimization", "Established", [],
                [new("Improve operating leverage", "Reduce risk and improve efficiency without harming customer outcomes.", "Program Manager", ["operations.optimization", "risk.management"], ["Operations", "Finance", "Quality"], ["Control effectiveness review"])],
                ["Local optimization", "Key-person risk"], ["Model ROI before restructuring", "Maintain contingency reserves"], Reviewed),
            new("turnaround", "1.0.0", "Business turnaround", "Turnaround", [],
                [new("Stabilize cash and delivery", "Restore control of cash, commitments, and critical operations.", "Turnaround Program Manager", ["cash-flow.triage", "operations.triage"], ["Finance", "Operations", "Customer Communications"], ["Weekly cash review", "Owner approval for material cuts"])],
                ["Running out of cash", "Damaging core customer relationships"], ["Thirteen-week cash forecast", "Freeze unapproved commitments"], Reviewed),
            new("exit-readiness", "1.0.0", "Exit readiness", "Exit", [],
                [new("Prepare for diligence", "Create a defensible, transferable operating record.", "Program Manager", ["due-diligence", "documentation"], ["Finance", "Legal", "Operations"], ["Credentialed legal and financial review"])],
                ["Incomplete records", "Undisclosed liabilities"], ["Budget for professional diligence", "Separate owner and company finances"], Reviewed)
        ];
        internal sealed record Pattern(string Id, string Version, string Name, string LifecycleStage, IReadOnlyList<string> BusinessTypes,
            IReadOnlyList<PatternWorkstream> Workstreams, IReadOnlyList<string> Risks, IReadOnlyList<string> FinancialConsiderations, DateTimeOffset ReviewedAt);
    }
}
