using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Contracts.Core;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Core;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class AgentDefinitionLifecycleTests
{
    [Fact]
    public async Task Import_QueuesOnlyBuilderAndCreatesNoBusinessOrRuntimeRows()
    {
        await using var db = CreateDb();
        var package = SeedPackage(db, AgentPackageVersionStatus.Previewed, requiredConfiguration: false);
        await db.SaveChangesAsync();

        var definition = await new AgentDefinitionService(db, new TestAuditEventWriter(), new RecordingBuildService(db))
            .ImportAsync(package.Id, Request("AlwaysOn"));

        Assert.Equal(AgentDefinitionStatus.Building.ToString(), definition.Status);
        Assert.Single(await db.AgentDefinitions.ToListAsync());
        Assert.Single(await db.AgentBuildJobs.ToListAsync());
        Assert.Empty(await db.AgentInstallations.ToListAsync());
        Assert.Empty(await db.AgentSchedules.ToListAsync());
        Assert.Empty(await db.AgentRuntimeInstances.ToListAsync());
    }

    [Fact]
    public async Task BuiltDefinition_WithMissingRequiredDefault_IsNotHireable()
    {
        await using var db = CreateDb();
        var package = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: true);
        package.PackageDigest = $"sha256:{new string('a', 64)}";
        package.ArtifactSignature = "test-signature";
        await db.SaveChangesAsync();

        var definition = await new AgentDefinitionService(db, new TestAuditEventWriter(), new RecordingBuildService(db))
            .ImportAsync(package.Id, Request("OnDemand"));

        Assert.False(definition.IsAvailableForHire);
        Assert.Equal(AgentDefinitionStatus.NeedsConfiguration.ToString(), definition.Status);
        Assert.Empty(await db.AgentInstallations.ToListAsync());
    }

    [Fact]
    public async Task RetryBuild_QueuesTheDefinitionsPackageInsteadOfLookingForAnInstallation()
    {
        await using var db = CreateDb();
        var package = SeedPackage(db, AgentPackageVersionStatus.Failed, requiredConfiguration: false);
        var definition = SeedDefinition(db, package, ActivationMode.AlwaysOn);
        definition.Status = AgentDefinitionStatus.BuildFailed;
        definition.IsAvailableForHire = false;
        var failedJob = new AgentBuildJob
        {
            Id = Guid.NewGuid(), PackageVersionId = package.Id, PackageVersion = package,
            Attempt = 1, QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        failedJob.TransitionTo(AgentBuildStatus.Failed, DateTimeOffset.UtcNow);
        db.AgentBuildJobs.Add(failedJob);
        await db.SaveChangesAsync();
        var builds = new RecordingBuildService(db);
        var service = new AgentDefinitionService(db, new TestAuditEventWriter(), builds);

        var result = await service.RetryBuildAsync(definition.Id);

        Assert.Equal(package.Id, builds.QueuedPackageVersionId);
        Assert.Equal(AgentDefinitionStatus.Building.ToString(), result.Status);
        Assert.False(result.IsAvailableForHire);
        Assert.Equal("Queued", result.Build?.Status);
        Assert.Equal(2, result.Build?.Attempt);
        Assert.Empty(await db.AgentInstallations.ToListAsync());
    }

    [Theory]
    [InlineData(ActivationMode.AlwaysOn, 1)]
    [InlineData(ActivationMode.Scheduled, 0)]
    [InlineData(ActivationMode.OnDemand, 0)]
    public async Task Hiring_CreatesFreshBusinessInstallation_AndStartsOnlyAlwaysOn(
        ActivationMode activationMode, int expectedRuntimeRequests)
    {
        await using var db = CreateDb();
        var package = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        package.PackageDigest = $"sha256:{new string('b', 64)}";
        package.ArtifactSignature = "test-signature";
        var definition = SeedDefinition(db, package, activationMode);
        var organization = new Organization { Id = Guid.NewGuid(), Name = "Example", CreatedAt = DateTimeOffset.UtcNow };
        var manager = new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organization.Id, DisplayName = "Owner",
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Owner,
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(organization, manager);
        await db.SaveChangesAsync();
        var runtimes = new RecordingRuntimeManager();
        var service = new OrganizationUserService(db, new TestAuditEventWriter(), agentRuntimeManager: runtimes);

        var result = await service.CreateAsync(organization.Id, new CreateOrganizationUserRequest(
            "Agent", null, (int)OrganizationPermissionLevel.Contributor, (int)EmployeeType.Agent,
            ReportsToOrganizationUserId: manager.Id, AgentDefinitionId: definition.Id));

        Assert.True(result.Succeeded, result.Message);
        var installation = await db.AgentInstallations.Include(x => x.Schedule).Include(x => x.Configuration).SingleAsync();
        Assert.Equal(organization.Id.ToString("D"), installation.BusinessId);
        Assert.Equal(definition.Id, installation.AgentDefinitionId);
        Assert.Equal(activationMode, installation.Schedule!.ActivationMode);
        Assert.Equal("{}", installation.Configuration!.SettingsJson);
        Assert.Equal(installation.Id, result.OrganizationUser!.AgentInstallationId);
        Assert.Equal(expectedRuntimeRequests, runtimes.RequestCount);
        Assert.Empty(await db.AgentRuntimeInstances.ToListAsync());
    }

    [Fact]
    public async Task RuntimeEligibility_RejectsUnassignedAgents_ButAllowsSystemServices()
    {
        await using var db = CreateDb();
        var agentPackage = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        agentPackage.PackageDigest = $"sha256:{new string('e', 64)}";
        agentPackage.ArtifactSignature = "agent-signature";
        var definition = SeedDefinition(db, agentPackage, ActivationMode.AlwaysOn);
        var agentInstallation = RuntimeInstallation(agentPackage, "00000000-0000-0000-0000-000000000001");
        agentInstallation.AgentDefinitionId = definition.Id;
        agentInstallation.AgentDefinition = definition;
        definition.Installations.Add(agentInstallation);

        var servicePackage = SeedPackage(db, AgentPackageVersionStatus.Built, requiredConfiguration: false);
        servicePackage.AgentId = "com.example.system-service";
        servicePackage.PluginKind = PluginKind.Service;
        servicePackage.PackageDigest = $"sha256:{new string('f', 64)}";
        servicePackage.ArtifactSignature = "service-signature";
        var serviceInstallation = RuntimeInstallation(servicePackage, "system");
        serviceInstallation.Scope = PluginInstallationScope.System;
        db.AgentInstallations.AddRange(agentInstallation, serviceInstallation);
        await db.SaveChangesAsync();
        var configurations = new AgentInstallationConfigurationService(db, new TestAuditEventWriter());
        var eligibility = new AgentRuntimeEligibilityService(db, configurations);

        var denied = await eligibility.EvaluateAsync(agentInstallation.Id);
        var allowed = await eligibility.EvaluateAsync(serviceInstallation.Id);

        Assert.False(denied.IsEligible);
        Assert.Contains("active hired employee", denied.Reason);
        Assert.True(allowed.IsEligible);
        Assert.True(allowed.IsSystemService);
    }

    private static AgentPackageVersion SeedPackage(
        CSweetDbContext db, AgentPackageVersionStatus status, bool requiredConfiguration)
    {
        var source = new AgentPackageSource
        {
            Id = Guid.NewGuid(), RepositoryUrl = "https://github.com/example/agent",
            RepositoryOwner = "example", RepositoryName = "agent", DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        object[] configuration = requiredConfiguration
            ? [new { key = "apiRegion", type = "text", label = "API region", required = true, secret = false }]
            : [];
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(), PackageSourceId = source.Id, PackageSource = source,
            AgentId = "com.example.agent", AgentName = "Example Agent", Version = "1.0.0",
            PublisherId = "example", PublisherName = "Example", RuntimeType = "dotnet-project",
            CommitSha = new string('c', 40), ManifestDigest = new string('d', 64),
            ManifestJson = JsonSerializer.Serialize(new
            {
                manifestVersion = "2.0", kind = "agent", id = "com.example.agent",
                name = "Example Agent", version = "1.0.0",
                publisher = new { id = "example", name = "Example" },
                runtime = new { type = "dotnet-project", projectPath = "src/Agent.csproj", targetFramework = "net10.0", defaultActivationMode = "OnDemand" },
                protocol = new { minimumVersion = "2.0", maximumVersion = "2.x" },
                provides = Array.Empty<object>(), requires = Array.Empty<object>(),
                events = new { subscribes = Array.Empty<string>() }, configuration,
                credentials = Array.Empty<object>(), webAccess = new { mode = "None", rules = Array.Empty<object>() }
            }),
            Status = status, ImportedAt = DateTimeOffset.UtcNow
        };
        db.AddRange(source, package);
        return package;
    }

    private static AgentDefinition SeedDefinition(
        CSweetDbContext db, AgentPackageVersion package, ActivationMode activationMode)
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new AgentDefinition
        {
            Id = Guid.NewGuid(), PackageSourceId = package.PackageSourceId, AgentId = package.AgentId,
            PackageVersionId = package.Id, PackageVersion = package, Status = AgentDefinitionStatus.Available,
            IsAvailableForHire = true, DefaultActivationMode = activationMode,
            DefaultTickFrequencySeconds = 3600, DefaultOverlapPolicy = OverlapPolicy.Skip,
            DefaultMaxRuntimeSeconds = 600, DefaultMemoryMb = 1024, DefaultCpuPercent = 50,
            CreatedAt = now, UpdatedAt = now
        };
        definition.Configuration = new AgentDefinitionConfiguration
        {
            Id = Guid.NewGuid(), AgentDefinitionId = definition.Id, SchemaVersion = "1",
            SettingsJson = "{}", Revision = 1, CreatedAt = now, UpdatedAt = now
        };
        db.AgentDefinitions.Add(definition);
        return definition;
    }

    private static AgentInstallation RuntimeInstallation(AgentPackageVersion package, string businessId)
    {
        var now = DateTimeOffset.UtcNow;
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(), InstallationKey = Guid.NewGuid(), PackageVersionId = package.Id,
            PackageVersion = package, BusinessId = businessId, IsEnabled = true,
            RevisionStatus = PluginRevisionStatus.Active, SetupState = PluginSetupState.Ready,
            CreatedAt = now, UpdatedAt = now
        };
        installation.Schedule = new AgentSchedule
        {
            Id = Guid.NewGuid(), AgentInstallationId = installation.Id,
            ActivationMode = ActivationMode.AlwaysOn, TickFrequencySeconds = 60,
            MaxRuntimeSeconds = 600, OverlapPolicy = OverlapPolicy.Skip, IsEnabled = true
        };
        installation.Grant = new AgentInstallationGrant
        {
            Id = Guid.NewGuid(), AgentInstallationId = installation.Id, MaxRuntimeSeconds = 600,
            MemoryMb = 1024, CpuPercent = 50, ApprovedAt = now
        };
        installation.Configuration = new AgentInstallationConfiguration
        {
            Id = Guid.NewGuid(), AgentInstallationId = installation.Id, SchemaVersion = "1",
            SettingsJson = "{}", Revision = 1, CreatedAt = now, UpdatedAt = now
        };
        return installation;
    }

    private static InstallAgentRequest Request(string activationMode) => new(
        "ignored-global-definition", activationMode, 3600, "Skip", [], [], [], [], [], 600, 1024, 50);

    private static CSweetDbContext CreateDb() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed class RecordingRuntimeManager : IAgentRuntimeManager
    {
        public int RequestCount { get; private set; }
        public Task<bool> EnsureRuntimeQueuedAsync(Guid installationId, string reason, bool interactive = false,
            CancellationToken cancellationToken = default)
        { RequestCount++; return Task.FromResult(true); }
        public Task<bool> RestartRuntimeAsync(Guid installationId, string reason, bool interactive = false,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> EnsureAlwaysOnRuntimesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> ProcessDueSchedulesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> ReconcileAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class RecordingBuildService(CSweetDbContext db) : IAgentBuildService
    {
        public Guid? QueuedPackageVersionId { get; private set; }

        public async Task<Guid> QueueAsync(
            Guid packageVersionId,
            CancellationToken cancellationToken = default)
        {
            QueuedPackageVersionId = packageVersionId;
            var package = await db.AgentPackageVersions.Include(x => x.BuildJobs)
                .SingleAsync(x => x.Id == packageVersionId, cancellationToken);
            var job = new AgentBuildJob
            {
                Id = Guid.NewGuid(),
                PackageVersionId = packageVersionId,
                PackageVersion = package,
                Attempt = (package.BuildJobs.Max(x => (int?)x.Attempt) ?? 0) + 1,
                QueuedAt = DateTimeOffset.UtcNow
            };
            package.Status = AgentPackageVersionStatus.Approved;
            db.AgentBuildJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            return job.Id;
        }

        public Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
