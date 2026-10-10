using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Auth;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed partial class HiringServiceTests
{
    private static async Task<(Guid Organization, Guid Installation, Guid Owner, Guid Application, HiringService Service)> AutonomyFixtureAsync(CSweetDbContext db,
        AgentCatalogSource source = AgentCatalogSource.FirstPartyCatalog, decimal? price = 0, bool requireConfiguration = true, bool staffed = false)
    {
        var organization = Guid.NewGuid(); var installation = Guid.NewGuid(); var owner = Guid.NewGuid();
        var application = Guid.NewGuid(); var chief = Guid.NewGuid();
        if (db.Database.IsRelational())
        {
            db.Users.Add(new ApplicationUser { Id = application, UserName = "hiring-test-owner" });
            var chiefSource = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = "https://example.test/chief" };
            var chiefPackage = new AgentPackageVersion { Id = Guid.NewGuid(), PackageSource = chiefSource, AgentId = "chief", Version = "1.0.0", ManifestJson = "{}" };
            db.AgentInstallations.Add(new AgentInstallation { Id = installation, InstallationKey = installation,
                PackageVersion = chiefPackage, BusinessId = organization.ToString("D") });
        }
        db.CoreOrganizations.Add(new Organization { Id = organization, Name = "Test business" });
        db.CoreOrganizationUsers.AddRange(
            new OrganizationUser { Id = owner, OrganizationId = organization, ApplicationUserId = application,
                PermissionLevel = OrganizationPermissionLevel.Owner, EmployeeType = EmployeeType.Human, DisplayName = "Owner" },
            new OrganizationUser { Id = chief, OrganizationId = organization, AgentInstallationId = installation,
                ReportsToOrganizationUserId = owner, EmployeeType = EmployeeType.Agent, DisplayName = "Chief" });
        db.LeadershipAssignments.Add(new LeadershipAssignment { Id = Guid.NewGuid(), OrganizationId = organization,
            OrganizationUserId = chief, PositionKey = LeadershipPositionKeys.ChiefOfStaff });
        await db.SaveChangesAsync();
        var repository = "https://github.com/example/product-manager";
        var agent = new AvailableAgent("catalog:product-manager", "com.example.product-manager", source, [],
            AgentAvailabilityState.AvailableToInstall, null, "Product Manager", "Own product outcomes", "C-Sweet", "Product",
            ["Product Manager"], ["product"], ["product.strategy"], price, "USD", null, 0, null, repository, 1, "Self-declared publisher");
        var definitions = new RecordingDefinitionService();
        if (source == AgentCatalogSource.Installed)
        {
            var installedId = Guid.NewGuid();
            var package = new AgentPackageVersion { Id = Guid.Parse("7547f772-e46b-4918-a290-f4fba1f04457"),
                PackageSource = new AgentPackageSource { Id = Guid.NewGuid(), RepositoryUrl = repository }, AgentId = agent.AgentId!,
                AgentName = agent.Name, Version = "1.0.0", PublisherId = "example", PublisherName = agent.Publisher,
                ManifestDigest = new('b', 64), PackageDigest = new('c', 64), ArtifactSignature = "signed",
                Status = AgentPackageVersionStatus.Built, RuntimeType = "dotnet-project",
                ManifestJson = "{\"runtime\":{\"supportsMultipleInstallations\":true}}" };
            db.AgentDefinitions.Add(new AgentDefinition { Id = definitions.DefinitionId, PackageVersionId = package.Id, PackageSourceId = package.PackageSource!.Id,
                AgentId = package.AgentId!, IsAvailableForHire = true, Status = AgentDefinitionStatus.Available,
                DefaultRequiredCapabilitiesJson = "[]", DefaultProvidedCapabilitiesJson = "[\"product.strategy\"]" });
            db.AgentInstallations.Add(new AgentInstallation { Id = installedId, InstallationKey = installedId,
                PackageVersion = package, PackageVersionId = package.Id, AgentDefinitionId = definitions.DefinitionId,
                BusinessId = organization.ToString("D"), Grant = new AgentInstallationGrant { Id = Guid.NewGuid(),
                    AgentInstallationId = installedId, RequiredCapabilitiesJson = "[]", ProvidedCapabilitiesJson = "[\"product.strategy\"]" } });
            if (staffed) db.CoreOrganizationUsers.Add(new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organization,
                AgentInstallationId = installedId, EmployeeType = EmployeeType.Agent, DisplayName = "Original agent", ReportsToOrganizationUserId = owner });
            await db.SaveChangesAsync();
            definitions.SeedExisting(package.Id);
            agent = agent with { AgentReference = $"installed:{installedId:N}", InstallationId = installedId, Availability = AgentAvailabilityState.InstalledEnabled };
        }
        var service = new HiringService(db, new RecordingOrganizationUserService(db), new TestAuditEventWriter(),
            new AutonomyPreview(new RecordingImportPreview(db, repository), requireConfiguration), new RecordingInstallationService(organization),
            new RecordingAgentCatalog(agent), definitions);
        return (organization, installation, owner, application, service);
    }

    private sealed class AutonomyPreview(RecordingImportPreview inner, bool requireConfiguration) : CSweet.Application.Setup.IAgentImportPreviewService
    {
        public async Task<CSweet.Contracts.Agents.AgentImportPreviewResponse> PreviewAsync(CSweet.Contracts.Agents.PreviewAgentImportRequest request, CancellationToken token = default)
        {
            var preview = await inner.PreviewAsync(request, token);
            return requireConfiguration ? preview : preview with { ConfigurationFields = [] };
        }
    }

    private static async Task<WorkforcePlan> AutonomyPlanAsync(CSweetDbContext db, Guid organization, Guid installation, Guid owner, int headcount = 1)
    {
        var request = new ResourceChangeRequestRecord { Id = Guid.NewGuid(), OrganizationId = organization,
            Status = ResourceChangeRequestStatus.Approved, DecidedAt = DateTimeOffset.UtcNow, ManagerOrganizationUserId = owner,
            Roles = [new ResourceChangeRoleRecord { Id = Guid.NewGuid(), RoleKey = "product-manager", Title = "Product Manager",
                Headcount = headcount, IsDesired = true, ReportsToOrganizationUserId = owner }] };
        var plan = new WorkforcePlan { Id = Guid.NewGuid(), OrganizationId = organization, RequestingInstallationId = installation,
            Title = "Product Manager", Objective = "Own product", RoleKey = "product-manager", Headcount = headcount,
            SourceResourceChangeRequestId = request.Id, IdempotencyKey = Guid.NewGuid().ToString() };
        if (db.Database.IsRelational())
        {
            var chief = await db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == installation);
            var conversation = await db.CoreConversations.FirstAsync(x => x.AgentOrganizationUserId == chief.Id);
            var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = conversation.Id,
                Role = ConversationRole.User, SenderOrganizationUserId = owner, Content = "Approve this hiring plan", CreatedAt = DateTimeOffset.UtcNow };
            var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = organization, ConversationId = conversation.Id,
                TargetAgentOrganizationUserId = chief.Id, UserMessageId = message.Id, Status = ChatTurnStatus.Completed };
            db.CoreConversationMessages.Add(message); db.ChatTurns.Add(turn);
            request.RequesterOrganizationUserId = chief.Id; request.RequesterInstallationId = installation;
            request.ConversationId = conversation.Id; request.ConversationMessageId = message.Id; request.ChatTurnId = turn.Id;
            request.IdempotencyKey = Guid.NewGuid().ToString("N");
        }
        db.ResourceChangeRequests.Add(request); db.WorkforcePlans.Add(plan);
        await db.SaveChangesAsync(); return plan;
    }

    [Fact]
    public async Task HiringPolicy_DefaultsToRecommendationsAndRejectsForeignChief()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db);
        var policy = await f.Service.ReadPolicyAsync(f.Organization, f.Installation);
        Assert.Equal(HiringSelectionMode.RecommendCandidates, policy.Settings.Mode);
        Assert.Equal(HiringPublisherPreference.PreferFirstParty, policy.Settings.Publishers);
        Assert.False(policy.SetupComplete); Assert.Equal(0, policy.Revision);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReadPolicyAsync(Guid.NewGuid(), f.Installation));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.UpdatePolicyAsync(f.Organization, f.Installation,
            Guid.NewGuid(), new(), 0, "No authority"));
    }

    [Fact]
    public async Task HiringPolicy_RevisionConflictsAndIncompleteLimitsDoNotGrantAuthority()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.UpdatePolicyAsync(f.Organization, f.Installation,
            f.Application, new(HiringSelectionMode.Automatic, AutomaticHiringLevel.WithinLimits), 0, "Missing limits"));
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application, new(), 0, "Recommend candidates");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.UpdatePolicyAsync(f.Organization, f.Installation,
            f.Application, new(HiringSelectionMode.Automatic), 0, "Stale revision"));
        Assert.Single(await db.ChiefHiringPolicyRevisions.ToListAsync());
    }

    [Fact]
    public async Task HiringPolicy_DecisionCaptureRequiresAnImmutableOwnerAnswerAndIsReplaySafe()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db);
        var turn = Guid.NewGuid(); var decision = new ExecutiveDecision { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            RequestingInstallationId = f.Installation, Status = ExecutiveDecisionStatus.Answered,
            AnsweredByOrganizationUserId = f.Owner, SelectedOptionId = "automatic", OptionsJson = "[{\"id\":\"automatic\",\"label\":\"Automatic hiring\",\"description\":\"Choose the authority next\"}]", NextChatTurnId = turn,
            IdempotencyKey = $"hiring-policy:{f.Installation:N}:mode:0", Prompt = "How should I help with hiring?" };
        db.ExecutiveDecisions.Add(decision); await db.SaveChangesAsync();
        var captured = await f.Service.CaptureDecisionAsync(f.Organization, f.Installation, new(Guid.Empty, 0, turn));
        Assert.False(captured.SetupComplete); Assert.Equal("level", captured.SetupStage);
        Assert.Equal(HiringSelectionMode.RecommendCandidates, (await f.Service.SelectCandidateAsync(f.Organization, f.Installation,
            (await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner)).Id)).Mode);
        var replay = await f.Service.CaptureDecisionAsync(f.Organization, f.Installation, new(decision.Id, 0));
        Assert.Equal(captured.Revision, replay.Revision);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.CaptureDecisionAsync(f.Organization, f.Installation, new(Guid.Empty, captured.Revision)));
    }

    [Fact]
    public async Task HiringPolicy_AmbiguousAutomaticAnswerDoesNotIncreaseAuthority()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db);
        var decision = new ExecutiveDecision { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            RequestingInstallationId = f.Installation, Status = ExecutiveDecisionStatus.Answered,
            AnsweredByOrganizationUserId = f.Owner, FreeTextAnswer = "just hire automatically",
            IdempotencyKey = $"hiring-policy:{f.Installation:N}:mode:0" };
        db.ExecutiveDecisions.Add(decision); await db.SaveChangesAsync();
        var draft = await f.Service.CaptureDecisionAsync(f.Organization, f.Installation, new(decision.Id, 0));
        Assert.Equal("level", draft.SetupStage);
        Assert.False(draft.SetupComplete);
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        Assert.Equal(HiringSelectionMode.RecommendCandidates, (await f.Service.SelectCandidateAsync(f.Organization, f.Installation, plan.Id)).Mode);
    }

    [Theory]
    [InlineData("Recommend candidates and prefer first-party", HiringPublisherPreference.PreferFirstParty)]
    [InlineData("Recommend candidates, first-party only", HiringPublisherPreference.FirstPartyOnly)]
    [InlineData("Recommend candidates regardless of publisher", HiringPublisherPreference.BestFit)]
    public async Task HiringPolicy_ExplicitPublisherAnswerAvoidsRedundantQuestion(string answer, HiringPublisherPreference expected)
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db);
        var decision = new ExecutiveDecision { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            RequestingInstallationId = f.Installation, Status = ExecutiveDecisionStatus.Answered,
            AnsweredByOrganizationUserId = f.Owner, FreeTextAnswer = answer,
            IdempotencyKey = $"hiring-policy:{f.Installation:N}:mode:0" };
        db.ExecutiveDecisions.Add(decision); await db.SaveChangesAsync();
        var saved = await f.Service.CaptureDecisionAsync(f.Organization, f.Installation, new(decision.Id, 0));
        Assert.True(saved.SetupComplete);
        Assert.Equal(expected, saved.Settings.Publishers);
        Assert.Single(await db.MemoryCaptureOutbox.ToListAsync());
    }

    [Fact]
    public async Task HiringPolicy_OldAnsweredDecisionCannotRestoreRevokedAuthority()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db);
        var decision = new ExecutiveDecision { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            RequestingInstallationId = f.Installation, Status = ExecutiveDecisionStatus.Answered,
            AnsweredByOrganizationUserId = f.Owner, SelectedOptionId = "automatic", OptionsJson = "[{\"id\":\"automatic\",\"label\":\"Automatic hiring\",\"description\":\"Choose the authority next\"}]",
            IdempotencyKey = $"hiring-policy:{f.Installation:N}:mode:0" };
        db.ExecutiveDecisions.Add(decision); await db.SaveChangesAsync();
        await f.Service.CaptureDecisionAsync(f.Organization, f.Installation, new(decision.Id, 0));
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application, new(), 1, "Revoke automatic hiring");
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.CaptureDecisionAsync(f.Organization, f.Installation, new(decision.Id, 2)));
        Assert.Equal(HiringSelectionMode.RecommendCandidates, (await f.Service.ReadPolicyAsync(f.Organization, f.Installation)).Settings.Mode);
    }

    [Fact]
    public async Task HiringPolicy_DisguisedOwnerDecisionCannotGrantAuthority()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db);
        var decision = new ExecutiveDecision { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            RequestingInstallationId = f.Installation, Status = ExecutiveDecisionStatus.Answered,
            AnsweredByOrganizationUserId = f.Owner, SelectedOptionId = "automatic",
            OptionsJson = "[{\"id\":\"automatic\",\"label\":\"Recommend candidates\"}]",
            IdempotencyKey = $"hiring-policy:{f.Installation:N}:mode:0" };
        db.ExecutiveDecisions.Add(decision); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.CaptureDecisionAsync(f.Organization, f.Installation, new(decision.Id, 0)));
        Assert.False((await f.Service.ReadPolicyAsync(f.Organization, f.Installation)).SetupComplete);
    }

    [Fact]
    public async Task HiringPolicy_ExplicitSkipResumesWithSafeDefaults()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db);
        var decision = new ExecutiveDecision { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            RequestingInstallationId = f.Installation, Status = ExecutiveDecisionStatus.Answered,
            AnsweredByOrganizationUserId = f.Owner, FreeTextAnswer = "skip for now",
            IdempotencyKey = $"hiring-policy:{f.Installation:N}:mode:0" };
        db.ExecutiveDecisions.Add(decision); await db.SaveChangesAsync();
        var saved = await f.Service.CaptureDecisionAsync(f.Organization, f.Installation, new(decision.Id, 0));
        Assert.True(saved.SetupComplete);
        Assert.Equal(new HiringPolicySettings(), saved.Settings);
    }

    [Fact]
    public async Task HiringPolicy_MirrorsTheSavedPolicyIntoDurableConversationMemory()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db);
        var chief = await db.CoreOrganizationUsers.SingleAsync(x => x.AgentInstallationId == f.Installation);
        db.CoreConversations.Add(new Conversation { Id = Guid.NewGuid(), OrganizationId = f.Organization,
            InitiatedByOrganizationUserId = f.Owner, AgentOrganizationUserId = chief.Id });
        await db.SaveChangesAsync();
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application, new(), 0, "I want to review each hire.");
        Assert.Contains("I want to review each hire", (await db.CoreConversationMessages.SingleAsync()).Content);
        Assert.Single(await db.MemoryCaptureOutbox.ToListAsync());
    }

    [Theory]
    [InlineData(HiringPublisherPreference.FirstPartyOnly, AgentCatalogSource.Marketplace, false)]
    [InlineData(HiringPublisherPreference.FirstPartyOnly, AgentCatalogSource.FirstPartyCatalog, true)]
    [InlineData(HiringPublisherPreference.PreferFirstParty, AgentCatalogSource.Marketplace, true)]
    [InlineData(HiringPublisherPreference.BestFit, AgentCatalogSource.Marketplace, true)]
    public async Task HiringPolicy_UsesPlatformProvenanceInsteadOfClaimedPublisher(HiringPublisherPreference publishers,
        AgentCatalogSource source, bool expected)
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db, source);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application, new(Publishers: publishers), 0, "Publisher preference");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        var selection = await f.Service.SelectCandidateAsync(f.Organization, f.Installation, plan.Id);
        Assert.Equal(expected, selection.Candidate is not null);
        Assert.Equal(expected, plan.SelectedCatalogAgentJson is not null);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SelectCandidateAsync(f.Organization, Guid.NewGuid(), plan.Id));
    }

    [Theory]
    [InlineData(HiringSelectionMode.ChooseCandidates, HiringPublisherPreference.FirstPartyOnly, false)]
    [InlineData(HiringSelectionMode.ChooseCandidates, HiringPublisherPreference.PreferFirstParty, false)]
    [InlineData(HiringSelectionMode.ChooseCandidates, HiringPublisherPreference.BestFit, false)]
    [InlineData(HiringSelectionMode.RecommendCandidates, HiringPublisherPreference.FirstPartyOnly, false)]
    [InlineData(HiringSelectionMode.RecommendCandidates, HiringPublisherPreference.PreferFirstParty, true)]
    [InlineData(HiringSelectionMode.RecommendCandidates, HiringPublisherPreference.BestFit, true)]
    [InlineData(HiringSelectionMode.Automatic, HiringPublisherPreference.FirstPartyOnly, false)]
    [InlineData(HiringSelectionMode.Automatic, HiringPublisherPreference.PreferFirstParty, true)]
    [InlineData(HiringSelectionMode.Automatic, HiringPublisherPreference.BestFit, true)]
    public async Task HiringPolicy_ModeAndPublisherMatrixNeverPromotesUntrustedPublisher(HiringSelectionMode mode,
        HiringPublisherPreference publishers, bool selected)
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db, AgentCatalogSource.Marketplace);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application, new(mode, Publishers: publishers), 0, "Owner selection");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        var result = await f.Service.SelectCandidateAsync(f.Organization, f.Installation, plan.Id);
        Assert.Equal(mode, result.Mode);
        Assert.Equal(selected, result.Candidate is not null);
        Assert.Equal(0, plan.FulfilledHeadcount);
    }

    [Fact]
    public async Task HiringPolicy_EnablingAutomationDoesNotAuthorizePreviouslyApprovedPlans()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db);
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Use broad delegation");
        var result = await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        Assert.Equal("NeedsReview", result.Status); Assert.Empty(await db.StaffingActionProposals.ToListAsync());
        Assert.Equal(HiringSelectionMode.RecommendCandidates,
            JsonSerializer.Deserialize<HiringPolicySettings>((await db.HiringPlanDelegations.SingleAsync()).SettingsJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Mode);
    }

    [Theory]
    [InlineData(AutomaticHiringLevel.ApprovedPackages)]
    [InlineData(AutomaticHiringLevel.WithinLimits)]
    public async Task HiringPolicy_RejectsUnapprovedPackagesOrPermissions(AutomaticHiringLevel level)
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, level, MaximumHireCost: 0, MaximumTotalCost: 0,
                Currency: "USD", AllowedCapabilities: [], AllowedNetworkAccess: []), 0, "Use restricted delegation");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        var result = await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        Assert.Equal("NeedsReview", result.Status); Assert.Equal(0, plan.FulfilledHeadcount);
        Assert.Null((await db.StaffingActionProposals.SingleAsync()).ResultOrganizationUserId);
    }

    [Fact]
    public async Task HiringPolicy_ChooseModeDoesNotSelectCandidateAndWithdrawnPlansRejectExecution()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.ChooseCandidates), 0, "I choose candidates");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        Assert.Null((await f.Service.SelectCandidateAsync(f.Organization, f.Installation, plan.Id)).Candidate);
        plan.Status = ProposalStatus.Cancelled; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id));
    }
    [Fact]
    public async Task HiringPolicy_BroadDelegationFillsApprovedSeatsAndDuplicateRequestsHaveNoAdditionalEffects()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db, requireConfiguration: false);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Use broad delegation");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner, headcount: 2);
        Assert.Equal("Succeeded", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id)).Status);
        Assert.Equal(2, plan.FulfilledHeadcount);
        Assert.Equal(2, await db.HiringRecommendationFulfillments.CountAsync());
        Assert.Equal("Succeeded", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id)).Status);
        Assert.Equal(2, await db.HiringRecommendationFulfillments.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiringPolicy_ApprovedPackagesReusesAnAlreadyApprovedVersionAndGrants(bool staffed)
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db, AgentCatalogSource.Installed, requireConfiguration: false, staffed: staffed);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.ApprovedPackages), 0, "Reuse approved packages");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        var result = await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        Assert.True(result.Status == "Succeeded", result.Message);
        Assert.Equal(1, plan.FulfilledHeadcount);
        Assert.Single(await db.AgentPackageVersions.ToListAsync());
    }

    [Fact]
    public async Task HiringPolicy_TotalLimitReservesCostAcrossMultipleHeadcountSlots()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db, price: 5, requireConfiguration: false);
        db.Budgets.Add(new Budget { Id = Guid.NewGuid(), OrganizationId = f.Organization, ScopeType = BudgetScopeType.Organization,
            Currency = "USD", LimitAmount = 100, IsActive = true, PeriodStart = DateTimeOffset.UtcNow.AddDays(-1), PeriodEnd = DateTimeOffset.UtcNow.AddDays(30) });
        await db.SaveChangesAsync();
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.WithinLimits, MaximumHireCost: 5, MaximumTotalCost: 5,
                Currency: "USD", AllowedCapabilities: ["platform.llm.chat-stream.v1", "platform.business-profile.read.v1"], AllowedNetworkAccess: []),
            0, "Spend no more than five total");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner, headcount: 2);
        var result = await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        Assert.Equal("NeedsReview", result.Status);
        Assert.Equal(1, plan.FulfilledHeadcount);
        Assert.Contains("exhausted", result.Message);
    }

    [Fact]
    public async Task HiringPolicy_IncreaseCannotConsumePreviouslyApprovedExistingSeats()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db, requireConfiguration: false);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Use broad delegation");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner, headcount: 3);
        db.ResourceChangeRoles.Add(new ResourceChangeRoleRecord { Id = Guid.NewGuid(), ResourceChangeRequestId = plan.SourceResourceChangeRequestId!.Value,
            RoleKey = plan.RoleKey!, Title = plan.Title, Headcount = 2, IsDesired = false });
        await db.SaveChangesAsync();
        var result = await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        Assert.Equal(0, plan.FulfilledHeadcount);
        Assert.NotEqual("Succeeded", result.Status);
    }

    [Fact]
    public async Task HiringPolicy_RevokingAuthorityStopsUncommittedPlans()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db, requireConfiguration: false);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Use broad delegation");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application, new(), 1, "Ask before hiring");
        Assert.Equal("NeedsReview", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id)).Status);
        Assert.Empty(await db.HiringRecommendationFulfillments.ToListAsync());
    }

    [Fact]
    public async Task HiringPolicy_UnknownCostsAndRequiredConfigurationRequireOwnerReview()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db, price: null);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Use broad delegation");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        var result = await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        Assert.Equal("NeedsReview", result.Status); Assert.Contains("unknown", result.Message);
        Assert.Equal(0, plan.FulfilledHeadcount);
    }

    [Fact]
    public async Task HiringPolicy_BoundedDelegationAcceptsAnExactPermissionAndCostBoundary()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db, requireConfiguration: false);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.WithinLimits, MaximumHireCost: 0, MaximumTotalCost: 0,
                Currency: "USD", AllowedCapabilities: ["platform.llm.chat-stream.v1", "platform.business-profile.read.v1"], AllowedNetworkAccess: []),
            0, "Allow these permissions for free hires");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        Assert.Equal("Succeeded", (await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id)).Status);
        Assert.Equal(1, plan.FulfilledHeadcount);
    }

    [Fact]
    public async Task HiringPolicy_RequiredConfigurationFallsBackToTheSharedReview()
    {
        await using var db = CreateDb(); var f = await AutonomyFixtureAsync(db);
        await f.Service.UpdatePolicyAsync(f.Organization, f.Installation, f.Application,
            new(HiringSelectionMode.Automatic, AutomaticHiringLevel.BroadDelegation), 0, "Use broad delegation");
        var plan = await AutonomyPlanAsync(db, f.Organization, f.Installation, f.Owner);
        var result = await f.Service.SubmitDelegatedAsync(f.Organization, f.Installation, plan.Id);
        Assert.Equal("NeedsReview", result.Status); Assert.Contains("configuration", result.Message);
        Assert.Equal(0, plan.FulfilledHeadcount);
    }

}
