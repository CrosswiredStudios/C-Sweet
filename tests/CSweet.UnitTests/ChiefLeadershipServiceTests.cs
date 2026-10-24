using CSweet.Agent.SDK;
using CSweet.Application.Core;
using CSweet.Domain.Core;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json;
using CSweet.AgentHost.Broker;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using CSweet.Infrastructure.Persistence.Migrations;

namespace CSweet.UnitTests;

public sealed partial class UserActionServiceTests
{
    internal static IChiefLeadershipService ChiefAcceptanceService(CSweetDbContext db) => Leadership(db);

    internal static async Task<(Guid Organization, Guid Installation, Guid Chief, Guid Owner, Guid Hire, Guid Conversation, Guid Message)> ChiefAcceptanceFixtureAsync(CSweetDbContext db)
    {
        var fixture = await LeadershipFixtureAsync(db);
        return (fixture.Organization, fixture.Installation, fixture.Agent, fixture.Owner, fixture.Hire, fixture.Conversation, fixture.Message);
    }

    private static readonly ChiefCoverageDomain[] ChiefDomains = [new("product", "Product", "Accountable product outcomes", 1),
        new("research", "Research", "Validate assumptions", 2), new("financial", "Financial", "Financial controls", 3), new("legal", "Legal", "Legal responsibility", 4)];

    [Fact]
    public async Task ChiefFocusNativeAnswerSurvivesRestartAndActivatesOneAssessment()
    {
        await using var db = CreateDb();
        var f = await LeadershipFixtureAsync(db);
        var presented = await Leadership(db).PresentAsync(f.Organization, f.Installation, new("general", 1, f.Conversation, f.Message, ChiefDomains, 0, "focus"));
        var before = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Null(before.Focus); Assert.Null(before.NextAssessmentId);
        Assert.All(before.Assessments, x => Assert.Equal("Backlog", x.ExecutionStatus));
        var answer = await AnswerLeadershipAsync(db, f, presented.DecisionId, "product");
        var command = new CaptureChiefFocusRequest(answer, 0, "capture", presented.DecisionId);
        var first = await Leadership(db).CaptureAsync(f.Organization, f.Installation, command);
        db.ChangeTracker.Clear();
        var duplicate = await Leadership(db).CaptureAsync(f.Organization, f.Installation, command);
        var read = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal(first.Focus!.Id, duplicate.Focus!.Id);
        Assert.Equal(first.Focus.Id, read.Focus!.Id);
        Assert.Equal(1, read.FocusRevision);
        Assert.Equal("product", Assert.Single(read.Assessments, x => x.ExecutionStatus == "Ready").DomainId);
        Assert.Single(await db.ChiefFocusRecords.ToListAsync());
        Assert.Empty(await db.WorkforcePlans.ToListAsync());
        Assert.Equal(4, await db.CoreWorkTasks.CountAsync(x => x.CorrelationId!.StartsWith("chief-assessment:")));
    }

    [Fact]
    public async Task ChiefRejectsWrongOwnerWrongInstallationStaleDecisionAndSpoofedText()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var service = Leadership(db);
        var prompt = await service.PresentAsync(f.Organization, f.Installation, new("general", 1, f.Conversation, f.Message, ChiefDomains, 0, "focus"));
        db.ConversationParticipants.Add(new() { Id = Guid.NewGuid(), ConversationId = f.Conversation,
            OrganizationUserId = f.Hire, Role = ConversationParticipantRole.Member, JoinedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var denied = await new ExecutiveDecisionService(db, new ChatTurnService(db)).AnswerAsync(f.Organization, f.Conversation,
            prompt.DecisionId!.Value, f.Hire, new("product", null, "nonowner"));
        Assert.False(denied.Succeeded);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ReadAsync(Guid.NewGuid(), f.Installation, new()));
        var answer = await AnswerLeadershipAsync(db, f, prompt.DecisionId, "product");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CaptureAsync(f.Organization, Guid.NewGuid(), new(answer, 0, "wrong", prompt.DecisionId)));
        await service.CaptureAsync(f.Organization, f.Installation, new(answer, 0, "answer", prompt.DecisionId));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.CaptureAsync(f.Organization, f.Installation, new(answer, 0, "stale", prompt.DecisionId)));
        var spoof = await OwnerLeadershipTurnAsync(db, f, "Decision: Focus\nAnswer: financial");
        await Assert.ThrowsAsync<ArgumentException>(() => service.CaptureAsync(f.Organization, f.Installation, new(spoof, 1, "spoof", ExplicitDomainId: "financial")));
        Assert.Single(await db.ChiefFocusRecords.ToListAsync());
        Assert.Empty(await db.WorkforcePlans.ToListAsync());
    }

    [Fact]
    public async Task ChiefCoverageOwnerDecisionCompletesResponsibilityAndAdvancesOnce()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var state = await SelectedLeadershipAsync(db, f);
        var row = state.Assessments.Single(x => x.DomainId == "product");
        var result = new ChiefCoverageResult("NeedsCoverage", "Unresolved", null, "Product accountability is not assigned.", "Question", [],
            Question: "Who will own product decisions for the current business?");
        var recorded = await Leadership(db).AssessAsync(f.Organization, f.Installation,
            new(row.Id, row.Revision, state.FocusRevision, state.InputFingerprint, result, "question"));
        Assert.Equal("WaitingDecision", recorded.ExecutionStatus);
        Assert.Null((await Leadership(db).ReadAsync(f.Organization, f.Installation, new())).NextAssessmentId);
        Assert.Empty(await db.WorkforcePlans.ToListAsync());
        var answer = await AnswerLeadershipAsync(db, f, recorded.DecisionId!.Value, "owner");
        var command = new CaptureChiefFocusRequest(answer, state.FocusRevision, "owner", recorded.DecisionId);
        await Leadership(db).CaptureAsync(f.Organization, f.Installation, command);
        db.ChangeTracker.Clear();
        await Leadership(db).CaptureAsync(f.Organization, f.Installation, command);
        var final = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal("AdequatelyCovered", final.Assessments.Single(x => x.Id == row.Id).CoverageStatus);
        Assert.Equal(f.Owner, final.Assessments.Single(x => x.Id == row.Id).AccountableOwnerId);
        Assert.Equal("research", final.Assessments.Single(x => x.Id == final.NextAssessmentId).DomainId);
        Assert.Single(final.Assessments, x => x.ExecutionStatus == "Ready");
    }

    [Fact]
    public async Task ChiefCoverageExternalChoiceRemainsUnverifiedAndDoesNotHire()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var state = await SelectedLeadershipAsync(db, f); var row = state.Assessments.Single(x => x.DomainId == "product");
        var result = new ChiefCoverageResult("NeedsCoverage", "Unresolved", null, "Coverage needs an explicit owner.", "Question", [], Question: "Who owns this responsibility?");
        var waiting = await Leadership(db).AssessAsync(f.Organization, f.Installation, new(row.Id, row.Revision, state.FocusRevision, state.InputFingerprint, result, "question"));
        var answer = await AnswerLeadershipAsync(db, f, waiting.DecisionId!.Value, "external");
        var final = await Leadership(db).CaptureAsync(f.Organization, f.Installation, new(answer, state.FocusRevision, "external", waiting.DecisionId));
        Assert.Equal("Blocked", final.Assessments.Single(x => x.Id == row.Id).ExecutionStatus);
        Assert.Null(final.NextAssessmentId);
        Assert.Empty(await db.WorkforcePlans.ToListAsync());
    }

    [Fact]
    public async Task ChiefGapCreatesOneRecommendationAndWithdrawalDefersCoverage()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var state = await SelectedLeadershipAsync(db, f); var row = state.Assessments.Single(x => x.DomainId == "product");
        var result = new ChiefCoverageResult("NeedsCoverage", "Unresolved", null, "No assigned Product Manager currently owns this gap.", "Hire", [f.Owner],
            RoleKey: "product-manager", RoleTitle: "Product Manager", RoleObjective: "Own validated customer outcomes.");
        var command = new RecordChiefAssessmentRequest(row.Id, row.Revision, state.FocusRevision, state.InputFingerprint, result, "hire-gap");
        var waiting = await Leadership(db).AssessAsync(f.Organization, f.Installation, command);
        db.ChangeTracker.Clear();
        Assert.Equal(waiting.RecommendationId, (await Leadership(db).AssessAsync(f.Organization, f.Installation, command)).RecommendationId);
        Assert.Single(await db.WorkforcePlans.ToListAsync());
        Assert.Null((await Leadership(db).ReadAsync(f.Organization, f.Installation, new())).NextAssessmentId);
        await Hiring(db).WithdrawRecommendationAsync(f.Organization, f.Installation, new(waiting.RecommendationId!.Value, "Owner deferred", "withdraw"));
        var resumed = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal("Deferred", resumed.Assessments.Single(x => x.Id == row.Id).CoverageStatus);
        Assert.Equal("Complete", resumed.Assessments.Single(x => x.Id == row.Id).ExecutionStatus);
        Assert.Single(await db.WorkforcePlans.ToListAsync());
    }

    [Fact]
    public async Task ChiefOwnerPauseAndReplacementPreserveFocusAndFencePreviousInstallation()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var state = await SelectedLeadershipAsync(db, f);
        var pause = await OwnerLeadershipTurnAsync(db, f, "Pause proactive leadership");
        await Leadership(db).ControlAsync(f.Organization, f.Installation, new(pause, true, state.FocusRevision, "pause"));
        Assert.Null((await Leadership(db).ReadAsync(f.Organization, f.Installation, new())).NextAssessmentId);
        var chief = await db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Agent);
        var replacement = Guid.NewGuid(); chief.AgentInstallationId = replacement; await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Leadership(db).ReadAsync(f.Organization, f.Installation, new()));
        var read = await Leadership(db).ReadAsync(f.Organization, replacement, new());
        Assert.Equal(state.Focus!.Id, read.Focus!.Id);
        Assert.True(read.Paused); Assert.Null(read.NextAssessmentId);
    }

    [HiringSuggestionPostgresFact]
    public async Task ChiefLeadershipFocusAndWakeRollBackTogetherInPostgres()
    {
        var connection = TestConnection();
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options);
        try
        {
            await db.Database.EnsureCreatedAsync(); var f = await LeadershipFixtureAsync(db);
            var prompt = await Leadership(db).PresentAsync(f.Organization, f.Installation, new("general", 1, f.Conversation, f.Message, ChiefDomains, 0, "focus"));
            var answer = await AnswerLeadershipAsync(db, f, prompt.DecisionId, "product");
            var wakes = await db.AgentPlatformEventOutbox.CountAsync();
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                await Leadership(db).CaptureAsync(f.Organization, f.Installation, new(answer, 0, "answer", prompt.DecisionId));
                Assert.Single(await db.ChiefFocusRecords.ToListAsync());
                await transaction.RollbackAsync();
            }
            db.ChangeTracker.Clear();
            Assert.Empty(await db.ChiefFocusRecords.ToListAsync());
            Assert.Equal(wakes, await db.AgentPlatformEventOutbox.CountAsync());
            Assert.All(await db.ChiefCoverageRecords.ToListAsync(), x => Assert.Equal("Backlog", x.ExecutionStatus));
            // A later reconnect legitimately recovers the separately committed native answer.
            Assert.Equal("product", (await Leadership(db).ReadAsync(f.Organization, f.Installation, new())).Focus!.DomainId);
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private static ChiefLeadershipService Leadership(CSweetDbContext db, bool enabled = true) => new(db, TimeProvider.System,
        new ExecutiveDecisionService(db, new ChatTurnService(db)),
        new CommunicationHubService(db, new TestAuditEventWriter(), new ChatTurnService(db)), Hiring(db), Options.Create(new ChiefLeadershipOptions { Enabled = enabled }));

    [Fact]
    public async Task ChiefBrokerUsesSessionAuthorityAndRegistersHiddenBoundedSchemas()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var handler = new ChiefLeadershipCapabilityHandler(Leadership(db));
        var catalog = new McpToolCatalog([handler]);
        var descriptors = catalog.List(ChiefLeadershipCapabilities.All);
        Assert.Equal(5, descriptors.Count);
        Assert.All(descriptors, tool => { Assert.False(tool.ModelVisible); Assert.False(tool.InputSchema.GetProperty("additionalProperties").GetBoolean()); });
        Assert.Equal(100, descriptors.Single(x => x.Capability == ChiefLeadershipCapabilities.Read).InputSchema
            .GetProperty("properties").GetProperty("pageSize").GetProperty("maximum").GetInt32());
        var session = new AgentSession("session", "chief", f.Installation.ToString(), f.Organization.ToString(),
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), ChiefLeadershipCapabilities.All, Revision: 1));
        var request = new RequestCapability { RequestId = "read", Capability = ChiefLeadershipCapabilities.Read,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(new ReadChiefLeadershipRequest(), new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        async Task<CapabilityResult> Invoke(AgentSession identity, RequestCapability command)
        {
            var results = new List<CapabilityResult>();
            await foreach (var result in handler.HandleAsync(identity, command, CancellationToken.None)) results.Add(result);
            return Assert.Single(results);
        }
        Assert.True((await Invoke(session, request)).Succeeded);
        Assert.False((await Invoke(session with { InstallationId = Guid.NewGuid().ToString() }, request)).Succeeded);
        Assert.False((await Invoke(session with { Grant = new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), Revision: 1) }, request)).Succeeded);
        Assert.False((await Invoke(session, new RequestCapability { RequestId = "invalid", Capability = ChiefLeadershipCapabilities.Present, Payload = JsonPayload.From("{}"u8.ToArray()) })).Succeeded);
        Assert.Empty(await db.ChiefFocusRecords.ToListAsync());
    }

    [HiringSuggestionPostgresFact]
    public async Task ChiefLeadershipMigrationsUpgradeEmptyStateAndRefuseDestructiveRollback()
    {
        var connection = TestConnection();
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options);
        try
        {
            await db.Database.EnsureCreatedAsync();
            var initial = new AddChiefLeadershipState(); var integrity = new AddChiefLeadershipOrganizationIntegrity();
            async Task Apply(IReadOnlyList<Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation> operations)
            {
                foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(operations))
                    await db.Database.ExecuteSqlRawAsync(command.CommandText);
            }
            await Apply(integrity.DownOperations); await Apply(initial.DownOperations);
            await Apply(initial.UpOperations); await Apply(integrity.UpOperations);
            var f = await LeadershipFixtureAsync(db); await SelectedLeadershipAsync(db, f);
            var error = await Assert.ThrowsAsync<PostgresException>(() => Apply(initial.DownOperations));
            Assert.Equal("P0001", error.SqlState);
            Assert.Single(await db.ChiefFocusRecords.ToListAsync());
            Assert.Equal(4, await db.ChiefCoverageRecords.CountAsync());
            Assert.False(db.Database.HasPendingModelChanges());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [Fact]
    public async Task ChiefBrokerTreatsStaleAssessmentAndPreemptionAsConflictsWithoutBlockingCoverage()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var state = await SelectedLeadershipAsync(db, f);
        var row = state.Assessments.Single(x => x.Id == state.NextAssessmentId);
        var session = new AgentSession("session", "chief", f.Installation.ToString(), f.Organization.ToString(),
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), ChiefLeadershipCapabilities.All, Revision: 1));
        var command = new RecordChiefAssessmentRequest(row.Id, row.Revision + 1, state.FocusRevision, state.InputFingerprint,
            new("NotApplicable", "Unresolved", null, "Current scope excludes this responsibility.", "None", []), "stale-assessment");
        async Task AssertConflict(RecordChiefAssessmentRequest input)
        {
            var handler = new ChiefLeadershipCapabilityHandler(Leadership(db));
            await foreach (var response in handler.HandleAsync(session, new() { RequestId = input.IdempotencyKey,
                Capability = ChiefLeadershipCapabilities.Assess, Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))) }, default))
            {
                Assert.False(response.Succeeded);
                Assert.Equal(PlatformCapabilityErrorCode.Conflict,
                    JsonSerializer.Deserialize<PlatformCapabilityError>(response.Payload.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Code);
            }
        }
        await AssertConflict(command);
        var pause = await OwnerLeadershipTurnAsync(db, f, "Pause proactive leadership");
        await Leadership(db).ControlAsync(f.Organization, f.Installation, new(pause, true, state.FocusRevision, "pause-for-preemption"));
        await AssertConflict(command with { ExpectedRevision = row.Revision, IdempotencyKey = "preempted-assessment" });
        var after = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal("Ready", after.Assessments.Single(x => x.Id == row.Id).ExecutionStatus);
        Assert.Empty(after.Assessments.Where(x => x.ExecutionStatus == "Blocked"));
    }

    [Fact]
    public async Task ChiefMissedFocusCallbackRecoversOnlyTheAuthenticatedNativeAnswer()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var prompt = await Leadership(db).PresentAsync(f.Organization, f.Installation, new("general", 1, f.Conversation, f.Message, ChiefDomains, 0, "focus"));
        await AnswerLeadershipAsync(db, f, prompt.DecisionId, "product");
        db.ChangeTracker.Clear();
        var recovered = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal("product", recovered.Focus!.DomainId);
        await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Single(await db.ChiefFocusRecords.ToListAsync());
        Assert.Single(recovered.Assessments, x => x.ExecutionStatus == "Ready");
    }

    [Fact]
    public async Task ChiefInitialExplicitPriorityIsSavedWithoutAnotherQuestionOrHire()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var turn = await OwnerLeadershipTurnAsync(db, f, "Focus on product");
        var source = await db.ChatTurns.SingleAsync(x => x.Id == turn);
        var command = new PresentChiefFocusRequest("general", 1, f.Conversation, source.UserMessageId, ChiefDomains, 0,
            "initial-priority", turn, "product");
        var captured = await Leadership(db).PresentAsync(f.Organization, f.Installation, command);
        Assert.Null(captured.DecisionId);
        db.ChangeTracker.Clear();
        await Leadership(db).PresentAsync(f.Organization, f.Installation, command);
        var state = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal("product", state.Focus!.DomainId); Assert.Equal(turn, state.Focus.SourceTurnId);
        Assert.Single(state.Assessments, x => x.ExecutionStatus == "Ready");
        Assert.Empty(await db.ExecutiveDecisions.ToListAsync()); Assert.Empty(await db.WorkforcePlans.ToListAsync());
    }

    [Fact]
    public async Task ChiefHistoricalOwnerProseCannotInitializeFocusOrChangePauseControl()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var oldTurn = await OwnerLeadershipTurnAsync(db, f, "Focus on product");
        var source = await db.ChatTurns.SingleAsync(x => x.Id == oldTurn); source.Status = ChatTurnStatus.Completed;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Leadership(db).PresentAsync(f.Organization, f.Installation,
            new("general", 1, f.Conversation, source.UserMessageId, ChiefDomains, 0, "historical", oldTurn, "product")));
        var pause = await OwnerLeadershipTurnAsync(db, f, "Pause proactive leadership");
        (await db.ChatTurns.SingleAsync(x => x.Id == pause)).Status = ChatTurnStatus.Completed;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Leadership(db).ControlAsync(f.Organization, f.Installation, new(pause, true, 0, "old-pause")));
        Assert.Empty(await db.ChiefFocusRecords.ToListAsync()); Assert.Empty(await db.ExecutiveDecisions.ToListAsync());
        Assert.False((await Leadership(db).ReadAsync(f.Organization, f.Installation, new())).Paused);
    }

    [Fact]
    public async Task ChiefCustomFocusIsRetainedWithoutInferringAStaffingAction()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var prompt = await Leadership(db).PresentAsync(f.Organization, f.Installation, new("general", 1, f.Conversation, f.Message, ChiefDomains, 0, "focus"));
        var answer = await new ExecutiveDecisionService(db, new ChatTurnService(db)).AnswerAsync(f.Organization, f.Conversation,
            prompt.DecisionId!.Value, f.Owner, new(null, "Validate our proposed acquisition", "custom-answer"));
        Assert.True(answer.Succeeded, answer.Message);
        var result = await Leadership(db).CaptureAsync(f.Organization, f.Installation, new(answer.Turn!.Id, 0, "custom", prompt.DecisionId));
        Assert.Equal("Validate our proposed acquisition", result.Focus!.CustomText);
        Assert.Equal("Blocked", result.Assessments.Single(x => x.DomainId == "custom").ExecutionStatus);
        Assert.Null(result.NextAssessmentId); Assert.Empty(await db.WorkforcePlans.ToListAsync());
    }

    [Fact]
    public async Task ChiefLegacyNativeDecisionIsBoundWithoutReaskingOrInferringProse()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var legacy = await new ExecutiveDecisionService(db, new ChatTurnService(db)).CreateAsync(new(f.Organization, f.Conversation,
            null, f.Message, f.Installation, "Legacy focus", ChiefDomains.Select(x => new CSweet.Application.Communications.CreateExecutiveDecisionOption(x.Id, x.Label, x.Description)).ToArray(),
            "product", "legacy-focus"));
        await AnswerLeadershipAsync(db, f, legacy.Id, "product");
        var linked = await Leadership(db).PresentAsync(f.Organization, f.Installation, new("general", 1, f.Conversation, f.Message, ChiefDomains, 0, "legacy-focus"));
        Assert.Equal(legacy.Id, linked.DecisionId); Assert.NotNull(linked.AnswerTurnId);
        var recovered = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal("product", recovered.Focus!.DomainId); Assert.Single(await db.ExecutiveDecisions.ToListAsync());
    }

    [Fact]
    public async Task ChiefFulfilledHireRequiresReassessmentAndFocusChangePreservesCommitment()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var state = await SelectedLeadershipAsync(db, f); var row = state.Assessments.Single(x => x.DomainId == "product");
        var hire = new ChiefCoverageResult("NeedsCoverage", "Unresolved", null, "Verified unassigned product accountability.", "Hire", [],
            RoleKey: "product-manager", RoleTitle: "Product Manager", RoleObjective: "Own product outcomes.");
        var waiting = await Leadership(db).AssessAsync(f.Organization, f.Installation, new(row.Id, row.Revision, 1, state.InputFingerprint, hire, "gap"));
        var turn = await OwnerLeadershipTurnAsync(db, f, "Focus on research");
        var refocused = await Leadership(db).CaptureAsync(f.Organization, f.Installation, new(turn, 1, "change", ExplicitDomainId: "research"));
        Assert.Null(refocused.NextAssessmentId);
        Assert.Equal("WaitingHire", refocused.Assessments.Single(x => x.Id == row.Id).ExecutionStatus);
        await Hiring(db).ResolveRecommendationAsync(f.Organization, f.Installation, new(waiting.RecommendationId!.Value, f.Hire, "fulfill"));
        db.ChangeTracker.Clear();
        var after = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        var reassess = after.Assessments.Single(x => x.Id == row.Id);
        Assert.Equal("Ready", reassess.ExecutionStatus); Assert.Equal("Unassessed", reassess.CoverageStatus);
        Assert.Single(await db.WorkforcePlans.ToListAsync());
        Assert.Equal("research", after.Focus!.DomainId);
        // A fulfillment receipt alone is insufficient. Establish actual current responsibility,
        // then accept the reassessment and verify continuation after a restart.
        db.LeadershipAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization, OrganizationUserId = f.Hire,
            PositionKey = "product-manager", StartsAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        after = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        reassess = after.Assessments.Single(x => x.Id == row.Id);
        var covered = new ChiefCoverageResult("AdequatelyCovered", "Combined", f.Hire,
            "The assigned product manager owns the agreed responsibility and has capacity for this scope.", "None", [f.Hire]);
        await Leadership(db).AssessAsync(f.Organization, f.Installation,
            new(row.Id, reassess.Revision, after.FocusRevision, after.InputFingerprint, covered, "verified"));
        db.ChangeTracker.Clear();
        var complete = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal("Complete", complete.Assessments.Single(x => x.Id == row.Id).ExecutionStatus);
        Assert.Equal("research", complete.Assessments.Single(x => x.Id == complete.NextAssessmentId).DomainId);
    }

    [Fact]
    public async Task ChiefBlocksCeoTitleEvidenceAndConflictingGameStudioHiring()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var state = await SelectedLeadershipAsync(db, f); var row = state.Assessments.Single(x => x.DomainId == "product");
        var ceo = new ChiefCoverageResult("AdequatelyCovered", "Combined", f.Owner, "The CEO exists.", "None", [f.Owner]);
        await Assert.ThrowsAsync<ArgumentException>(() => Leadership(db).AssessAsync(f.Organization, f.Installation,
            new(row.Id, row.Revision, 1, state.InputFingerprint, ceo, "unsupported-ceo")));
        var agenda = await db.ChiefLeadershipAgendas.SingleAsync(); agenda.ProfileKey = "game-studio";
        foreach (var record in await db.ChiefCoverageRecords.ToListAsync()) record.ProfileKey = "game-studio";
        db.LeadershipAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization, OrganizationUserId = f.Owner,
            PositionKey = "creative-director", StartsAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        state = await Leadership(db).ReadAsync(f.Organization, f.Installation, new()); row = state.Assessments.Single(x => x.Id == row.Id);
        var hire = new ChiefCoverageResult("NeedsCoverage", "Unresolved", null, "A model proposed another director.", "Hire", [],
            RoleKey: "creative-director", RoleTitle: "Creative Director", RoleObjective: "Own creative outcomes.");
        await Assert.ThrowsAsync<ArgumentException>(() => Leadership(db).AssessAsync(f.Organization, f.Installation,
            new(row.Id, row.Revision, 1, state.InputFingerprint, hire, "conflict")));
        Assert.Empty(await db.WorkforcePlans.ToListAsync());
        Assert.Equal("Ready", (await db.ChiefCoverageRecords.SingleAsync(x => x.Id == row.Id)).ExecutionStatus);
    }

    [Fact]
    public async Task ChiefCannotTurnLegalCoverageIntoAnAgentProductManagerHire()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        await SelectedLeadershipAsync(db, f);
        var turn = await OwnerLeadershipTurnAsync(db, f, "Focus on legal");
        var state = await Leadership(db).CaptureAsync(f.Organization, f.Installation, new(turn, 1, "legal", ExplicitDomainId: "legal"));
        var row = state.Assessments.Single(x => x.DomainId == "legal");
        await Assert.ThrowsAsync<ArgumentException>(() => Leadership(db).AssessAsync(f.Organization, f.Installation,
            new(row.Id, row.Revision, state.FocusRevision, state.InputFingerprint,
                new("NeedsCoverage", "Unresolved", null, "The legal responsibility is unresolved.", "Hire", [],
                    RoleKey: "product-manager", RoleTitle: "Product Manager", RoleObjective: "Own legal coverage."), "invalid-role-scope")));
        Assert.Empty(await db.WorkforcePlans.ToListAsync());
    }

    [Fact]
    public async Task ChiefGameStudioExistingCreativeOwnershipCompletesWithoutCompanyHiring()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        db.LeadershipAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization, OrganizationUserId = f.Hire,
            PositionKey = "creative-director", StartsAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var domains = new[] { new ChiefCoverageDomain("creative-product", "Creative and Product", "Creative direction and player outcomes", 1) }.Concat(ChiefDomains.Skip(1)).ToArray();
        var prompt = await Leadership(db).PresentAsync(f.Organization, f.Installation, new("game-studio", 1, f.Conversation, f.Message, domains, 0, "studio"));
        var answer = await AnswerLeadershipAsync(db, f, prompt.DecisionId, "creative-product");
        var state = await Leadership(db).CaptureAsync(f.Organization, f.Installation, new(answer, 0, "studio-answer", prompt.DecisionId));
        await new ChatTurnService(db).CompleteAsync(answer, f.LaterMessage, false);
        var row = state.Assessments.Single(x => x.DomainId == "creative-product");
        await Leadership(db).AssessAsync(f.Organization, f.Installation, new(row.Id, row.Revision, state.FocusRevision, state.InputFingerprint,
            new("AdequatelyCovered", "Combined", f.Hire, "The current Creative Director owns this game's direction and initial team design with capacity for this scope.", "None", [f.Hire]), "studio-covered"));
        db.ChangeTracker.Clear(); var resumed = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal("Complete", resumed.Assessments.Single(x => x.Id == row.Id).ExecutionStatus);
        Assert.Equal("research", resumed.Assessments.Single(x => x.Id == resumed.NextAssessmentId).DomainId);
        Assert.Empty(await db.WorkforcePlans.ToListAsync());
    }

    [Fact]
    public async Task ChiefEvidenceBlockRetriesOnlyAfterInputsChangeAndReviewIsBounded()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var state = await SelectedLeadershipAsync(db, f); var row = state.Assessments.Single(x => x.DomainId == "product");
        await Leadership(db).AssessAsync(f.Organization, f.Installation, new(row.Id, row.Revision, 1, state.InputFingerprint,
            new("NeedsCoverage", "Unresolved", null, "Missing accountability evidence.", "Blocked", []), "block"));
        Assert.Null((await Leadership(db).ReadAsync(f.Organization, f.Installation, new())).NextAssessmentId);
        (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Owner)).Revision++;
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var recovered = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal(row.Id, recovered.NextAssessmentId);
        var revision = recovered.Assessments.Single(x => x.Id == row.Id).Revision;
        Assert.Equal(revision, (await Leadership(db).ReadAsync(f.Organization, f.Installation, new())).Assessments.Single(x => x.Id == row.Id).Revision);
    }

    [Fact]
    public async Task ChiefBackfillsLegacyCardAndReopensOnlyOneDueReview()
    {
        await using var db = CreateDb(); var f = await LeadershipFixtureAsync(db);
        var old = await new CSweet.Infrastructure.WorkManagement.PersonalTodoService(db, TimeProvider.System).AddAsync(f.Organization,
            new(f.Agent, f.Installation), new("Assess product coverage", "Legacy agenda", "Medium", null, "legacy-card",
                CorrelationId: "leadership-coverage:general:product") { StartInBacklog = true });
        var state = await SelectedLeadershipAsync(db, f);
        Assert.Equal(old.Id, state.Assessments.Single(x => x.DomainId == "product").WorkItemId);
        Assert.Equal(4, await db.CoreWorkTasks.CountAsync(x => x.IsExecutable));
        // Simulate an exhausted agenda whose explicit review date is due after restart.
        foreach (var row in await db.ChiefCoverageRecords.ToListAsync())
        { row.ExecutionStatus = "Complete"; row.CoverageStatus = "Deferred"; row.ReviewAt = DateTimeOffset.UtcNow.AddMinutes(-1); }
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var reviewed = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Single(reviewed.Assessments, x => x.ExecutionStatus == "Ready");
        var same = await Leadership(db).ReadAsync(f.Organization, f.Installation, new());
        Assert.Equal(reviewed.NextAssessmentId, same.NextAssessmentId);
        Assert.Single(same.Assessments, x => x.ExecutionStatus == "Ready");
    }

    [HiringSuggestionPostgresFact]
    public async Task ChiefConcurrentNativeAnswersAndFocusCaptureRemainImmutableInPostgres()
    {
        var connection = TestConnection(); var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new CSweetDbContext(options);
        try
        {
            await db.Database.EnsureCreatedAsync(); var f = await LeadershipFixtureAsync(db);
            var prompt = await Leadership(db).PresentAsync(f.Organization, f.Installation, new("general", 1, f.Conversation, f.Message, ChiefDomains, 0, "focus"));
            async Task<CSweet.Contracts.Communications.AnswerExecutiveDecisionResponse> Answer(string option)
            {
                await using var concurrent = new CSweetDbContext(options);
                return await new ExecutiveDecisionService(concurrent, new ChatTurnService(concurrent)).AnswerAsync(f.Organization,
                    f.Conversation, prompt.DecisionId!.Value, f.Owner, new(option, null, option));
            }
            var answers = await Task.WhenAll(Answer("product"), Answer("research"));
            Assert.Single(answers, x => x.Succeeded);
            var accepted = answers.Single(x => x.Succeeded);
            async Task Capture()
            {
                await using var concurrent = new CSweetDbContext(options);
                await Leadership(concurrent).CaptureAsync(f.Organization, f.Installation, new(accepted.Turn!.Id, 0, "capture", prompt.DecisionId));
            }
            await Task.WhenAll(Capture(), Capture());
            db.ChangeTracker.Clear();
            Assert.Single(await db.ChiefFocusRecords.ToListAsync());
            Assert.Single(await db.ChiefCoverageRecords.Where(x => x.ExecutionStatus == "Ready").ToListAsync());
            Assert.False(db.Database.HasPendingModelChanges());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private static async Task<HiringFixture> LeadershipFixtureAsync(CSweetDbContext db)
    {
        var f = await HiringFixtureAsync(db);
        (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Agent)).ReportsToOrganizationUserId = f.Owner;
        db.LeadershipAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization, OrganizationUserId = f.Agent,
            PositionKey = LeadershipPositionKeys.ChiefOfStaff, StartsAt = DateTimeOffset.UtcNow });
        db.LeadershipAssignments.Add(new() { Id = Guid.NewGuid(), OrganizationId = f.Organization, OrganizationUserId = f.Owner,
            PositionKey = LeadershipPositionKeys.ChiefExecutiveOfficer, StartsAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(); return f;
    }

    private static async Task<Guid> AnswerLeadershipAsync(CSweetDbContext db, HiringFixture f, Guid? decision, string option)
    {
        // Exercise the normal native decision service and its authenticated answer turn.
        var response = await new ExecutiveDecisionService(db, new ChatTurnService(db))
            .AnswerAsync(f.Organization, f.Conversation, decision!.Value, f.Owner, new(option, null, $"answer:{decision:N}"));
        Assert.True(response.Succeeded, response.Message);
        return response.Turn!.Id;
    }
    private static async Task<ChiefLeadershipSnapshot> SelectedLeadershipAsync(CSweetDbContext db, HiringFixture f)
    {
        var presented = await Leadership(db).PresentAsync(f.Organization, f.Installation, new("general", 1, f.Conversation, f.Message, ChiefDomains, 0, "focus"));
        var answer = await AnswerLeadershipAsync(db, f, presented.DecisionId, "product");
        var state = await Leadership(db).CaptureAsync(f.Organization, f.Installation, new(answer, 0, "selected", presented.DecisionId));
        await new ChatTurnService(db).CompleteAsync(answer, f.LaterMessage, false);
        return state;
    }
    private static async Task<Guid> OwnerLeadershipTurnAsync(CSweetDbContext db, HiringFixture f, string text)
    {
        var message = new ConversationMessage { Id = Guid.NewGuid(), ConversationId = f.Conversation,
            Role = ConversationRole.User, SenderOrganizationUserId = f.Owner, Content = text, CreatedAt = DateTimeOffset.UtcNow };
        var turn = new ChatTurn { Id = Guid.NewGuid(), OrganizationId = f.Organization, ConversationId = f.Conversation,
            TargetAgentOrganizationUserId = f.Agent, UserMessageId = message.Id, Status = ChatTurnStatus.Running, CreatedAt = message.CreatedAt, UpdatedAt = message.CreatedAt };
        db.AddRange(message, turn); await db.SaveChangesAsync(); return turn.Id;
    }
}
