using CSweet.Api.Chat;
using CSweet.Application.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CSweet.UnitTests;

public sealed class ArtifactReviewJobWorkerTests
{
    [Fact]
    public async Task MissingConversationIsSilentAndDoesNotPreventAnAgentDecision()
    {
        await using var f = await Fixture.CreateAsync();
        f.Job.ConversationId = null;
        await f.Db.SaveChangesAsync();

        Assert.True(await f.Worker.ProcessNextAsync(default));
        Assert.Empty(await f.Db.UserNotifications.ToListAsync());
        await f.ReloadAsync();
        Assert.Equal(ArtifactReviewJobStatus.Pending, f.Job.Status);
        Assert.Contains("conversation", f.Job.LastError);

        await ArtifactReviewJobLifecycle.CompleteRevisionAsync(f.Db, f.Job.OrganizationId,
            f.Job.RevisionId, f.Clock.GetUtcNow(), default);
        await f.Db.SaveChangesAsync();
        Assert.Equal(ArtifactReviewJobStatus.Completed, f.Job.Status);
        Assert.Null(f.Job.LastError);
        Assert.Empty(await f.Db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task InactiveReviewerRaisesOnlyOneActionableHumanError()
    {
        await using var f = await Fixture.CreateAsync();
        f.Reviewer.IsActive = false;
        await f.Db.SaveChangesAsync();
        await f.Worker.ProcessNextAsync(default);
        var notification = Assert.Single(await f.Db.UserNotifications.ToListAsync());
        Assert.Equal(f.Owner.Id, notification.RecipientOrganizationUserId);
        Assert.Contains("inactive", notification.Body);
        Assert.Contains("Assign an active reviewer", notification.Body);

        f.Clock.Now = f.Clock.Now.AddMinutes(15);
        await f.Worker.ProcessNextAsync(default);
        Assert.Single(await f.Db.UserNotifications.ToListAsync());

        await ArtifactReviewJobLifecycle.CompleteRevisionAsync(f.Db, f.Job.OrganizationId,
            f.Job.RevisionId, f.Clock.Now, default);
        await f.Db.SaveChangesAsync();
        Assert.NotNull((await f.Db.UserNotifications.SingleAsync()).DismissedAt);
        Assert.Contains(await f.Db.ApplicationRealtimeOutbox.ToListAsync(), x =>
            x.EventType == CSweet.Contracts.Realtime.AppRealtimeEvents.NotificationUpdated);
    }

    [Fact]
    public async Task DeliveryAlreadySavedIsRecoveredWithoutWarningOrDuplicateTurn()
    {
        await using var f = await Fixture.CreateAsync();
        var turns = new ChatTurnService(f.Db);
        var delivered = await turns.StartForAgentAsync(f.Job.OrganizationId, f.Job.ConversationId!.Value,
            f.Reviewer.Id, "Review this revision", f.Creator.Id,
            idempotencyKey: $"artifact-review:{f.Job.Id:D}");
        Assert.NotNull(delivered);

        await f.Worker.ProcessNextAsync(default);
        await f.ReloadAsync();
        Assert.Equal(ArtifactReviewJobStatus.Completed, f.Job.Status);
        Assert.Null(f.Job.LastError);
        Assert.Empty(await f.Db.UserNotifications.ToListAsync());
        Assert.Single(await f.Db.ChatTurns.ToListAsync());
        Assert.Equal(delivered.UserMessage.Id, (await f.Db.ConversationMessageArtifacts.SingleAsync()).MessageId);
    }

    [Fact]
    public async Task BusyReviewerWaitsSilently()
    {
        await using var f = await Fixture.CreateAsync();
        f.Db.ChatTurns.Add(new ChatTurn { Id = Guid.NewGuid(), OrganizationId = f.Job.OrganizationId,
            ConversationId = f.Job.ConversationId!.Value, TargetAgentOrganizationUserId = f.Reviewer.Id,
            Status = ChatTurnStatus.Running });
        await f.Db.SaveChangesAsync();
        await f.Worker.ProcessNextAsync(default);
        await f.ReloadAsync();
        Assert.Equal(ArtifactReviewJobStatus.Pending, f.Job.Status);
        Assert.Equal("Waiting for the active reviewer turn.", f.Job.LastError);
        Assert.Empty(await f.Db.UserNotifications.ToListAsync());
    }

    [Theory]
    [InlineData("decided")]
    [InlineData("superseded")]
    [InlineData("archived")]
    public async Task ObsoleteSubmissionDoesNotEscalateAnInactiveReviewer(string state)
    {
        await using var f = await Fixture.CreateAsync();
        f.Reviewer.IsActive = false;
        if (state == "decided") f.Revision.Status = ArtifactRevisionStatus.Accepted;
        if (state == "superseded") f.Artifact.SubmittedRevisionId = Guid.NewGuid();
        if (state == "archived") f.Artifact.ArchivedAt = f.Clock.Now;
        await f.Db.SaveChangesAsync();
        await f.Worker.ProcessNextAsync(default);
        await f.ReloadAsync();
        Assert.Equal(ArtifactReviewJobStatus.Completed, f.Job.Status);
        Assert.Empty(await f.Db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task AvailableReviewerReceivesReviewWithoutHumanNotification()
    {
        await using var f = await Fixture.CreateAsync();
        // Late-bound origin metadata is sufficient to recover the internal delivery route.
        f.Artifact.OriginConversationId = f.Job.ConversationId;
        f.Job.ConversationId = null;
        await f.Db.SaveChangesAsync();
        await f.Worker.ProcessNextAsync(default);
        await f.ReloadAsync();
        Assert.Equal(ArtifactReviewJobStatus.Completed, f.Job.Status);
        Assert.Single(await f.Db.ChatTurns.ToListAsync());
        Assert.Empty(await f.Db.UserNotifications.ToListAsync());
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required ServiceProvider Services { get; init; }
        public required CSweetDbContext Db { get; init; }
        public required ArtifactReviewJobWorker Worker { get; init; }
        public required FixedClock Clock { get; init; }
        public required ArtifactReviewJob Job { get; set; }
        public required Artifact Artifact { get; init; }
        public required ArtifactRevision Revision { get; init; }
        public required OrganizationUser Reviewer { get; init; }
        public required OrganizationUser Creator { get; init; }
        public required OrganizationUser Owner { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<CSweetDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            var clock = new FixedClock();
            var services = new ServiceCollection();
            services.AddScoped(_ => new CSweetDbContext(options));
            services.AddScoped<IChatTurnService, ChatTurnService>();
            var provider = services.BuildServiceProvider();
            var db = new CSweetDbContext(options);
            var organizationId = Guid.NewGuid();
            var reviewer = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organizationId,
                DisplayName = "Reviewer", EmployeeType = EmployeeType.Agent, AgentInstallationId = Guid.NewGuid() };
            var creator = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organizationId,
                DisplayName = "Creator", EmployeeType = EmployeeType.Agent, AgentInstallationId = Guid.NewGuid() };
            var owner = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organizationId,
                DisplayName = "Owner", EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner };
            var contributor = new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organizationId,
                DisplayName = "Contributor", EmployeeType = EmployeeType.Human };
            var conversation = new Conversation { Id = Guid.NewGuid(), OrganizationId = organizationId,
                AgentOrganizationUserId = reviewer.Id, InitiatedByOrganizationUserId = creator.Id };
            var artifact = new Artifact { Id = Guid.NewGuid(), OrganizationId = organizationId, Title = "Brief",
                Type = ArtifactType.Document, CreatedByOrganizationUserId = creator.Id, ApprovalStatus = ApprovalStatus.Pending };
            var revision = new ArtifactRevision { Id = Guid.NewGuid(), ArtifactId = artifact.Id,
                OrganizationId = organizationId, Status = ArtifactRevisionStatus.Submitted };
            artifact.SubmittedRevisionId = revision.Id;
            var job = new ArtifactReviewJob { Id = Guid.NewGuid(), OrganizationId = organizationId,
                ArtifactId = artifact.Id, RevisionId = revision.Id, ReviewerOrganizationUserId = reviewer.Id,
                ReviewerInstallationId = reviewer.AgentInstallationId, ConversationId = conversation.Id,
                CreatedAt = clock.Now, NextAttemptAt = clock.Now };
            db.AddRange(reviewer, creator, owner, contributor, conversation, artifact, revision, job);
            await db.SaveChangesAsync();
            return new() { Services = provider, Db = db, Clock = clock, Job = job, Artifact = artifact, Revision = revision,
                Reviewer = reviewer, Creator = creator, Owner = owner,
                Worker = new(provider.GetRequiredService<IServiceScopeFactory>(), clock,
                    NullLogger<ArtifactReviewJobWorker>.Instance) };
        }

        public async Task ReloadAsync()
        {
            await Db.Entry(Job).ReloadAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Worker.Dispose();
            await Db.DisposeAsync();
            await Services.DisposeAsync();
        }
    }
}
