using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Application.GenAi;
using CSweet.Application.Communications;
using CSweet.Application.Setup;
using CSweet.Agent.SDK;
using CSweet.AgentHost.Broker;
using CSweet.Contracts.GenAi;
using CSweet.Contracts.Plugins;
using CSweet.Domain.Core;
using CSweet.Domain.Communications;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.GenAi;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class PluginStandingPolicyServiceTests
{
    [Fact]
    public async Task OwnerPolicy_AuthorizesOnlyBoundNonHardGatedActions_AndBindsIdempotency()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ApplicationUserId = applicationUserId,
            DisplayName = "Owner", EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Owner, IsActive = true
        });
        db.AgentInstallations.Add(new AgentInstallation
        {
            Id = installationId, InstallationKey = Guid.NewGuid(), BusinessId = organizationId.ToString("D"),
            PackageVersionId = Guid.NewGuid(), SetupState = PluginSetupState.Ready
        });
        db.PluginConnections.Add(new PluginConnection
        {
            Id = Guid.NewGuid(), AgentInstallationId = installationId, DeclarationId = "youtube",
            ProviderProfile = "google", Status = PluginConnectionStatus.Connected, BoundResourceId = "channel-1"
        });
        db.AgentInstallationConfigurations.Add(new AgentInstallationConfiguration
        {
            Id = Guid.NewGuid(), AgentInstallationId = installationId, SchemaVersion = "1",
            SettingsJson = "{\"approvalMode\":\"Fully Autonomous\"}"
        });
        await db.SaveChangesAsync();
        var service = new PluginStandingPolicyService(db, new TestAuditEventWriter());
        var approved = await service.ApproveAsync(organizationId, applicationUserId, installationId,
            new ApprovePluginStandingPolicyRequest("channel-1", new PluginStandingPolicyDefinition(
                ["CommentReplies", "Publishing"], ["private", "unlisted"], [0, 1, 2, 3, 4, 5, 6],
                0, 24, 10, true, false, ["legal"]), null));
        var payload = JsonSerializer.SerializeToElement(new { text = "Thanks for watching" });
        var hash = Hash(payload.GetRawText());

        var decision = await service.EvaluateAsync(new ManagedActionPolicyInput(organizationId, installationId,
            "channel-1", "reply", payload, hash, "reply-1"));

        Assert.True(decision.Authorized);
        Assert.Equal(approved.Id, decision.PolicyId);
        var hardGate = await service.EvaluateAsync(new ManagedActionPolicyInput(organizationId, installationId,
            "channel-1", "delete-permanently", payload, hash, "delete-1"));
        Assert.False(hardGate.Authorized);
        var changedPayload = JsonSerializer.SerializeToElement(new { text = "Different" });
        var replay = await service.EvaluateAsync(new ManagedActionPolicyInput(organizationId, installationId,
            "channel-1", "reply", changedPayload, Hash(changedPayload.GetRawText()), "reply-1"));
        Assert.False(replay.Authorized);
        Assert.Contains("different content", replay.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonOwner_CannotApproveStandingPolicy()
    {
        await using var db = CreateDb();
        var organizationId = Guid.NewGuid();
        var applicationUserId = Guid.NewGuid();
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, ApplicationUserId = applicationUserId,
            DisplayName = "Manager", EmployeeType = EmployeeType.Human,
            PermissionLevel = OrganizationPermissionLevel.Manager, IsActive = true
        });
        await db.SaveChangesAsync();
        var service = new PluginStandingPolicyService(db, new TestAuditEventWriter());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ApproveAsync(
            organizationId, applicationUserId, Guid.NewGuid(), new ApprovePluginStandingPolicyRequest(
                "channel", new PluginStandingPolicyDefinition(["Publishing"], ["private"], [1],
                    0, 24, 1, false, false, []), null)));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}

public sealed class PluginProviderProfileRegistryTests
{
    [Fact]
    public async Task Profile_EncryptsSecret_AndNeverReturnsItFromAdministrationReads()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var registry = new PluginProviderProfileRegistry(db, new EphemeralDataProtectionProvider(),
            Options.Create(new PluginConnectionOptions()), new TestAuditEventWriter());

        var response = await registry.UpsertAsync("com.example.youtube", new UpsertPluginProviderProfileRequest(
            "YouTube", "https://accounts.example.com/authorize", "https://accounts.example.com/token",
            "https://accounts.example.com/revoke", "client-id", "top-secret"));

        Assert.True(response.HasClientSecret);
        var stored = await db.PluginProviderProfiles.SingleAsync();
        Assert.DoesNotContain("top-secret", stored.ProtectedClientSecret, StringComparison.Ordinal);
        var listed = Assert.Single(await registry.ListAsync());
        Assert.True(listed.HasClientSecret);
        var resolved = await registry.ResolveAsync("com.example.youtube");
        Assert.Equal("top-secret", resolved?.ClientSecret);
    }

    [Fact]
    public async Task Profile_RejectsUnsafeOAuthEndpoints()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var registry = new PluginProviderProfileRegistry(db, new EphemeralDataProtectionProvider(),
            Options.Create(new PluginConnectionOptions()), new TestAuditEventWriter());

        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync("unsafe",
            new UpsertPluginProviderProfileRequest("Unsafe", "http://localhost/authorize",
                "https://localhost/token", null, "client", "secret")));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync("private-network",
            new UpsertPluginProviderProfileRequest("Unsafe", "https://10.0.0.5/authorize",
                "https://id.example.com/token", null, "client", "secret")));
    }
}

public sealed class PluginOAuthFlowTests
{
    [Fact]
    public async Task Authorization_UsesPkceAndSingleUseUserBoundState()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(), AgentId = "test", AgentName = "Test", Version = "1.0.0",
            ManifestJson = ManifestJson, ManifestDigest = new string('a', 64),
            CapabilityDescriptorsDigest = new string('b', 64), RuntimeType = "dotnet-project"
        };
        db.AgentPackageVersions.Add(package);
        db.AgentInstallations.Add(new AgentInstallation
        {
            Id = installationId, InstallationKey = Guid.NewGuid(), PackageVersionId = package.Id,
            BusinessId = organizationId.ToString("D"), SetupState = PluginSetupState.NeedsSetup,
            SetupFlowId = "onboarding", SetupStepId = "connect", PackageVersion = package
        });
        await db.SaveChangesAsync();
        var protection = new EphemeralDataProtectionProvider();
        var secrets = new DataProtectionPluginSecretStore(db, protection);
        var handler = new TokenEndpointHandler();
        var service = new PluginSetupService(db, secrets, protection, new SingleClientFactory(handler),
            new EmptyBootstrap(), new StaticProviderRegistry(), new AgentInstallationConfigurationService(db, new TestAuditEventWriter()),
            new SuccessfulOnboarding(), new TestAuditEventWriter());

        var begin = await service.BeginAuthorizationAsync(organizationId, userId, installationId, "provider",
            new BeginPluginAuthorizationRequest("base"), "https://app.example.com/api/plugin-connections/oauth/callback");
        var query = ParseQuery(new Uri(begin.AuthorizationUrl).Query);

        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        Assert.Equal("scope.read", query["scope"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteAuthorizationAsync(Guid.NewGuid(), "code", query["state"]));

        var completion = await service.CompleteAuthorizationAsync(userId, "code", query["state"]);

        Assert.Equal(installationId, completion.InstallationId);
        Assert.Contains("code_verifier=", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("client_secret=secret", handler.RequestBody, StringComparison.Ordinal);
        Assert.Equal(PluginConnectionStatus.Connected, (await db.PluginConnections.SingleAsync()).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteAuthorizationAsync(userId, "code", query["state"]));
    }

    [Fact]
    public async Task Authorization_RejectsNonHttpsCallback()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var organizationId = Guid.NewGuid();
        var package = new AgentPackageVersion { Id = Guid.NewGuid(), AgentId = "test", AgentName = "Test",
            Version = "1", ManifestJson = ManifestJson, ManifestDigest = new string('a', 64),
            CapabilityDescriptorsDigest = new string('b', 64), RuntimeType = "dotnet-project" };
        var installation = new AgentInstallation { Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(),
            PackageVersionId = package.Id, PackageVersion = package, BusinessId = organizationId.ToString("D"),
            SetupState = PluginSetupState.NeedsSetup, SetupFlowId = "onboarding", SetupStepId = "connect" };
        db.AddRange(package, installation);
        await db.SaveChangesAsync();
        var protection = new EphemeralDataProtectionProvider();
        var service = new PluginSetupService(db, new DataProtectionPluginSecretStore(db, protection), protection,
            new SingleClientFactory(new TokenEndpointHandler()), new EmptyBootstrap(), new StaticProviderRegistry(),
            new AgentInstallationConfigurationService(db, new TestAuditEventWriter()), new SuccessfulOnboarding(), new TestAuditEventWriter());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BeginAuthorizationAsync(organizationId,
            Guid.NewGuid(), installation.Id, "provider", new BeginPluginAuthorizationRequest("base"),
            "http://app.example.com/callback"));
    }

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2))
        .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x.Length > 1 ? x[1] : ""));

    private const string ManifestJson = """
        {"manifestVersion":"2.0","kind":"agent","id":"test","name":"Test","version":"1.0.0",
         "connections":[{"id":"provider","type":"oauth2","providerProfile":"profile",
           "allowedOrigins":["https://api.example.com"],
           "scopeSets":[{"id":"base","label":"Read","purpose":"Read","required":true,"scopes":["scope.read"]}]}],
         "setup":{"required":true,"entryFlow":"onboarding","flows":[{"id":"onboarding","title":"Setup",
           "steps":[{"id":"connect","kind":"oauth-connect","title":"Connect","connection":"provider","scopeSet":"base"}]}]}}
        """;

    private sealed class StaticProviderRegistry : IPluginProviderProfileRegistry
    {
        public Task<PluginOAuthProviderProfile?> ResolveAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<PluginOAuthProviderProfile?>(new("profile", "Provider", "https://id.example.com/auth",
                "https://id.example.com/token", "https://id.example.com/revoke", "client", "secret"));
        public Task<IReadOnlyList<PluginProviderProfileResponse>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PluginProviderProfileResponse>>([]);
        public Task<PluginProviderProfileResponse> UpsertAsync(string id, UpsertPluginProviderProfileRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class EmptyBootstrap : IPluginBootstrapCapabilityService
    {
        public Task<JsonElement> InvokeAsync(Guid organizationId, Guid installationId, string stepId,
            JsonElement arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { }));
    }
    private sealed class SuccessfulOnboarding : IAgentCommunicationOnboardingService
    {
        public Task<AgentCommunicationOnboardingResult> EnsureAsync(Guid organizationId, OrganizationUser agent,
            Guid? hiringApplicationUserId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentCommunicationOnboardingResult(true, null, "Ready"));
    }
    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }
    private sealed class TokenEndpointHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"access\",\"refresh_token\":\"refresh\",\"expires_in\":3600,\"scope\":\"scope.read\"}")
            };
        }
    }
}

public sealed class PluginEngagementNotificationTests
{
    [Fact]
    public async Task Engagement_DeduplicatesUrgentAlertsAndDailyDigestInProtectedConversation()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var organizationId = Guid.NewGuid();
        var installationId = Guid.NewGuid();
        var agentUserId = Guid.NewGuid();
        var humanUserId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.CoreOrganizations.Add(new Organization { Id = organizationId, Name = "Test",
            Status = OrganizationStatus.Active, CreatedAt = now, UpdatedAt = now });
        db.CoreOrganizationUsers.AddRange(
            new OrganizationUser { Id = agentUserId, OrganizationId = organizationId,
                AgentInstallationId = installationId, DisplayName = "YouTube manager",
                EmployeeType = EmployeeType.Agent, PermissionLevel = OrganizationPermissionLevel.Contributor,
                IsActive = true, CreatedAt = now },
            new OrganizationUser { Id = humanUserId, OrganizationId = organizationId,
                ApplicationUserId = Guid.NewGuid(), DisplayName = "Owner", EmployeeType = EmployeeType.Human,
                PermissionLevel = OrganizationPermissionLevel.Owner, IsActive = true, CreatedAt = now });
        db.CoreConversations.Add(new Conversation { Id = conversationId, OrganizationId = organizationId,
            AgentOrganizationUserId = agentUserId, InitiatedByOrganizationUserId = humanUserId,
            IsPrivate = true, IsDeletionProtected = true, CreatedAt = now, UpdatedAt = now });
        db.AgentOnboardingEventOutbox.Add(new AgentOnboardingEventOutboxItem { Id = Guid.NewGuid(),
            OrganizationId = organizationId, AgentOrganizationUserId = agentUserId,
            HiringOrganizationUserId = humanUserId, ConversationId = conversationId,
            Status = AgentOnboardingEventOutboxStatus.Delivered, OccurredAt = now, NextAttemptAt = now });
        db.PluginConnections.Add(new PluginConnection { Id = Guid.NewGuid(), AgentInstallationId = installationId,
            DeclarationId = "youtube", ProviderProfile = "google", Status = PluginConnectionStatus.Connected,
            BoundResourceId = "channel-1", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        var handler = new PluginOperationsCapabilityHandler(db, new TestAuditEventWriter(),
            new PluginStandingPolicyService(db, new TestAuditEventWriter()), new ConversationService(db));
        var session = new AgentSession("session", "youtube", installationId.ToString("D"),
            organizationId.ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(),
                new HashSet<string>([PluginOperationsCapabilityHandler.EngagementInbox]), 1));
        var payload = new
        {
            channelId = "channel-1", source = "youtube",
            items = new[] { new { externalId = "comment-1", urgent = true,
                excerpt = "A legal escalation was mentioned.", payload = new { text = "details" } } },
            digest = new { total = 1, urgent = 1 }
        };

        await InvokeAsync(handler, session, payload);
        await InvokeAsync(handler, session, payload);

        var messages = await db.CoreConversationMessages.OrderBy(x => x.CreatedAt).ToListAsync();
        Assert.Equal(2, messages.Count);
        Assert.Single(messages, x => x.Content.Contains("Potentially urgent", StringComparison.Ordinal));
        Assert.Single(messages, x => x.Content.Contains("daily engagement digest", StringComparison.Ordinal));
    }

    private static async Task InvokeAsync(PluginOperationsCapabilityHandler handler, AgentSession session,
        object payload)
    {
        var request = new RequestCapability { RequestId = Guid.NewGuid().ToString("N"),
            Capability = PluginOperationsCapabilityHandler.EngagementInbox,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(payload)) };
        await foreach (var result in handler.HandleAsync(session, request, CancellationToken.None))
            Assert.True(result.Succeeded, result.Error);
    }
}

public sealed class PluginAgentApproverTests
{
    [Fact]
    public async Task AssignedAgentManager_CanDecideExactProposalIdempotently()
    {
        await using var db = new CSweetDbContext(new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var organizationId = Guid.NewGuid();
        var requesterInstallationId = Guid.NewGuid();
        var approverInstallationId = Guid.NewGuid();
        var approverUserId = Guid.NewGuid();
        var proposalId = Guid.NewGuid();
        db.CoreOrganizationUsers.AddRange(
            new OrganizationUser { Id = approverUserId, OrganizationId = organizationId,
                AgentInstallationId = approverInstallationId, DisplayName = "Manager agent",
                EmployeeType = EmployeeType.Agent, PermissionLevel = OrganizationPermissionLevel.Manager,
                IsActive = true },
            new OrganizationUser { Id = Guid.NewGuid(), OrganizationId = organizationId,
                AgentInstallationId = requesterInstallationId, ReportsToOrganizationUserId = approverUserId,
                DisplayName = "YouTube agent", EmployeeType = EmployeeType.Agent,
                PermissionLevel = OrganizationPermissionLevel.Contributor, IsActive = true });
        db.ActionProposals.Add(new ActionProposal
        {
            Id = proposalId, OrganizationId = organizationId, AgentInstallationId = requesterInstallationId,
            ActionType = "youtube.update", Summary = "Update video", IdempotencyKey = "action-1",
            PayloadJson = JsonSerializer.Serialize(new { installationId = requesterInstallationId.ToString("D"),
                channelId = "channel-1", actionType = "update", payload = new { title = "New title" },
                payloadHash = new string('a', 64), idempotencyKey = "action-1", approvalId = (string?)null,
                expectedRevision = 3, alwaysRequiresApproval = false, resourceId = "video-1" }),
            Status = ProposalStatus.Pending, CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var handler = new PluginOperationsCapabilityHandler(db, new TestAuditEventWriter(),
            new PluginStandingPolicyService(db, new TestAuditEventWriter()), new ConversationService(db));
        var session = new AgentSession("session", "manager", approverInstallationId.ToString("D"),
            organizationId.ToString("D"), Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new AuthorizedAgentGrant(new HashSet<string>(), new HashSet<string>(),
                new HashSet<string>([PluginOperationsCapabilityHandler.ManagedActionDecide]), 1));
        var decision = new { proposalId, decision = "Approve", comment = (string?)null,
            payloadHash = new string('a', 64), expectedRevision = 3, actionIdempotencyKey = "action-1",
            decisionIdempotencyKey = "decision-1", resourceId = "video-1" };

        var first = await InvokeAsync(handler, session, decision);
        var replay = await InvokeAsync(handler, session, decision);

        Assert.True(first.Succeeded, first.Error);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.Equal(ProposalStatus.Approved, (await db.ActionProposals.SingleAsync()).Status);
        Assert.Single(await db.PluginOperationalStates.Where(x => x.Kind == "managed-action-decision").ToListAsync());
        using var replayBody = JsonDocument.Parse(replay.Payload.ToByteArray());
        Assert.True(replayBody.RootElement.GetProperty("idempotent").GetBoolean());
    }

    private static async Task<CapabilityResult> InvokeAsync(PluginOperationsCapabilityHandler handler,
        AgentSession session, object payload)
    {
        var request = new RequestCapability { RequestId = Guid.NewGuid().ToString("N"),
            Capability = PluginOperationsCapabilityHandler.ManagedActionDecide,
            Payload = JsonPayload.From(JsonSerializer.SerializeToUtf8Bytes(payload)) };
        var values = new List<CapabilityResult>();
        await foreach (var result in handler.HandleAsync(session, request, CancellationToken.None)) values.Add(result);
        return Assert.Single(values);
    }
}

public sealed class ResumableMediaUploadServiceTests
{
    [Fact]
    public async Task Upload_ResumesAtExactOffset_AndCompletesValidatedAsset()
    {
        await using var db = CreateDb();
        var organizationId = await AddOrganizationAsync(db);
        var bytes = new byte[70_000];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var temporary = new MemoryUploadStore();
        var service = CreateService(db, temporary);
        var session = await service.CreateAsync(organizationId,
            new CreateMediaUploadSessionRequest("thumbnail.png", "image/png", bytes.Length, expectedHash));

        await service.AppendAsync(organizationId, session.Id, 0, 65_536,
            new MemoryStream(bytes, 0, 65_536, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AppendAsync(organizationId,
            session.Id, 1, bytes.Length - 65_536, new MemoryStream(bytes, 65_536, bytes.Length - 65_536, false)));
        await service.AppendAsync(organizationId, session.Id, 65_536, bytes.Length - 65_536,
            new MemoryStream(bytes, 65_536, bytes.Length - 65_536, false));

        var completed = await service.CompleteAsync(organizationId, session.Id);

        Assert.Equal("Completed", completed.Status);
        Assert.NotNull(completed.Asset);
        Assert.Equal(expectedHash, completed.Asset.Sha256);
        Assert.False(temporary.Exists(session.Id));
    }

    [Fact]
    public async Task Upload_ChecksumFailure_IsFailedAndTemporaryDataIsPurged()
    {
        await using var db = CreateDb();
        var organizationId = await AddOrganizationAsync(db);
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        var temporary = new MemoryUploadStore();
        var service = CreateService(db, temporary);
        var session = await service.CreateAsync(organizationId,
            new CreateMediaUploadSessionRequest("thumbnail.png", "image/png", bytes.Length, new string('0', 64)));
        await service.AppendAsync(organizationId, session.Id, 0, bytes.Length, new MemoryStream(bytes));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteAsync(organizationId, session.Id));

        Assert.Equal(MediaUploadSessionStatus.Failed,
            (await db.MediaUploadSessions.SingleAsync(x => x.Id == session.Id)).Status);
        Assert.False(temporary.Exists(session.Id));
    }

    [Fact]
    public async Task Upload_RejectsOrganizationQuotaReservation()
    {
        await using var db = CreateDb();
        var organizationId = await AddOrganizationAsync(db);
        var options = Options.Create(new MediaAssetStorageOptions
        {
            MaximumFileSizeBytes = 100, MaximumOrganizationStorageBytes = 10,
            ResumableChunkSizeBytes = 65_536, UploadSessionLifetimeHours = 1
        });
        var service = new ResumableMediaUploadService(db, new MemoryUploadStore(),
            new MediaAssetService(db, new MemoryAssetStore()), options, new TestAuditEventWriter());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(organizationId,
            new CreateMediaUploadSessionRequest("video.mp4", "video/mp4", 11)));
    }

    private static ResumableMediaUploadService CreateService(CSweetDbContext db, MemoryUploadStore temporary)
    {
        var options = Options.Create(new MediaAssetStorageOptions
        {
            MaximumFileSizeBytes = 1024 * 1024, MaximumOrganizationStorageBytes = 2 * 1024 * 1024,
            ResumableChunkSizeBytes = 65_536, UploadSessionLifetimeHours = 1
        });
        return new ResumableMediaUploadService(db, temporary, new MediaAssetService(db, new MemoryAssetStore()),
            options, new TestAuditEventWriter());
    }

    private static async Task<Guid> AddOrganizationAsync(CSweetDbContext db)
    {
        var id = Guid.NewGuid();
        db.CoreOrganizations.Add(new Organization { Id = id, Name = "Test", CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return id;
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class MemoryUploadStore : IResumableMediaUploadStore
    {
        private readonly Dictionary<Guid, byte[]> _values = [];
        public bool Exists(Guid id) => _values.ContainsKey(id);
        public Task CreateAsync(Guid sessionId, CancellationToken cancellationToken = default)
        { _values.Add(sessionId, []); return Task.CompletedTask; }
        public async Task AppendAsync(Guid sessionId, long committedLength, long contentLength, Stream content,
            CancellationToken cancellationToken = default)
        {
            var current = _values[sessionId];
            if (current.LongLength < committedLength) throw new InvalidOperationException();
            await using var output = new MemoryStream();
            await output.WriteAsync(current.AsMemory(0, (int)committedLength), cancellationToken);
            var buffer = new byte[contentLength];
            await content.ReadExactlyAsync(buffer, cancellationToken);
            await output.WriteAsync(buffer, cancellationToken);
            _values[sessionId] = output.ToArray();
        }
        public Task<long> GetLengthAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values[sessionId].LongLength);
        public Task<Stream> OpenReadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(_values[sessionId], false));
        public Task DeleteAsync(Guid sessionId, CancellationToken cancellationToken = default)
        { _values.Remove(sessionId); return Task.CompletedTask; }
    }

    private sealed class MemoryAssetStore : IMediaAssetStore
    {
        private readonly Dictionary<string, byte[]> _values = [];
        public async Task<(string StorageKey, long SizeBytes, string Sha256)> SaveAsync(string fileName,
            Stream content, CancellationToken cancellationToken = default)
        {
            await using var output = new MemoryStream();
            await content.CopyToAsync(output, cancellationToken);
            var bytes = output.ToArray();
            var key = Guid.NewGuid().ToString("N");
            _values[key] = bytes;
            return (key, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(_values[storageKey], false));
        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken = default)
        { _values.Remove(storageKey); return Task.CompletedTask; }
    }
}
