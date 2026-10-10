using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.Infrastructure.Core;

public sealed partial class HiringService
{
    private bool _executingDelegation;

    private async Task RequireChiefAsync(Guid organizationId, Guid installationId, CancellationToken token)
    {
        if (!await db.CoreOrganizationUsers.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.AgentInstallationId == installationId && x.IsActive &&
                db.LeadershipAssignments.Any(a => a.OrganizationUserId == x.Id &&
                    a.OrganizationId == organizationId && a.PositionKey == LeadershipPositionKeys.ChiefOfStaff && a.EndsAt == null), token))
            throw new UnauthorizedAccessException("Hiring delegation belongs to an active Chief of Staff in this business.");
    }

    private static HiringPolicySettings Policy(string json) =>
        JsonSerializer.Deserialize<HiringPolicySettings>(json, JsonOptions) ?? new();

    public async Task<HiringPolicyResponse> ReadPolicyAsync(Guid organizationId, Guid installationId, CancellationToken token = default)
    {
        await RequireChiefAsync(organizationId, installationId, token);
        var row = await db.ChiefHiringPolicies.AsNoTracking().SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.InstallationId == installationId, token);
        return row is null ? new(installationId, 0, new(), false) :
            new(installationId, row.Revision, Policy(row.SettingsJson), row.SetupComplete,
                row.OwnerId, row.SourceDecisionId, row.Rationale, row.SetupStage);
    }

    public async Task<HiringPolicyResponse> UpdatePolicyAsync(Guid organizationId, Guid installationId,
        Guid applicationUserId, HiringPolicySettings settings, long expectedRevision, string rationale,
        Guid? decisionId = null, CancellationToken token = default)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(token) : null;
        await LockHiringOrganizationAsync(organizationId, token);
        var result = await UpdatePolicyCoreAsync(organizationId, installationId, applicationUserId, settings, expectedRevision, rationale, decisionId, token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return result;
    }

    private async Task LockHiringOrganizationAsync(Guid organizationId, CancellationToken token)
    {
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({organizationId.ToString()}, 0))", token);
    }

    private async Task<HiringPolicyResponse> UpdatePolicyCoreAsync(Guid organizationId, Guid installationId,
        Guid applicationUserId, HiringPolicySettings settings, long expectedRevision, string rationale,
        Guid? decisionId = null, CancellationToken token = default)
    {
        await RequireChiefAsync(organizationId, installationId, token);
        var owner = await RequireOwnerAsync(organizationId, applicationUserId, token);
        if (!Enum.IsDefined(settings.Mode) || !Enum.IsDefined(settings.AutomaticLevel) || !Enum.IsDefined(settings.Publishers))
            throw new ArgumentException("Unknown hiring preference.");
        if (settings.MaximumHireCost is < 0 || settings.MaximumTotalCost is < 0)
            throw new ArgumentException("Hiring cost limits cannot be negative.");
        if (settings.Mode == HiringSelectionMode.Automatic && settings.AutomaticLevel == AutomaticHiringLevel.WithinLimits &&
            (settings.MaximumHireCost is null || settings.MaximumTotalCost is null ||
             string.IsNullOrWhiteSpace(settings.Currency) || settings.AllowedCapabilities is null || settings.AllowedNetworkAccess is null))
            throw new ArgumentException("Within limits requires explicit per-hire and total costs, currency, capabilities, and network access. Empty permission lists allow none.");
        var row = await db.ChiefHiringPolicies.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.InstallationId == installationId, token);
        if ((row?.Revision ?? 0) != expectedRevision) throw new InvalidOperationException("Hiring preferences changed. Refresh before saving.");
        row ??= new ChiefHiringPolicy { Id = Guid.NewGuid(), OrganizationId = organizationId, InstallationId = installationId, Revision = 0 };
        if (db.Entry(row).State == EntityState.Detached) db.ChiefHiringPolicies.Add(row);
        row.SettingsJson = JsonSerializer.Serialize(settings, JsonOptions);
        row.SetupComplete = true;
        row.SetupStage = "complete";
        row.OwnerId = owner.Id;
        row.SourceDecisionId = decisionId;
        row.Rationale = Required(rationale, 2048, nameof(rationale));
        row.Revision++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        db.ChiefHiringPolicyRevisions.Add(new ChiefHiringPolicyRevision
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, InstallationId = installationId,
            Revision = row.Revision, SettingsJson = row.SettingsJson, EffectiveAt = row.UpdatedAt
        });
        await QueuePolicyMemoryAsync(row, owner.Id, token);
        await db.SaveChangesAsync(token);
        await audit.WriteAsync("hiring.policy.changed", nameof(ChiefHiringPolicy), row.Id,
            $"Owner set hiring policy revision {row.Revision}: {settings.Mode}, {settings.AutomaticLevel}, {settings.Publishers}.", cancellationToken: token);
        return await ReadPolicyAsync(organizationId, installationId, token);
    }

    private async Task QueuePolicyMemoryAsync(ChiefHiringPolicy policy, Guid ownerId, CancellationToken token)
    {
        var chief = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.OrganizationId == policy.OrganizationId && x.AgentInstallationId == policy.InstallationId && x.IsActive, token);
        var conversation = await db.CoreConversations.AsNoTracking().FirstOrDefaultAsync(x =>
            x.OrganizationId == policy.OrganizationId && x.AgentOrganizationUserId == chief.Id &&
            x.InitiatedByOrganizationUserId == ownerId && x.ArchivedAt == null, token);
        if (conversation is null)
        {
            conversation = new Conversation { Id = Guid.NewGuid(), OrganizationId = policy.OrganizationId,
                AgentOrganizationUserId = chief.Id, InitiatedByOrganizationUserId = ownerId,
                Kind = ConversationKind.DirectHumanAgent, IsPrivate = true,
                CreatedAt = policy.UpdatedAt, UpdatedAt = policy.UpdatedAt,
                Participants = [
                    new ConversationParticipant { Id = Guid.NewGuid(), OrganizationUserId = ownerId, JoinedAt = policy.UpdatedAt },
                    new ConversationParticipant { Id = Guid.NewGuid(), OrganizationUserId = chief.Id, JoinedAt = policy.UpdatedAt }]
            };
            db.CoreConversations.Add(conversation);
        }
        var settings = Policy(policy.SettingsJson);
        var modeLabel = settings.Mode switch { HiringSelectionMode.Automatic => "Automatic hiring",
            HiringSelectionMode.ChooseCandidates => "Choose candidates yourself", _ => "Recommend candidates" };
        var sourceLabel = settings.Publishers switch { HiringPublisherPreference.FirstPartyOnly => "First-party only",
            HiringPublisherPreference.BestFit => "Best fit regardless of publisher", _ => "Prefer first-party" };
        var authority = settings.Mode != HiringSelectionMode.Automatic ? "Each hire requires your confirmation."
            : settings.AutomaticLevel switch {
                AutomaticHiringLevel.ApprovedPackages => "Use packages and grants already approved in this business.",
                AutomaticHiringLevel.BroadDelegation => "Use eligible packages and their requested grants for approved roles, within platform restrictions.",
                _ => $"Limits: {settings.MaximumHireCost} {settings.Currency} per hire; {settings.MaximumTotalCost} {settings.Currency} total. Allowed capabilities: {string.Join(", ", settings.AllowedCapabilities ?? [])}. Allowed network access: {string.Join(", ", settings.AllowedNetworkAccess ?? [])}." };
        var messageId = Guid.NewGuid();
        db.CoreConversationMessages.Add(new ConversationMessage
        {
            Id = messageId, ConversationId = conversation.Id, Role = ConversationRole.Assistant,
            SenderOrganizationUserId = chief.Id, SourceProvider = "HiringPolicy", CreatedAt = policy.UpdatedAt,
            CorrelationId = policy.Id, CausationId = policy.SourceDecisionId,
            IdempotencyKey = $"hiring-policy-memory:{policy.InstallationId:N}:{policy.Revision}",
            Content = $"Your hiring preferences are saved: {modeLabel}; {sourceLabel}. {authority} Reason: {policy.Rationale}. These preferences apply to future approved plans. I will check the current platform policy before every automatic hire."
        });
        db.MemoryCaptureOutbox.Add(new MemoryCaptureOutboxItem
        {
            Id = Guid.NewGuid(), ConversationMessageId = messageId, Status = MemoryCaptureStatus.Pending,
            CreatedAt = policy.UpdatedAt, NextAttemptAt = policy.UpdatedAt
        });
    }

    public async Task<HiringPolicyResponse> CaptureDecisionAsync(Guid organizationId, Guid installationId,
        CaptureHiringPolicyDecisionRequest request, CancellationToken token = default)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(token) : null;
        await LockHiringOrganizationAsync(organizationId, token);
        var result = await CaptureDecisionCoreAsync(organizationId, installationId, request, token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return result;
    }

    private async Task<HiringPolicyResponse> CaptureDecisionCoreAsync(Guid organizationId, Guid installationId,
        CaptureHiringPolicyDecisionRequest request, CancellationToken token)
    {
        var current = await ReadPolicyAsync(organizationId, installationId, token);
        if (request.DecisionId == Guid.Empty && (!request.AnswerTurnId.HasValue || request.AnswerTurnId == Guid.Empty))
            throw new ArgumentException("A decision ID or authenticated answer turn is required.");
        var decision = await db.ExecutiveDecisions.AsNoTracking().SingleOrDefaultAsync(x =>
            (x.Id == request.DecisionId || request.DecisionId == Guid.Empty && x.NextChatTurnId == request.AnswerTurnId) && x.OrganizationId == organizationId && x.RequestingInstallationId == installationId &&
            x.Status == ExecutiveDecisionStatus.Answered, token)
            ?? throw new UnauthorizedAccessException("An answered owner decision is required.");
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == decision.AnsweredByOrganizationUserId && x.OrganizationId == organizationId && x.IsActive &&
            x.PermissionLevel == OrganizationPermissionLevel.Owner && x.EmployeeType == EmployeeType.Human, token);
        if (owner?.ApplicationUserId is null) throw new UnauthorizedAccessException("Only the owner can delegate hiring.");
        if (current.SourceDecisionId == decision.Id) return current;
        if (current.Revision != request.ExpectedRevision) throw new InvalidOperationException("Hiring preferences changed.");
        static bool MatchesDecisionKey(string key, string prefix) => key == prefix || key.StartsWith(prefix + ":", StringComparison.Ordinal);
        if (current.SetupComplete && MatchesDecisionKey(decision.IdempotencyKey, $"hiring-policy:{installationId:N}:mode:{current.Revision}"))
            current = current with { SetupStage = "mode" };
        var prefix = $"hiring-policy:{installationId:N}:{current.SetupStage}:{current.Revision}";
        if (!MatchesDecisionKey(decision.IdempotencyKey, prefix))
            throw new ArgumentException("This decision is not the current hiring setup question and revision.");
        var value = decision.SelectedOptionId;
        var ownerText = (decision.FreeTextAnswer ?? "").Trim().ToLowerInvariant();
        var explicitPublisher = ownerText.Contains("first-party only", StringComparison.Ordinal) ? HiringPublisherPreference.FirstPartyOnly
            : ownerText.Contains("prefer first-party", StringComparison.Ordinal) ? HiringPublisherPreference.PreferFirstParty
            : ownerText.Contains("regardless of publisher", StringComparison.Ordinal) ? HiringPublisherPreference.BestFit : (HiringPublisherPreference?)null;
        // Interpret explicit owner phrases. Ambiguous authority still requires another question.
        if (value is null && decision.FreeTextAnswer is { } text)
        {
            value = text.Trim().ToLowerInvariant() switch
            {
                "recommend candidates" => "recommend", "choose candidates yourself" => "choose",
                "automatic hiring" => "automatic", "approved packages" => "approved",
                "within limits" => "bounded", "broad delegation" => "broad",
                "first-party only" => "first", "prefer first-party" => "prefer", "best fit" => "best",
                "skip" or "skip for now" or "keep recommending" => "skip", _ => null
            };
            if (value is null && current.SetupStage == "mode")
                value = ownerText.Contains("recommend candidates", StringComparison.Ordinal) || ownerText == "recommend agents" || ownerText == "i want to review each hire" ? "recommend"
                    : ownerText.Contains("choose candidates yourself", StringComparison.Ordinal) || ownerText == "i'll choose the agents" ? "choose"
                    : ownerText.Contains("hire automatically", StringComparison.Ordinal) || ownerText.Contains("automatic hiring", StringComparison.Ordinal) || ownerText.Contains("automatically hire", StringComparison.Ordinal) ? "automatic" : null;
            if (value is null && current.SetupStage == "publishers" && explicitPublisher is { } source)
                value = source switch { HiringPublisherPreference.FirstPartyOnly => "first", HiringPublisherPreference.PreferFirstParty => "prefer", _ => "best" };
        }
        if (decision.SelectedOptionId is not null || current.SetupStage == "level" && value == "broad")
            ValidateOwnerPolicyOption(decision, current.SetupStage, value);
        var settings = explicitPublisher is { } publisher ? current.Settings with { Publishers = publisher } : current.Settings;
        var stage = current.SetupStage;
        if (value == "skip") { settings = settings with { Mode = HiringSelectionMode.RecommendCandidates }; stage = "complete"; }
        else if (stage == "mode")
        {
            settings = settings with { Mode = value switch {
                "recommend" => HiringSelectionMode.RecommendCandidates, "choose" => HiringSelectionMode.ChooseCandidates,
                "automatic" => HiringSelectionMode.Automatic, _ => throw new ArgumentException("Please clarify which hiring mode you want.") } };
            stage = value == "automatic" ? "level" : "publishers";
        }
        else if (stage == "level")
        {
            settings = settings with { AutomaticLevel = value switch {
                "approved" => AutomaticHiringLevel.ApprovedPackages, "bounded" => AutomaticHiringLevel.WithinLimits,
                "broad" => AutomaticHiringLevel.BroadDelegation, _ => throw new ArgumentException("Please choose the degree of automatic hiring authority.") } };
            stage = value == "bounded" ? "costs" : "publishers";
        }
        else if (stage == "costs")
        {
            if (value == "free") settings = settings with { MaximumHireCost = 0, MaximumTotalCost = 0, Currency = "USD" };
            else
            {
                var match = System.Text.RegularExpressions.Regex.Match(decision.FreeTextAnswer ?? "",
                    @"^\s*(?<hire>\d+(?:\.\d{1,2})?)\s+(?<currency>[A-Za-z]{3})\s+per hire\s*(?:,|and|;)\s*(?<total>\d+(?:\.\d{1,2})?)\s+(?:(?<totalCurrency>[A-Za-z]{3})\s+)?total\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!match.Success || match.Groups["totalCurrency"].Success && !string.Equals(match.Groups["currency"].Value, match.Groups["totalCurrency"].Value, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Please specify the maximum per-hire and total costs, for example: 10 USD per hire, 100 USD total.");
                settings = settings with { MaximumHireCost = decimal.Parse(match.Groups["hire"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    MaximumTotalCost = decimal.Parse(match.Groups["total"].Value, System.Globalization.CultureInfo.InvariantCulture), Currency = match.Groups["currency"].Value.ToUpperInvariant() };
            }
            stage = "permissions";
        }
        else if (stage == "permissions")
        {
            if (value == "none") settings = settings with { AllowedCapabilities = [], AllowedNetworkAccess = [] };
            else if (value == "existing")
            {
                var installs = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant)
                    .Where(x => x.BusinessId == organizationId.ToString("D") && x.IsEnabled).ToListAsync(token);
                settings = settings with { AllowedCapabilities = installs.SelectMany(x => ReadStrings(x.Grant?.RequiredCapabilitiesJson)).Distinct(StringComparer.Ordinal).ToArray(),
                    AllowedNetworkAccess = installs.SelectMany(x => ReadStrings(x.Grant?.NetworkAccessJson)).Distinct(StringComparer.Ordinal).ToArray() };
            }
            else throw new ArgumentException("Choose a permission boundary or set exact permissions in Chief settings.");
            stage = "publishers";
        }
        else if (stage == "publishers")
        {
            settings = settings with { Publishers = value switch {
                "first" => HiringPublisherPreference.FirstPartyOnly, "prefer" => HiringPublisherPreference.PreferFirstParty,
                "best" => HiringPublisherPreference.BestFit, _ => throw new ArgumentException("Please clarify your publisher preference.") } };
            stage = "complete";
        }
        else throw new ArgumentException("Use Chief settings to specify explicit hiring limits.");
        // Carry an explicit publisher answer forward across the relevant authority questions.
        if (explicitPublisher.HasValue || current.SetupComplete || current.Rationale?.Contains("Publisher preference supplied", StringComparison.Ordinal) == true)
            if (stage == "publishers") stage = "complete";
        if (stage == "complete") return await UpdatePolicyAsync(organizationId, installationId,
            owner.ApplicationUserId.Value, settings, current.Revision, decision.FreeTextAnswer ?? decision.Prompt,
            decision.Id, token);
        var row = await db.ChiefHiringPolicies.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.InstallationId == installationId, token)
            ?? new ChiefHiringPolicy { Id = Guid.NewGuid(), OrganizationId = organizationId, InstallationId = installationId, Revision = 0 };
        if (db.Entry(row).State == EntityState.Detached) db.ChiefHiringPolicies.Add(row);
        row.SetupComplete = false;
        if (explicitPublisher.HasValue || current.SetupComplete)
            row.Rationale = "Publisher preference supplied by owner; preserve during setup.";
        row.Revision++; row.SettingsJson = JsonSerializer.Serialize(settings, JsonOptions); row.SetupStage = stage;
        row.SourceDecisionId = decision.Id; row.OwnerId = owner.Id; row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        return await ReadPolicyAsync(organizationId, installationId, token);
    }

    private static void ValidateOwnerPolicyOption(ExecutiveDecision decision, string stage, string? value)
    {
        var expectedLabel = (stage, value) switch {
            ("mode", "recommend") => "Recommend candidates", ("mode", "choose") => "Choose candidates yourself",
            ("mode", "automatic") => "Automatic hiring", ("level", "approved") => "Approved packages",
            ("level", "bounded") => "Within limits", ("level", "broad") => "Broad delegation",
            ("publishers", "prefer") => "Prefer first-party", ("publishers", "first") => "First-party only",
            ("publishers", "best") => "Best fit", ("costs", "free") => "Free agents only",
            ("permissions", "none") => "No capabilities or network access",
            ("permissions", "existing") => "Permissions already approved here",
            (_, "skip") => "Keep recommending", _ => throw new ArgumentException("Unknown owner hiring decision option.") };
        using var document = JsonDocument.Parse(decision.OptionsJson);
        var options = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement
            : document.RootElement.GetProperty("options");
        var option = options.Deserialize<List<HiringOwnerOption>>(JsonOptions)?.SingleOrDefault(x => x.Id == value);
        if (option?.Label != expectedLabel || value == "broad" &&
            !(option.Description?.Contains("grant their requested permissions", StringComparison.OrdinalIgnoreCase) ?? false))
            throw new ArgumentException("The owner must receive the explicit hiring authority description before delegation is captured.");
    }

    private sealed record HiringOwnerOption(string Id, string Label, string? Description);

    private async Task<WorkforcePlan> RequireRecommendationAsync(Guid organizationId, Guid installationId, Guid recommendationId, CancellationToken token)
    {
        await RequireChiefAsync(organizationId, installationId, token);
        return await db.WorkforcePlans.SingleOrDefaultAsync(x => x.Id == recommendationId &&
            x.OrganizationId == organizationId && x.RequestingInstallationId == installationId &&
            x.Status == ProposalStatus.Pending && x.FulfilledHeadcount < x.Headcount, token)
            ?? throw new ArgumentException("The recommendation is unavailable, withdrawn, or fulfilled.");
    }

    public async Task<HiringCandidateSelectionResponse> SelectCandidateAsync(Guid organizationId, Guid installationId,
        Guid recommendationId, CancellationToken token = default)
    {
        var plan = await RequireRecommendationAsync(organizationId, installationId, recommendationId, token);
        var policy = await ReadPolicyAsync(organizationId, installationId, token);
        var mode = policy.SetupComplete ? policy.Settings.Mode : HiringSelectionMode.RecommendCandidates;
        if (mode == HiringSelectionMode.ChooseCandidates)
        {
            plan.SelectedCatalogAgentJson = null;
            plan.SelectionRationale = "Choose the candidate for this approved role.";
            await db.SaveChangesAsync(token);
            return new(plan.Id, mode, null, plan.SelectionRationale);
        }
        var context = await GetCandidateSearchContextAsync(organizationId, plan.Id, token);
        var approvedRole = plan.SourceResourceChangeRequestId.HasValue
            ? await db.ResourceChangeRoles.AsNoTracking().SingleOrDefaultAsync(x => x.ResourceChangeRequestId == plan.SourceResourceChangeRequestId && x.RoleKey == plan.RoleKey && x.IsDesired, token) : null;
        if (approvedRole?.HumanRequired == true)
        {
            plan.SelectedCatalogAgentJson = null;
            plan.SelectionRationale = "This approved role requires a human.";
            await db.SaveChangesAsync(token);
            return new(plan.Id, mode, null, plan.SelectionRationale, "Choose human coverage for this role.");
        }
        var candidates = await (agentCatalog ?? throw new InvalidOperationException("Agent catalog unavailable."))
            .GetAvailableAgentsAsync(organizationId, new AvailableAgentSearchQuery(
                Role: context?.RoleCategoryKey is null ? plan.Title : null, Limit: 100,
                RequiredCapabilities: ReadStrings(approvedRole?.RequiredCapabilitiesJson),
                RoleCategoryKey: context?.RoleCategoryKey, PreferredSpecializationKeys: context?.PreferredSpecializationKeys), token);
        var eligible = candidates.Agents.Where(x => x.Availability is AgentAvailabilityState.AvailableToInstall or AgentAvailabilityState.InstalledEnabled);
        // Only the platform's embedded catalog establishes first-party provenance.
        if (policy.Settings.Publishers == HiringPublisherPreference.FirstPartyOnly)
            eligible = eligible.Where(IsFirstParty);
        var selected = eligible.OrderByDescending(x => policy.Settings.Publishers == HiringPublisherPreference.PreferFirstParty && IsFirstParty(x))
            .ThenByDescending(x => x.Score).ThenBy(x => x.AgentReference, StringComparer.Ordinal).FirstOrDefault();
        var rationale = selected is null ? "No eligible candidate is available under your publisher preference." :
            $"{selected.Name} matches the approved {plan.Title} role. Publisher: {selected.Publisher}. " +
            (IsFirstParty(selected) ? "From the platform first-party catalog." : "Third-party package; review its permissions before hiring.");
        plan.SelectedCatalogAgentJson = selected is null ? null : JsonSerializer.Serialize(selected, JsonOptions);
        plan.SelectionRationale = rationale;
        await db.SaveChangesAsync(token);
        return new(plan.Id, mode, selected, rationale, selected is null ? rationale : null);
    }

    private static bool IsFirstParty(AvailableAgent agent) => agent.Source == AgentCatalogSource.FirstPartyCatalog ||
        agent.AlternateSources.Contains(AgentCatalogSource.FirstPartyCatalog);

    public async Task ApplyToPlanAsync(Guid organizationId, Guid installationId, Guid requestId,
        Guid applicationUserId, CancellationToken token = default)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(token) : null;
        await LockHiringOrganizationAsync(organizationId, token);
        await ApplyToPlanCoreAsync(organizationId, installationId, requestId, applicationUserId, token);
        if (transaction is not null) await transaction.CommitAsync(token);
    }

    private async Task ApplyToPlanCoreAsync(Guid organizationId, Guid installationId, Guid requestId,
        Guid applicationUserId, CancellationToken token)
    {
        _ = await RequireOwnerAsync(organizationId, applicationUserId, token);
        var policy = await ReadPolicyAsync(organizationId, installationId, token);
        if (!policy.SetupComplete) throw new InvalidOperationException("Finish hiring preferences first.");
        if (!await db.ResourceChangeRequests.AnyAsync(x => x.Id == requestId && x.OrganizationId == organizationId &&
                x.Status == ResourceChangeRequestStatus.Approved, token)) throw new ArgumentException("Approved plan unavailable.");
        var binding = await db.HiringPlanDelegations.SingleOrDefaultAsync(x =>
            x.ResourceChangeRequestId == requestId && x.InstallationId == installationId, token);
        binding ??= new HiringPlanDelegation { Id = Guid.NewGuid(), OrganizationId = organizationId,
            InstallationId = installationId, ResourceChangeRequestId = requestId };
        if (db.Entry(binding).State == EntityState.Detached) db.HiringPlanDelegations.Add(binding);
        binding.SettingsJson = JsonSerializer.Serialize(policy.Settings, JsonOptions); binding.CreatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        await audit.WriteAsync("hiring.policy.applied-to-plan", nameof(HiringPlanDelegation), binding.Id,
            $"Owner applied hiring policy revision {policy.Revision} to approved plan {requestId:D}.", cancellationToken: token);
        var recommendations = await db.WorkforcePlans.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
            x.RequestingInstallationId == installationId && x.SourceResourceChangeRequestId == requestId &&
            x.Status == ProposalStatus.Pending && x.FulfilledHeadcount < x.Headcount).Select(x => x.Id).Take(100).ToListAsync(token);
        foreach (var recommendation in recommendations)
        {
            if (policy.Settings.Mode == HiringSelectionMode.Automatic)
                _ = await SubmitDelegatedAsync(organizationId, installationId, recommendation, token);
            else _ = await SelectCandidateAsync(organizationId, installationId, recommendation, token);
        }
    }

    private async Task<HiringPolicySettings> BoundPolicyAsync(WorkforcePlan plan, CancellationToken token)
    {
        if (plan.SourceResourceChangeRequestId is not Guid requestId) return new();
        var existing = await db.HiringPlanDelegations.SingleOrDefaultAsync(x =>
            x.ResourceChangeRequestId == requestId && x.InstallationId == plan.RequestingInstallationId, token);
        if (existing is not null) return Policy(existing.SettingsJson);
        var approved = await db.ResourceChangeRequests.AsNoTracking().SingleAsync(x => x.Id == requestId &&
            x.OrganizationId == plan.OrganizationId && x.Status == ResourceChangeRequestStatus.Approved, token);
        var revision = approved.DecidedAt is { } at ? await db.ChiefHiringPolicyRevisions.AsNoTracking()
            .Where(x => x.OrganizationId == plan.OrganizationId && x.InstallationId == plan.RequestingInstallationId && x.EffectiveAt <= at)
            .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(token) : null;
        var settings = revision is null ? new HiringPolicySettings() : Policy(revision.SettingsJson);
        db.HiringPlanDelegations.Add(new HiringPlanDelegation { Id = Guid.NewGuid(), OrganizationId = plan.OrganizationId,
            InstallationId = plan.RequestingInstallationId, ResourceChangeRequestId = requestId,
            SettingsJson = JsonSerializer.Serialize(settings, JsonOptions), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(token);
        return settings;
    }

    public async Task<DelegatedHireResponse> SubmitDelegatedAsync(Guid organizationId, Guid installationId,
        Guid recommendationId, CancellationToken token = default)
    {
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(token) : null;
        await LockHiringOrganizationAsync(organizationId, token);
        await RequireChiefAsync(organizationId, installationId, token);
        var completedPlan = await db.WorkforcePlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == recommendationId &&
            x.OrganizationId == organizationId && x.RequestingInstallationId == installationId &&
            x.Status == ProposalStatus.Approved && x.FulfilledHeadcount >= x.Headcount, token);
        if (completedPlan is not null)
        {
            if (transaction is not null) await transaction.CommitAsync(token);
            return new(recommendationId, null, "Succeeded", "The approved role is already filled.");
        }
        DelegatedHireResponse result;
        do
        {
            result = await SubmitDelegatedCoreAsync(organizationId, installationId, recommendationId, token);
        } while (result.Status == "Succeeded" && await db.WorkforcePlans.AnyAsync(x => x.Id == recommendationId &&
            x.OrganizationId == organizationId && x.Status == ProposalStatus.Pending && x.FulfilledHeadcount < x.Headcount, token));
        if (transaction is not null) await transaction.CommitAsync(token);
        return result;
    }

    private async Task<DelegatedHireResponse> SubmitDelegatedCoreAsync(Guid organizationId, Guid installationId,
        Guid recommendationId, CancellationToken token = default)
    {
        var plan = await RequireRecommendationAsync(organizationId, installationId, recommendationId, token);
        var selection = await SelectCandidateAsync(organizationId, installationId, recommendationId, token);
        if (selection.Candidate is not { } candidate) return new(plan.Id, null, "NeedsReview", selection.Rationale);
        var current = await ReadPolicyAsync(organizationId, installationId, token);
        var bound = await BoundPolicyAsync(plan, token);
        if (!current.SetupComplete || current.Settings.Mode != HiringSelectionMode.Automatic || bound.Mode != HiringSelectionMode.Automatic)
            return new(plan.Id, null, "NeedsReview", "This plan has no automatic hiring delegation.");
        var owner = await db.CoreOrganizationUsers.AsNoTracking().SingleAsync(x => x.Id == current.OwnerId &&
            x.OrganizationId == organizationId && x.IsActive && x.PermissionLevel == OrganizationPermissionLevel.Owner && x.ApplicationUserId.HasValue, token);
        var preview = await PreviewMarketplaceHireAsync(organizationId, owner.ApplicationUserId!.Value,
            new PreviewMarketplaceHireRequest(candidate.AgentReference, plan.Title, candidate.Name, null,
                $"delegated-hire:{plan.Id:N}:{plan.FulfilledHeadcount}") { RecommendationId = plan.Id, TeamId = plan.TeamId }, token);
        var workflow = await db.StaffingActionProposals.SingleAsync(x => x.Id == preview.WorkflowId, token);
        workflow.DelegatedInstallationId = installationId;
        await db.SaveChangesAsync(token);
        try
        {
            await ValidateDelegationAsync(workflow, token);
            _executingDelegation = true;
            var operation = await StartAsync(organizationId, workflow.Id, owner.ApplicationUserId.Value,
                new ConfirmHiringWorkflowRequest($"delegated-confirm:{workflow.Id:N}"), token);
            return new(plan.Id, workflow.Id, operation?.Status ?? "NeedsReview", operation?.Error);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            plan.SelectionRationale = selection.Rationale + " Owner review needed: " + error.Message;
            workflow.DelegatedInstallationId = null;
            await db.SaveChangesAsync(token);
            return new(plan.Id, workflow.Id, "NeedsReview", error.Message);
        }
        finally { _executingDelegation = false; }
    }

    private async Task ValidateDelegationAsync(StaffingActionProposal workflow, CancellationToken token)
    {
        if (workflow.DelegatedInstallationId is not Guid installationId) return;
        var plan = await RequireRecommendationAsync(workflow.OrganizationId, installationId, workflow.WorkforcePlanId, token);
        var current = await ReadPolicyAsync(workflow.OrganizationId, installationId, token);
        var bound = await BoundPolicyAsync(plan, token);
        if (!current.SetupComplete || current.Settings.Mode != HiringSelectionMode.Automatic || bound.Mode != HiringSelectionMode.Automatic)
            throw new UnauthorizedAccessException("Automatic hiring authority has been revoked or was not granted for this plan.");
        var snapshot = JsonSerializer.Deserialize<WorkflowSnapshot>(workflow.PayloadJson, JsonOptions)!;
        var candidate = await db.WorkforceCandidates.AsNoTracking().SingleAsync(x => x.Id == ParseCandidateReference(workflow.CandidateId), token);
        var catalogReference = ReadMetadata(candidate.ExplanationJson).CatalogReference;
        var agent = await agentCatalog!.ResolveAsync(workflow.OrganizationId, catalogReference ?? candidate.ExternalCandidateId, token)
            ?? throw new InvalidOperationException("The selected package is unavailable.");
        // Resolve returns the installed record, while discovery attaches platform catalog
        // provenance only for the same package repository and manifest identity.
        if (agent.Source == AgentCatalogSource.Installed)
        {
            var listings = await agentCatalog.GetAvailableAgentsAsync(workflow.OrganizationId,
                new AvailableAgentSearchQuery(Limit: 100, RoleCategoryKey: (await GetCandidateSearchContextAsync(workflow.OrganizationId, plan.Id, token))?.RoleCategoryKey), token);
            agent = listings.Agents.FirstOrDefault(x => x.AgentReference == agent.AgentReference) ?? agent;
        }
        if (agent.Source != AgentCatalogSource.Installed &&
            (agent.Price != snapshot.Price || !string.Equals(agent.Currency, snapshot.Currency, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The selected package cost changed; owner review is required.");
        var embedded = snapshot.EmbeddedAgent;
        AgentInstallation? approvedInstallation = null;
        if (embedded is null && agent.Source == AgentCatalogSource.Installed && agent.InstallationId is Guid installedId)
        {
            approvedInstallation = await db.AgentInstallations.AsNoTracking().Include(x => x.Grant)
                .SingleOrDefaultAsync(x => x.Id == installedId && x.BusinessId == workflow.OrganizationId.ToString("D") &&
                    x.IsEnabled && x.RevisionStatus == PluginRevisionStatus.Active, token);
            if (approvedInstallation?.Grant is null || approvedInstallation.SetupState != PluginSetupState.Ready ||
                !SameGrantSet(snapshot.ApprovedGrants, ReadStrings(approvedInstallation.Grant.RequiredCapabilitiesJson)))
                throw new InvalidOperationException("The installed package needs setup or its approved permissions changed.");
        }
        if (embedded is null && approvedInstallation is null) throw new InvalidOperationException("Missing approved package snapshot.");
        var requestedCapabilities = embedded?.RequestedCapabilities ?? ReadStrings(approvedInstallation?.Grant?.RequiredCapabilitiesJson);
        var networkAccess = embedded?.NetworkAccess ?? ReadStrings(approvedInstallation?.Grant?.NetworkAccessJson);
        foreach (var policy in new[] { bound, current.Settings })
        {
            if (policy.Publishers == HiringPublisherPreference.FirstPartyOnly && !IsFirstParty(agent))
                throw new UnauthorizedAccessException("This policy permits first-party packages only.");
            if (policy.AutomaticLevel == AutomaticHiringLevel.ApprovedPackages && !(embedded?.DefinitionId.HasValue == true || approvedInstallation is not null))
                throw new UnauthorizedAccessException("This package has not previously been approved in this business.");
            if (policy.AutomaticLevel == AutomaticHiringLevel.WithinLimits)
            {
                if (policy.MaximumHireCost is null || policy.MaximumTotalCost is null || policy.AllowedCapabilities is null || policy.AllowedNetworkAccess is null ||
                    snapshot.Price is null || snapshot.Price > policy.MaximumHireCost ||
                    !string.Equals(snapshot.Currency, policy.Currency, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("The candidate cost is unknown or outside the delegated limits.");
                if (requestedCapabilities.Except(policy.AllowedCapabilities ?? [], StringComparer.Ordinal).Any() ||
                    networkAccess.Except(policy.AllowedNetworkAccess ?? [], StringComparer.Ordinal).Any())
                    throw new UnauthorizedAccessException("The package requests permissions outside the delegated limits.");
                var spent = await db.StaffingActionProposals.AsNoTracking().Where(x => x.OrganizationId == workflow.OrganizationId &&
                    x.Id != workflow.Id && (x.Status == ProposalStatus.Approved || x.Status == ProposalStatus.Pending && x.DelegatedInstallationId != null))
                    .Select(x => x.PayloadJson).ToListAsync(token);
                var total = spent.Select(x => JsonSerializer.Deserialize<WorkflowSnapshot>(x, JsonOptions)!)
                    .Where(x => string.Equals(x.Currency, policy.Currency, StringComparison.OrdinalIgnoreCase)).Sum(x => x.Price ?? 0);
                if (total + snapshot.Price > policy.MaximumTotalCost) throw new UnauthorizedAccessException("The total delegated hiring cost limit is exhausted.");
            }
            if (snapshot.Price is null) throw new UnauthorizedAccessException("Candidate cost is unknown; owner review is required.");
        }
        if (embedded?.ConfigurationFields.Any(x => x.Required && x.DefaultValue is null &&
                !(embedded.ConfigurationSettings?.ContainsKey(x.Key) ?? false)) == true)
            throw new InvalidOperationException("Required employee configuration needs owner input.");
    }
}
