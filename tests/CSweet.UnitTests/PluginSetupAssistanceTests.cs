using System.Text.Json;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;

namespace CSweet.UnitTests;

public sealed class PluginSetupAssistanceTests
{
    [Fact]
    public async Task SetupDeliverySurvivesRestartAndSendsOnlyOneReminder()
    {
        await using var fixture = await Fixture.Create();
        var clock = new MutableClock(fixture.Obligation.CreatedAt);
        var inbox = new AgentWorkInbox(fixture.Db, new EphemeralDataProtectionProvider(), clock);
        await new PluginSetupObligationDispatcher(fixture.Db, inbox, clock).DispatchAsync(default);
        var introductionId = fixture.Obligation.IntroductionWorkId;
        Assert.NotNull(introductionId);
        Assert.Single(await fixture.Db.AgentWorkItems.ToListAsync());
        // Simulate the crash window after inbox persistence but before saving its ID on the obligation.
        fixture.Obligation.IntroductionWorkId = null;
        await fixture.Db.SaveChangesAsync();
        await new PluginSetupObligationDispatcher(fixture.Db, inbox, clock).DispatchAsync(default);
        Assert.Equal(introductionId, fixture.Obligation.IntroductionWorkId);
        Assert.Single(await fixture.Db.AgentWorkItems.ToListAsync());
        clock.Now = clock.Now.AddHours(25);
        await new PluginSetupObligationDispatcher(fixture.Db, inbox, clock).DispatchAsync(default);
        Assert.NotNull(fixture.Obligation.ReminderWorkId);
        Assert.NotEqual(introductionId, fixture.Obligation.ReminderWorkId);
        clock.Now = clock.Now.AddDays(7);
        await new PluginSetupObligationDispatcher(fixture.Db, inbox, clock).DispatchAsync(default);
        Assert.Equal(2, await fixture.Db.AgentWorkItems.CountAsync());
    }

    [Fact]
    public async Task OnlyTheProtectedConversationCanBeRead()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Validate("communication.chat.read.v1", new { chatId = fixture.Obligation.ConversationId });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Validate("communication.chat.read.v1", new { chatId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Validate("communication.chat.read.v1", new { }));
    }

    [Theory]
    [InlineData("platform.web.request.v1")]
    [InlineData("platform.genai.job.read.v1")]
    [InlineData("communication.chat.create.v1")]
    [InlineData("youtube.api.video.upload.v1")]
    public async Task UnrelatedAndImplicitAuthorityIsDenied(string capability)
    {
        await using var fixture = await Fixture.Create();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Validate(capability, new { }));
    }

    [Fact]
    public async Task TextOnlyLlmRequestsMustBeBoundToSetup()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Validate("platform.llm.chat-stream.v1", new
        {
            telemetry = new { conversationId = fixture.Obligation.ConversationId },
            messages = new[] { new { role = "user", contents = new[] { new { kind = "text", text = "How do I connect?" } } } }
        });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Validate("platform.llm.chat-stream.v1", new
        {
            telemetry = new { conversationId = fixture.Obligation.ConversationId },
            messages = new[] { new { role = "user", contents = new[] { new { kind = "media_reference", attachmentId = Guid.NewGuid() } } } }
        }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Validate("platform.llm.chat-stream.v1", new
        {
            telemetry = new { conversationId = Guid.NewGuid() }, messages = Array.Empty<object>()
        }));
    }

    [Fact]
    public async Task SendsCannotMentionOtherEmployeesOrAttachCompanyMedia()
    {
        await using var fixture = await Fixture.Create();
        await fixture.Validate("communication.message.send.v1", new { chatId = fixture.Obligation.ConversationId, content = "Please connect." });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Validate("communication.message.send.v1", new
        { chatId = fixture.Obligation.ConversationId, attachmentMediaAssetIds = new[] { Guid.NewGuid() } }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Validate("communication.message.send.v1", new
        { chatId = fixture.Obligation.ConversationId, mentions = new[] { new { organizationUserId = Guid.NewGuid() } } }));
        // Handler deserialization is case-insensitive; alternate casing must not bypass the guard.
        var alternate = JsonSerializer.SerializeToElement(new { chatId = fixture.Obligation.ConversationId, AttachmentMediaAssetIds = new[] { Guid.NewGuid() } });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Policy.ValidateCapabilityAsync(
            fixture.Installation.BusinessId, fixture.Installation.Id, "communication.message.send.v1", alternate, default));
    }

    [Fact]
    public async Task SetupEventRequiresHostSourceAndExactParticipantBinding()
    {
        await using var fixture = await Fixture.Create();
        var payload = JsonSerializer.SerializeToElement(new
        {
            conversationId = fixture.Obligation.ConversationId, installationId = fixture.Installation.Id,
            organizationId = fixture.Obligation.OrganizationId, agentOrganizationUserId = fixture.Obligation.AgentOrganizationUserId,
            humanOrganizationUserId = fixture.Obligation.HumanOrganizationUserId
        });
        Assert.True(await fixture.Policy.AllowsWorkAsync(fixture.Installation.BusinessId, fixture.Installation.Id,
            AgentWorkKind.Event, PluginSetupAssistancePolicy.RequestedEvent, payload, "plugin-setup-assistance", default));
        Assert.False(await fixture.Policy.AllowsWorkAsync(fixture.Installation.BusinessId, fixture.Installation.Id,
            AgentWorkKind.Event, PluginSetupAssistancePolicy.RequestedEvent, payload, "agent-coordination", default));
        Assert.False(await fixture.Policy.AllowsWorkAsync(fixture.Installation.BusinessId, Guid.NewGuid(),
            AgentWorkKind.Event, PluginSetupAssistancePolicy.RequestedEvent, payload, "plugin-setup-assistance", default));
    }

    [Fact]
    public async Task CancelledObligationsAndDisabledInstallationsFailClosed()
    {
        await using var fixture = await Fixture.Create();
        fixture.Obligation.CancelledAt = DateTimeOffset.UtcNow;
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Validate("communication.chat.read.v1", new { chatId = fixture.Obligation.ConversationId }));
        fixture.Installation.SetupState = PluginSetupState.Ready;
        fixture.Installation.IsEnabled = false;
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Validate("communication.chat.read.v1", new { chatId = fixture.Obligation.ConversationId }));
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required CSweetDbContext Db { get; init; }
        public required AgentInstallation Installation { get; init; }
        public required PluginSetupObligation Obligation { get; init; }
        public PluginSetupAssistancePolicy Policy => new(Db);
        public Task Validate(string capability, object input) => Policy.ValidateCapabilityAsync(Installation.BusinessId,
            Installation.Id, capability, JsonSerializer.SerializeToElement(input), default);
        public static async Task<Fixture> Create()
        {
            var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var organization = Guid.NewGuid();
            var installation = new AgentInstallation { Id = Guid.NewGuid(), BusinessId = organization.ToString(),
                PackageVersionId = Guid.NewGuid(), SetupState = PluginSetupState.NeedsSetup };
            var package = new AgentPackageVersion { Id = installation.PackageVersionId, ManifestJson =
                """{"kind":"agent","setup":{"required":true,"assistance":{"profile":"conversation.v1"}}}""" };
            var obligation = new PluginSetupObligation { Id = Guid.NewGuid(), OrganizationId = organization,
                InstallationId = installation.Id, ConversationId = Guid.NewGuid(), AgentOrganizationUserId = Guid.NewGuid(),
                HumanOrganizationUserId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
            db.AddRange(installation, package, obligation); await db.SaveChangesAsync();
            return new Fixture { Db = db, Installation = installation, Obligation = obligation };
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
