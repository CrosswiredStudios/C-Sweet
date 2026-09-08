using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class LegacyPluginActionBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerDefinedAuthorization_IsRejectedEvenWithPriorApprovalOrAutonomousPolicy(bool autonomous)
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var organization = Guid.NewGuid();
        var installation = Guid.NewGuid();
        var payload = JsonSerializer.SerializeToElement(new { text = "A reply" });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.GetRawText()))).ToLowerInvariant();
        var proposed = new
        {
            installationId = installation.ToString("D"), channelId = "account-1", actionType = "reply",
            payload, payloadHash = hash, idempotencyKey = "old-action", approvalId = (string?)null,
            expectedRevision = (long?)null, alwaysRequiresApproval = false, resourceId = "comment-1"
        };
        db.PluginConnections.Add(new PluginConnection
        {
            Id = Guid.NewGuid(), AgentInstallationId = installation, DeclarationId = "provider",
            ProviderProfile = "profile", Status = PluginConnectionStatus.Connected, BoundResourceId = "account-1"
        });
        db.AgentInstallationConfigurations.Add(new AgentInstallationConfiguration
        {
            Id = Guid.NewGuid(), AgentInstallationId = installation, SchemaVersion = "1",
            SettingsJson = JsonSerializer.Serialize(new { approvalMode = autonomous ? "Fully Autonomous" : "Manager Approval" })
        });
        db.PluginStandingPolicies.Add(new PluginStandingPolicy
        {
            Id = Guid.NewGuid(), OrganizationId = organization, AgentInstallationId = installation,
            ChannelId = "account-1", Revision = 1, Status = PluginStandingPolicyStatus.Approved,
            ApprovedByOrganizationUserId = Guid.NewGuid(), PayloadHash = hash,
            PolicyJson = JsonSerializer.Serialize(new
            {
                allowedActionCategories = new[] { "CommentReplies" }, allowedPrivacyValues = new[] { "private" },
                allowedUtcDays = new[] { 0, 1, 2, 3, 4, 5, 6 }, allowedUtcStartHour = 0, allowedUtcEndHour = 24,
                maximumActionsPerHour = 10, allowReplies = true, allowModeration = false, escalationKeywords = Array.Empty<string>()
            })
        });
        db.ActionProposals.Add(new ActionProposal
        {
            Id = Guid.NewGuid(), OrganizationId = organization, AgentInstallationId = installation,
            ActionType = "youtube.reply", Summary = "Historical reply", IdempotencyKey = "old-action",
            PayloadJson = JsonSerializer.Serialize(proposed), Status = ProposalStatus.Approved
        });
        await db.SaveChangesAsync();
        var handler = new PluginOperationsCapabilityHandler(db, new TestAuditEventWriter());
        var session = Session(organization, installation, PluginOperationsCapabilityHandler.ManagedAction);

        var result = await Invoke(handler, session, PluginOperationsCapabilityHandler.ManagedAction, proposed);
        var replay = await Invoke(handler, session, PluginOperationsCapabilityHandler.ManagedAction, proposed);

        Assert.False(result.Succeeded);
        Assert.False(replay.Succeeded);
        Assert.Equal(PlatformCapabilityErrorCode.Denied.ToString(), result.FailureCode);
        Assert.Contains("exact approved action", result.Error);
        Assert.Single(await db.ActionProposals.ToArrayAsync());
        Assert.Empty(await db.PluginOperationalStates.ToArrayAsync());
        Assert.Empty(await db.AgentWorkItems.ToArrayAsync());
        Assert.Empty(await db.CoreConversationMessages.ToArrayAsync());
        Assert.All(db.ChangeTracker.Entries(), x => Assert.Equal(EntityState.Unchanged, x.State));
    }

    [Fact]
    public async Task UnknownCapability_CannotFallThroughToCheckpointStorage()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var handler = new PluginOperationsCapabilityHandler(db, new TestAuditEventWriter());
        const string capability = "platform.unregistered.write.v1";
        var result = await Invoke(handler, Session(Guid.NewGuid(), Guid.NewGuid(), capability), capability,
            new { source = "unexpected", data = "must not persist" });
        Assert.False(handler.CanHandle(capability));
        Assert.False(result.Succeeded);
        Assert.Equal(PlatformCapabilityErrorCode.Denied.ToString(), result.FailureCode);
        Assert.Empty(await db.PluginOperationalStates.ToArrayAsync());
    }

    private static AgentSession Session(Guid organization, Guid installation, string capability) =>
        new("session", "test", installation.ToString("D"), organization.ToString("D"),
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(), new HashSet<string>([capability]), 1));

    private static async Task<CapabilityResult> Invoke(PluginOperationsCapabilityHandler handler, AgentSession session,
        string capability, object payload)
    {
        var results = new List<CapabilityResult>();
        var request = new RequestCapability { RequestId = Guid.NewGuid().ToString("N"), Capability = capability,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(payload)) };
        await foreach (var result in handler.HandleAsync(session, request, CancellationToken.None)) results.Add(result);
        return Assert.Single(results);
    }
}
