using CSweet.Domain.Setup;
using CSweet.Application.Communications;
using CSweet.Api.Communications;
using CSweet.Application.Setup;
using CSweet.Communications.Abstractions;
using CSweet.Contracts.Communications;
using CSweet.Domain.Communications;
using CSweet.Infrastructure.Communications;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CSweet.UnitTests;

public sealed class PluginPlatformTests
{
    [Fact]
    public void CommunicationRuntime_ResolvesDurableEventPublisher()
    {
        var services = new ServiceCollection();
        services.AddDbContext<CSweetDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton(TimeProvider.System);
        services.AddCommunicationPluginRuntime();
        services.AddScoped<ICommunicationEventPublisher, DurableCommunicationEventPublisher>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();

        Assert.IsType<DurableCommunicationEventPublisher>(
            scope.ServiceProvider.GetRequiredService<ICommunicationEventPublisher>());
    }

    [Fact]
    public void ManifestReader_RejectsLegacyManifest()
    {
        var manifest = """{"id":"com.example.agent","name":"Example","version":"1.0.0"}"""u8.ToArray();

        Assert.Throws<System.Text.Json.JsonException>(() =>
            new PluginManifestReader().Read(manifest, "csweet-agent.json"));
    }

    [Fact]
    public void ManifestReader_ReadsServiceKind()
    {
        var manifest = """{"manifestVersion":"2.0","kind":"service","id":"com.example.chat","name":"Chat","version":"1.0.0","protocol":{"minimumVersion":"2.0","maximumVersion":"2.x"}}"""u8.ToArray();

        var result = new PluginManifestReader().Read(manifest, "csweet-plugin.json");

        Assert.Equal("service", result.Kind);
    }

    [Fact]
    public void ManifestReader_AcceptsLongRunningCapabilityWithinSdkLimit()
    {
        var manifest = """
            {
              "manifestVersion": "2.0",
              "kind": "agent",
              "id": "com.example.software-developer",
              "name": "Software Developer",
              "version": "1.0.0",
              "catalog": {
                "role": { "key": "software-developer", "name": "Software Developer" },
                "license": { "spdxId": "Apache-2.0" },
                "iconUrls": ["https://example.com/software-developer.png"]
              },
              "protocol": { "minimumVersion": "2.0", "maximumVersion": "2.x" },
              "provides": [{
                "name": "work.execution.run.v1",
                "description": "Execute one workflow stage.",
                "inputSchema": { "type": "object" },
                "outputSchema": { "type": "object" },
                "executionTimeoutSeconds": 3600,
                "idempotency": "work-item"
              }]
            }
            """u8.ToArray();

        var result = new PluginManifestReader().Read(manifest, "csweet-plugin.json");

        Assert.Equal("agent", result.Kind);
    }

    [Fact]
    public async Task SecretStore_EncryptsPersistedValueAndRoundTripsPlaintext()
    {
        await using var db = CreateDb();
        var installationId = Guid.NewGuid();
        db.AgentInstallations.Add(new AgentInstallation
        {
            Id = installationId, PackageVersionId = Guid.NewGuid(), BusinessId = "enterprise",
            Scope = PluginInstallationScope.System, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var services = new ServiceCollection().AddDataProtection().UseEphemeralDataProtectionProvider().Services.BuildServiceProvider();
        var store = new DataProtectionPluginSecretStore(db, services.GetRequiredService<IDataProtectionProvider>());

        await store.SetAsync(installationId, "BOT_TOKEN", "top-secret-value");

        var persisted = await db.PluginSecrets.SingleAsync();
        Assert.DoesNotContain("top-secret-value", persisted.ProtectedValue, StringComparison.Ordinal);
        Assert.Equal("top-secret-value", await store.GetAsync(installationId, "BOT_TOKEN"));
    }

    [Fact]
    public async Task SystemPlugin_CannotAccessOrganizationWithoutServerGrant()
    {
        await using var db = CreateDb();
        var installationId = Guid.NewGuid();
        var allowedOrganization = Guid.NewGuid();
        var deniedOrganization = Guid.NewGuid();
        db.AgentInstallations.Add(new AgentInstallation
        {
            Id = installationId, PackageVersionId = Guid.NewGuid(), BusinessId = "enterprise", IsEnabled = true,
            Scope = PluginInstallationScope.System, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        db.PluginOrganizationGrants.Add(new PluginOrganizationGrant
        {
            Id = Guid.NewGuid(), PluginInstallationId = installationId, OrganizationId = allowedOrganization,
            GrantedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var policy = new PersistedPluginAuthorizationPolicy(db);

        Assert.True(await policy.CanAccessOrganizationAsync(installationId, allowedOrganization));
        Assert.False(await policy.CanAccessOrganizationAsync(installationId, deniedOrganization));
    }

    [Fact]
    public async Task CommunicationIngress_IsIdempotentPerInstallationAndProviderKey()
    {
        await using var db = CreateDb();
        var installationId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        db.CommunicationConnections.Add(new CommunicationConnection
        {
            Id = Guid.NewGuid(), PluginInstallationId = installationId, OrganizationId = organizationId,
            ProviderKey = "fake", WorkspaceExternalId = "workspace-1", WorkspaceMode = CommunicationWorkspaceMode.Dedicated,
            Status = CommunicationConnectionStatus.Connected, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var router = new CountingRouter();
        var handler = new CommunicationIngressHandler(db, new AllowOrganization(), router);
        var envelope = new NormalizedCommunicationEnvelope(Guid.NewGuid(), "fake", CommunicationEnvelopeKind.Message,
            "workspace-1", "channel-1", null, "user-1", "message-1", null, "hello", [], false, false,
            DateTimeOffset.UtcNow, "provider-idempotency-key");

        var first = await handler.IngestAsync(installationId, organizationId, envelope);
        var duplicate = await handler.IngestAsync(installationId, organizationId, envelope);

        Assert.True(first.Succeeded);
        Assert.True(duplicate.Succeeded);
        Assert.Equal(1, router.Calls);
        Assert.Single(await db.CommunicationIngressReceipts.ToListAsync());
    }

    private static CSweetDbContext CreateDb() => new(new DbContextOptionsBuilder<CSweetDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class AllowOrganization : IPluginAuthorizationPolicy
    {
        public Task<bool> CanAccessOrganizationAsync(Guid installationId, Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class CountingRouter : ICommunicationRouter
    {
        public int Calls { get; private set; }
        public Task<CommunicationActionResponse> RouteInboundAsync(NormalizedCommunicationEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new CommunicationActionResponse(true, null, "accepted"));
        }
    }
}
