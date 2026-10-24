using CSweet.Domain.Core;
using CSweet.Contracts.Communications;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;

namespace CSweet.UnitTests;

public sealed partial class UserActionServiceTests
{
    [HiringSuggestionPostgresFact]
    public async Task HiringWithdrawalExecutionCardsAndSuccessor_RollBackTogether()
    {
        var connection = TestConnection();
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options);
        try
        {
            await db.Database.EnsureCreatedAsync();
            var f = await HiringFixtureAsync(db);
            var first = await AddRecommendationAsync(db, f.Organization, f.Installation);
            var next = await AddRecommendationAsync(db, f.Organization, f.Installation);
            var service = new CSweet.Infrastructure.WorkManagement.PersonalTodoService(db, TimeProvider.System);
            var actor = new CSweet.Application.WorkManagement.PersonalTodoActor(f.Agent, f.Installation);
            var card = await service.AddAsync(f.Organization, actor, new("First hire", null, "Medium", null, "first-card", CorrelationId: $"hiring-recommendation:{first:N}"));
            var successor = await service.AddAsync(f.Organization, actor, new("Next hire", null, "Medium", null, "next-card", CorrelationId: $"hiring-recommendation:{next:N}") { StartInBacklog = true });
            var initialOutbox = await db.ApplicationRealtimeOutbox.CountAsync();
            var initialWakes = await db.AgentPlatformEventOutbox.CountAsync();
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                await Hiring(db).WithdrawRecommendationAsync(f.Organization, f.Installation, new(first, "Replace", "withdraw"));
                Assert.Equal(WorkTaskStatus.Cancelled, (await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == card.Id)).Status);
                Assert.Equal(WorkTaskStatus.Ready, (await db.CoreWorkTasks.AsNoTracking().SingleAsync(x => x.Id == successor.Id)).Status);
                await transaction.RollbackAsync();
            }
            db.ChangeTracker.Clear();
            Assert.Equal(ProposalStatus.Pending, (await db.WorkforcePlans.SingleAsync(x => x.Id == first)).Status);
            Assert.Equal(WorkTaskStatus.Ready, (await db.CoreWorkTasks.SingleAsync(x => x.Id == card.Id)).Status);
            Assert.Equal(WorkTaskStatus.Backlog, (await db.CoreWorkTasks.SingleAsync(x => x.Id == successor.Id)).Status);
            Assert.Equal(initialOutbox, await db.ApplicationRealtimeOutbox.CountAsync());
            Assert.Equal(initialWakes, await db.AgentPlatformEventOutbox.CountAsync());
            Assert.Empty(await db.WorkItemActivities.Where(x => x.WorkItemId == card.Id || x.WorkItemId == successor.Id).ToListAsync());
            // Exercise the actual PostgreSQL source/card join used for legacy orphan recovery.
            (await db.WorkforcePlans.SingleAsync(x => x.Id == first)).Status = ProposalStatus.Cancelled;
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            await Hiring(db).ListRecommendationsForInstallationAsync(f.Organization, f.Installation);
            Assert.Equal(WorkTaskStatus.Cancelled, (await db.CoreWorkTasks.SingleAsync(x => x.Id == card.Id)).Status);
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [HiringSuggestionPostgresFact]
    public async Task ConcurrentLateWorkerCannotOverwriteWithdrawnHiringCard()
    {
        var connection = TestConnection();
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new CSweetDbContext(options);
        try
        {
            await db.Database.EnsureCreatedAsync();
            var f = await HiringFixtureAsync(db);
            var target = await AddRecommendationAsync(db, f.Organization, f.Installation);
            var service = new CSweet.Infrastructure.WorkManagement.PersonalTodoService(db, TimeProvider.System);
            var card = await service.AddAsync(f.Organization, new(f.Agent, f.Installation),
                new("Hire", null, "Medium", null, "hiring-card", CorrelationId: $"hiring-recommendation:{target:N}"));
            await using var staleWorker = new CSweetDbContext(options);
            var stale = await staleWorker.CoreWorkTasks.SingleAsync(x => x.Id == card.Id);
            await Hiring(db).WithdrawRecommendationAsync(f.Organization, f.Installation, new(target, "Withdraw", "withdraw"));
            stale.Status = WorkTaskStatus.Completed; stale.Revision++;
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleWorker.SaveChangesAsync());
            db.ChangeTracker.Clear();
            Assert.Equal(WorkTaskStatus.Cancelled, (await db.CoreWorkTasks.SingleAsync(x => x.Id == card.Id)).Status);
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [HiringSuggestionPostgresFact]
    public async Task EmployeeDismissalAndActionExpiration_RollBackTogether()
    {
        var connection = TestConnection();
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new CSweetDbContext(options);
        try
        {
            await db.Database.EnsureCreatedAsync();
            var f = await HiringFixtureAsync(db);
            var target = await AddRecommendationAsync(db, f.Organization, f.Installation);
            await Actions(db).SuggestAsync(f.Organization, f.Installation, Request(f.Message, target, "card"));
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                var result = await new CSweet.Infrastructure.Core.OrganizationUserService(db, new TestAuditEventWriter()).DeleteAsync(f.Agent);
                Assert.True(result.Succeeded);
                Assert.Equal(ProposalStatus.Cancelled, (await db.WorkforcePlans.AsNoTracking().SingleAsync()).Status);
                Assert.Equal("Cancelled", (await db.SuggestedUserActions.AsNoTracking().SingleAsync()).Status);
                Assert.Single(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == SuggestedUserActionEvents.Cancelled).ToListAsync());
                await transaction.RollbackAsync();
            }
            db.ChangeTracker.Clear();
            Assert.True((await db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Agent)).IsActive);
            Assert.Equal(f.Installation, (await db.CoreOrganizationUsers.SingleAsync(x => x.Id == f.Agent)).AgentInstallationId);
            Assert.Equal(ProposalStatus.Pending, (await db.WorkforcePlans.SingleAsync()).Status);
            Assert.Equal("Pending", (await db.SuggestedUserActions.SingleAsync()).Status);
            Assert.Empty(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == SuggestedUserActionEvents.Cancelled).ToListAsync());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [HiringSuggestionPostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentSuggestionAndHire_SerializeInEitherOrder(bool hireFirst)
    {
        var connection = TestConnection();
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var setup = new CSweetDbContext(options);
        try
        {
            await setup.Database.EnsureCreatedAsync();
            var f = await HiringFixtureAsync(setup);
            var target = await AddRecommendationAsync(setup, f.Organization, f.Installation);
            await using var holder = new CSweetDbContext(options);
            await using var transaction = await holder.Database.BeginTransactionAsync();
            await holder.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({f.Organization.ToString()}, 0))");
            if (hireFirst)
                await Hiring(holder).ResolveRecommendationAsync(f.Organization, f.Installation, new(target, f.Hire, "hire"));
            else
                await Actions(holder).SuggestAsync(f.Organization, f.Installation, Request(f.Message, target, "card"));

            var waitingConnection = new NpgsqlConnectionStringBuilder(connection.ConnectionString) {
                ApplicationName = "hiring_suggestion_waiter_" + Guid.NewGuid().ToString("N") };
            await using var waiting = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
                .UseNpgsql(waitingConnection.ConnectionString).Options);
            Task mutation = hireFirst
                ? Actions(waiting).SuggestAsync(f.Organization, f.Installation, Request(f.Message, target, "card"))
                : Hiring(waiting).ResolveRecommendationAsync(f.Organization, f.Installation, new(target, f.Hire, "hire"));

            // Prove the second operation is blocked on the same PostgreSQL lock, rather than hoping
            // Task.WhenAll happens to execute the status read and fulfillment in the intended order.
            await using var observer = new NpgsqlConnection(connection.ConnectionString);
            await observer.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = @name AND wait_event = 'advisory')", observer);
            command.Parameters.AddWithValue("name", waitingConnection.ApplicationName);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var blocked = false;
            while (DateTime.UtcNow < deadline && !mutation.IsCompleted)
            {
                if ((bool)(await command.ExecuteScalarAsync())!) { blocked = true; break; }
                await Task.Delay(20);
            }
            // Release before asserting so a failing test cannot strand the waiting connection.
            await transaction.CommitAsync();
            await mutation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(blocked, "The competing mutation must wait for the organization's hiring transaction.");
            var card = Assert.Single(await setup.SuggestedUserActions.AsNoTracking().ToListAsync());
            Assert.Equal("Completed", card.Status);
            Assert.Equal(f.Hire, card.ResultOrganizationUserId);
            Assert.Single(await setup.ApplicationRealtimeOutbox.Where(x => x.EventType == SuggestedUserActionEvents.Completed).ToListAsync());
        }
        finally { await setup.Database.EnsureDeletedAsync(); }
    }

    [HiringSuggestionPostgresFact]
    public async Task RepairFulfilledSuggestions_IsScopedToRecordedHiresAndIdempotent()
    {
        var connection = TestConnection();
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new CSweetDbContext(options);
        try
        {
            await db.Database.EnsureCreatedAsync();
            var f = await HiringFixtureAsync(db);
            var target = await AddRecommendationAsync(db, f.Organization, f.Installation);
            var unfilled = await AddRecommendationAsync(db, f.Organization, f.Installation);
            await Hiring(db).ResolveRecommendationAsync(f.Organization, f.Installation, new(target, f.Hire, "hire"));
            var completedAt = (await db.WorkforcePlans.AsNoTracking().SingleAsync(x => x.Id == target)).DecidedAt;
            foreach (var status in new[] { "Pending", "Superseded", "Cancelled" })
            {
                var card = HiringAction(f.Organization, f.Installation, f.Conversation, f.Message, "QA", DateTimeOffset.UtcNow);
                card.ParametersJson = System.Text.Json.JsonSerializer.Serialize(new { recommendationId = target });
                card.Status = status;
                if (status == "Superseded") card.SupersededByRole = "Engineer";
                db.SuggestedUserActions.Add(card);
            }
            var untouched = HiringAction(f.Organization, f.Installation, f.Conversation, f.Message, "Other", DateTimeOffset.UtcNow);
            untouched.ParametersJson = System.Text.Json.JsonSerializer.Serialize(new { recommendationId = unfilled });
            db.SuggestedUserActions.Add(untouched);
            var noHire = await AddRecommendationAsync(db, f.Organization, f.Installation);
            var invalidPlan = (await db.WorkforcePlans.FindAsync(noHire))!;
            invalidPlan.Status = ProposalStatus.Approved;
            invalidPlan.FulfilledHeadcount = 1;
            var noHireCard = HiringAction(f.Organization, f.Installation, f.Conversation, f.Message, "Unrecorded", DateTimeOffset.UtcNow);
            noHireCard.ParametersJson = System.Text.Json.JsonSerializer.Serialize(new { recommendationId = noHire });
            var otherOrg = new Organization { Id = Guid.NewGuid(), Name = "Other", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            var foreignCard = HiringAction(otherOrg.Id, f.Installation, f.Conversation, f.Message, "Foreign", DateTimeOffset.UtcNow);
            foreignCard.ParametersJson = System.Text.Json.JsonSerializer.Serialize(new { recommendationId = target });
            db.AddRange(otherOrg, noHireCard, foreignCard);
            await db.SaveChangesAsync();
            var sql = Assert.IsType<SqlOperation>(Assert.Single(new RepairFulfilledHiringSuggestions().UpOperations)).Sql;
            await db.Database.ExecuteSqlRawAsync(sql);
            await db.Database.ExecuteSqlRawAsync(sql);
            var cards = await db.SuggestedUserActions.AsNoTracking().ToListAsync();
            Assert.Equal(2, cards.Count(x => x.Status == "Completed"));
            Assert.Single(cards, x => x.Status == "Cancelled");
            Assert.Equal("Pending", cards.Single(x => x.Id == untouched.Id).Status);
            Assert.Equal("Pending", cards.Single(x => x.Id == noHireCard.Id).Status);
            Assert.Equal("Pending", cards.Single(x => x.Id == foreignCard.Id).Status);
            Assert.All(cards.Where(x => x.Status == "Completed"), x => {
                Assert.Equal(f.Hire, x.ResultOrganizationUserId);
                Assert.Equal(completedAt, x.CompletedAt);
                Assert.Null(x.SupersededByRole);
            });
            var events = await db.ApplicationRealtimeOutbox.Where(x => x.EventType == SuggestedUserActionEvents.Completed).ToListAsync();
            Assert.Equal(2, events.Count);
            Assert.All(events, x => Assert.Contains(f.Owner,
                System.Text.Json.JsonSerializer.Deserialize<Guid[]>(x.RecipientOrganizationUserIdsJson)!));
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    [HiringSuggestionPostgresFact]
    public async Task CompletionEventAndHire_RollBackTogether()
    {
        var connection = TestConnection();
        var options = new DbContextOptionsBuilder<CSweetDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new CSweetDbContext(options);
        try
        {
            await db.Database.EnsureCreatedAsync();
            var f = await HiringFixtureAsync(db);
            var target = await AddRecommendationAsync(db, f.Organization, f.Installation);
            await Actions(db).SuggestAsync(f.Organization, f.Installation, Request(f.Message, target, "card"));
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                await Hiring(db).ResolveRecommendationAsync(f.Organization, f.Installation, new(target, f.Hire, "hire"));
                Assert.Single(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == SuggestedUserActionEvents.Completed).ToListAsync());
                await transaction.RollbackAsync();
            }
            db.ChangeTracker.Clear();
            Assert.Equal("Pending", (await db.SuggestedUserActions.SingleAsync()).Status);
            Assert.Equal(ProposalStatus.Pending, (await db.WorkforcePlans.SingleAsync()).Status);
            Assert.Empty(await db.HiringRecommendationFulfillments.ToListAsync());
            Assert.Empty(await db.ApplicationRealtimeOutbox.Where(x => x.EventType == SuggestedUserActionEvents.Completed).ToListAsync());
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }

    private static NpgsqlConnectionStringBuilder TestConnection()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CSWEET_HIRING_TEST_POSTGRES"));
        Assert.Contains(connection.Host, new[] { "localhost", "127.0.0.1", "::1" });
        connection.Database = "hiring_suggestion_test_" + Guid.NewGuid().ToString("N");
        connection.Pooling = false;
        return connection;
    }

    private sealed class HiringSuggestionPostgresFactAttribute : FactAttribute
    {
        public HiringSuggestionPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_HIRING_TEST_POSTGRES")))
                Skip = "Set CSWEET_HIRING_TEST_POSTGRES to a loopback PostgreSQL test server.";
        }
    }
    private sealed class HiringSuggestionPostgresTheoryAttribute : TheoryAttribute
    {
        public HiringSuggestionPostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CSWEET_HIRING_TEST_POSTGRES")))
                Skip = "Set CSWEET_HIRING_TEST_POSTGRES to a loopback PostgreSQL test server.";
        }
    }
}
