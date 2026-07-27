using System.Text.Json;
using CSweet.Application.Setup;
using CSweet.Contracts.Agents;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CSweet.UnitTests;

public sealed class AgentInstallationServiceTests
{
    [Fact]
    public async Task InstallAsync_RejectsGrantBroaderThanManifest()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var request = ValidRequest() with
        {
            GrantedCapabilities = ["research.execute.v1", "admin.delete.v1"]
        };

        var exception = await Assert.ThrowsAsync<AgentInstallationException>(() =>
            service.InstallAsync(package.Id, request));

        Assert.Contains("manifest did not request", exception.Message);
        Assert.Empty(await dbContext.AgentInstallations.ToListAsync());
    }

    [Fact]
    public async Task InstallAsync_RejectsTickFrequencyBelowGlobalMinimum()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var service = CreateService(dbContext);

        var exception = await Assert.ThrowsAsync<AgentInstallationException>(() =>
            service.InstallAsync(package.Id, ValidRequest() with { TickFrequencySeconds = 299 }));

        Assert.Contains("at least 300 seconds", exception.Message);
        Assert.Empty(await dbContext.AgentInstallations.ToListAsync());
    }

    [Fact]
    public async Task InstallAsync_ClampsMaxRuntimeToGlobalLimitAndLogsWarning()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var logger = new RecordingLogger<AgentInstallationService>();
        var service = CreateService(dbContext, logger: logger);

        var result = await service.InstallAsync(
            package.Id,
            ValidRequest() with { MaxRuntimeSeconds = 86_400 });

        Assert.Equal(600, result.Schedule.MaxRuntimeSeconds);
        var grant = await dbContext.AgentInstallationGrants.SingleAsync();
        var schedule = await dbContext.AgentSchedules.SingleAsync();
        Assert.Equal(600, grant.MaxRuntimeSeconds);
        Assert.Equal(600, schedule.MaxRuntimeSeconds);
        using var limits = JsonDocument.Parse(grant.ResourceLimitsJson);
        Assert.Equal(600, limits.RootElement.GetProperty("MaxRuntimeSeconds").GetInt32());
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("86400", StringComparison.Ordinal) &&
            entry.Message.Contains("600", StringComparison.Ordinal) &&
            entry.Message.Contains(package.AgentId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallAsync_RejectsNonPositiveMaxRuntime()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var service = CreateService(dbContext);

        var exception = await Assert.ThrowsAsync<AgentInstallationException>(() =>
            service.InstallAsync(package.Id, ValidRequest() with { MaxRuntimeSeconds = 0 }));

        Assert.Contains("greater than zero", exception.Message);
        Assert.Empty(await dbContext.AgentInstallations.ToListAsync());
    }

    [Fact]
    public async Task UpdateScheduleAsync_ClampsMaxRuntimeToGlobalLimit()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var logger = new RecordingLogger<AgentInstallationService>();
        var service = CreateService(dbContext, logger: logger);
        var installation = await service.InstallAsync(package.Id, ValidRequest());

        var result = await service.UpdateScheduleAsync(
            installation.Id,
            new UpdateAgentScheduleRequest("Periodic", 900, "Skip", 86_400, true));

        Assert.Equal(600, result.Schedule.MaxRuntimeSeconds);
        Assert.Equal(600, (await dbContext.AgentSchedules.SingleAsync()).MaxRuntimeSeconds);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task InstallAsync_RejectsRevokedPackageVersion()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        package.Status = AgentPackageVersionStatus.Revoked;
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);

        var exception = await Assert.ThrowsAsync<AgentInstallationException>(() =>
            service.InstallAsync(package.Id, ValidRequest()));

        Assert.Contains("not available for installation", exception.Message);
        Assert.Empty(await dbContext.AgentInstallations.ToListAsync());
    }

    [Fact]
    public async Task InstallAsync_CreatesInstallationGrantAndPeriodicSchedule()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var before = DateTimeOffset.UtcNow.AddSeconds(899);

        var result = await service.InstallAsync(package.Id, ValidRequest());

        Assert.True(result.IsEnabled);
        Assert.Equal("Periodic", result.Schedule.ActivationMode);
        Assert.True(result.Schedule.NextTickAt >= before);
        Assert.Single(await dbContext.AgentInstallations.ToListAsync());
        Assert.Single(await dbContext.AgentInstallationGrants.ToListAsync());
        Assert.Single(await dbContext.AgentSchedules.ToListAsync());
        var buildJob = Assert.Single(await dbContext.AgentBuildJobs.ToListAsync());
        Assert.Equal(AgentBuildStatus.Queued, buildJob.Status);
        Assert.Equal(
            AgentPackageVersionStatus.Approved,
            (await dbContext.AgentPackageVersions.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunNowAsync_RejectsAlwaysOnInstallation()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        (await dbContext.AgentRuntimeGlobalSettings.SingleAsync()).AllowAlwaysOnCommunityAgents = true;
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        var installation = await service.InstallAsync(
            package.Id,
            ValidRequest() with { ActivationMode = "AlwaysOn" });

        var exception = await Assert.ThrowsAsync<AgentInstallationException>(() =>
            service.RunNowAsync(installation.Id));

        Assert.Contains("unavailable for always-on agents", exception.Message);
    }

    [Fact]
    public async Task InstallAsync_ReusesPackageBuildAcrossBusinessInstallations()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var service = CreateService(dbContext);

        await service.InstallAsync(package.Id, ValidRequest());
        await service.InstallAsync(
            package.Id,
            ValidRequest() with { BusinessId = "second-business" });

        Assert.Equal(2, await dbContext.AgentInstallations.CountAsync());
        Assert.Single(await dbContext.AgentBuildJobs.ToListAsync());
    }

    [Fact]
    public async Task ListAsync_ReturnsNewestInstallationsFirst()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var service = CreateService(dbContext);

        var older = await service.InstallAsync(package.Id, ValidRequest());
        var newer = await service.InstallAsync(
            package.Id,
            ValidRequest() with { BusinessId = "newer-business" });

        (await dbContext.AgentInstallations.SingleAsync(x => x.Id == older.Id)).CreatedAt =
            DateTimeOffset.UtcNow.AddDays(-1);
        (await dbContext.AgentInstallations.SingleAsync(x => x.Id == newer.Id)).CreatedAt =
            DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync();

        var installations = await service.ListAsync();

        Assert.Equal([newer.Id, older.Id], installations.Select(x => x.Id));
    }

    [Fact]
    public async Task InstallAsync_AllowsDistinctSameBusinessInstancesWhenManifestOptsIn()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext, supportsMultipleInstallations: true);
        var service = CreateService(dbContext);

        var first = await service.InstallAsync(package.Id, ValidRequest());
        var second = await service.InstallAsync(package.Id, ValidRequest());

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.InstallationKey, second.InstallationKey);
        Assert.Equal(2, await dbContext.AgentInstallations.CountAsync());
    }

    [Fact]
    public async Task InstallAsync_RejectsSecondSameBusinessInstanceWithoutManifestOptIn()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        await service.InstallAsync(package.Id, ValidRequest());

        var error = await Assert.ThrowsAsync<AgentInstallationException>(() =>
            service.InstallAsync(package.Id, ValidRequest()));

        Assert.Contains("does not support multiple installations", error.Message);
        Assert.Single(await dbContext.AgentInstallations.ToListAsync());
    }

    [Fact]
    public async Task UpdateAsync_StagesZeroGrantRevisionAndKeepsActiveRevisionRunning()
    {
        await using var dbContext = CreateDbContext();
        var current = await SeedAsync(dbContext);
        var containers = new TestAgentContainerRunner(containerExists: true);
        var service = CreateService(dbContext, containers);
        var installed = await service.InstallAsync(current.Id, ValidRequest());
        dbContext.AgentRuntimeInstances.Add(new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installed.Id,
            ContainerId = "old-version-container",
            QueuedAt = DateTimeOffset.UtcNow
        });
        var update = new AgentPackageVersion
        {
            Id = Guid.NewGuid(),
            PackageSourceId = current.PackageSourceId,
            CommitSha = new string('2', 40),
            ManifestDigest = new string('b', 64),
            ManifestJson = JsonSerializer.Serialize(new
            {
                manifestVersion = "2.0",
                kind = "agent",
                id = current.AgentId,
                name = current.AgentName,
                version = "2.0.0",
                publisher = new { id = "com.example", name = "Example" },
                runtime = new { type = "dotnet-project" },
                protocol = new { minimumVersion = "2.0", maximumVersion = "2.x" },
                provides = Array.Empty<object>(),
                requires = Array.Empty<object>(),
                events = new { subscribes = Array.Empty<string>() },
                webAccess = new { mode = "None", rules = Array.Empty<object>() }
            }),
            ManifestFileName = "csweet-plugin.json",
            AgentId = current.AgentId,
            AgentName = current.AgentName,
            Version = "2.0.0",
            PublisherId = "com.example",
            PublisherName = "Example",
            RuntimeType = "dotnet-project",
            Status = AgentPackageVersionStatus.Failed,
            ImportedAt = DateTimeOffset.UtcNow
        };
        var failedBuild = new AgentBuildJob
        {
            Id = Guid.NewGuid(),
            PackageVersionId = update.Id,
            Attempt = 1,
            QueuedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        failedBuild.TransitionTo(AgentBuildStatus.Failed, DateTimeOffset.UtcNow);
        update.BuildJobs.Add(failedBuild);
        dbContext.AgentPackageVersions.Add(update);
        await dbContext.SaveChangesAsync();

        var result = await service.UpdateAsync(
            installed.Id,
            new UpdateAgentInstallationRequest(update.Id));

        Assert.Equal(update.Id, result.PackageVersionId);
        Assert.Equal("2.0.0", result.AgentVersion);
        Assert.Empty(result.GrantedCapabilities);
        Assert.Empty(result.GrantedSubscriptions);
        Assert.Empty(result.GrantedPublications);
        Assert.Equal("Staged", result.RevisionStatus);
        Assert.False(result.IsEnabled);
        Assert.Equal(3, await dbContext.AgentBuildJobs.CountAsync());
        var retry = await dbContext.AgentBuildJobs
            .Where(x => x.PackageVersionId == update.Id)
            .OrderByDescending(x => x.Attempt)
            .FirstAsync();
        Assert.Equal(2, retry.Attempt);
        Assert.Equal(AgentBuildStatus.Queued, retry.Status);
        Assert.DoesNotContain("old-version-container", containers.Removed);
        Assert.True((await dbContext.AgentInstallations.SingleAsync(x => x.Id == installed.Id)).IsEnabled);
        Assert.Equal(AgentRuntimeStatus.Queued, (await dbContext.AgentRuntimeInstances.SingleAsync()).Status);
    }

    [Fact]
    public async Task RemoveAsync_AssignedEmployeeRejectsBeforeDisablingInstallation()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var installed = await service.InstallAsync(package.Id, ValidRequest());
        var organization = new Organization
        {
            Id = Guid.NewGuid(),
            Name = "Example Company",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.CoreOrganizations.Add(organization);
        dbContext.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            AgentInstallationId = installed.Id,
            DisplayName = "Researcher",
            EmployeeType = EmployeeType.Agent,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AgentInstallationException>(
            () => service.RemoveAsync(installed.Id));

        Assert.Contains("Researcher", exception.Message);
        Assert.Contains("Employees page", exception.Message);
        Assert.True((await dbContext.AgentInstallations.SingleAsync()).IsEnabled);
    }

    [Fact]
    public async Task RemoveAsync_LastInstallation_RemovesPackageSourceAndRelatedRecords()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var installation = await service.InstallAsync(package.Id, ValidRequest());

        var result = await service.RemoveAsync(installation.Id);

        Assert.True(result.PackageRemoved);
        Assert.True(result.SourceRemoved);
        Assert.Equal(0, result.CleanupWarnings);
        Assert.Empty(await dbContext.AgentInstallations.ToListAsync());
        Assert.Empty(await dbContext.AgentInstallationGrants.ToListAsync());
        Assert.Empty(await dbContext.AgentSchedules.ToListAsync());
        Assert.Empty(await dbContext.AgentBuildJobs.ToListAsync());
        Assert.Empty(await dbContext.AgentPackageVersions.ToListAsync());
        Assert.Empty(await dbContext.AgentPackageSources.ToListAsync());
    }

    [Fact]
    public async Task RemoveAsync_SharedPackage_PreservesPackageAndOtherInstallation()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var first = await service.InstallAsync(package.Id, ValidRequest());
        await service.InstallAsync(package.Id, ValidRequest() with { BusinessId = "second-business" });

        var result = await service.RemoveAsync(first.Id);

        Assert.False(result.PackageRemoved);
        Assert.False(result.SourceRemoved);
        Assert.Single(await dbContext.AgentInstallations.ToListAsync());
        Assert.Single(await dbContext.AgentPackageVersions.ToListAsync());
        Assert.Single(await dbContext.AgentPackageSources.ToListAsync());
        Assert.Single(await dbContext.AgentBuildJobs.ToListAsync());
    }

    [Fact]
    public async Task RemoveAsync_RemovesRetainedRuntimeContainer()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var containers = new TestAgentContainerRunner(containerExists: true);
        var service = CreateService(dbContext, containers);
        var installation = await service.InstallAsync(package.Id, ValidRequest());
        var runtimeId = Guid.NewGuid();
        dbContext.AgentRuntimeInstances.Add(new AgentRuntimeInstance
        {
            Id = runtimeId,
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            ContainerId = "retained-container",
            QueuedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        await service.RemoveAsync(installation.Id);

        Assert.Contains("retained-container", containers.Removed);
        Assert.Contains(("csweet-runtime-" + runtimeId.ToString("N"), "agenthost"), containers.NetworksRemoved);
    }

    [Fact]
    public async Task RemoveAsync_SkipsHistoricalFailedStartWithoutContainerId()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var containers = new TestAgentContainerRunner(containerExists: true);
        var service = CreateService(dbContext, containers);
        var installation = await service.InstallAsync(package.Id, ValidRequest());
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            ContainerName = "failed-start-container",
            QueuedAt = DateTimeOffset.UtcNow
        };
        runtime.TransitionTo(AgentRuntimeStatus.Starting, DateTimeOffset.UtcNow);
        runtime.TransitionTo(AgentRuntimeStatus.StartFailed, DateTimeOffset.UtcNow, "Docker never created the container.");
        dbContext.AgentRuntimeInstances.Add(runtime);
        await dbContext.SaveChangesAsync();

        await service.RemoveAsync(installation.Id);

        Assert.DoesNotContain("failed-start-container", containers.Inspected);
        Assert.DoesNotContain("failed-start-container", containers.Removed);
    }

    [Fact]
    public async Task RemoveAsync_RejectsRemovalWhilePackageIsBuilding()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var installation = await service.InstallAsync(package.Id, ValidRequest());
        var build = await dbContext.AgentBuildJobs.SingleAsync();
        build.TransitionTo(AgentBuildStatus.Cloning, DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AgentInstallationException>(
            () => service.RemoveAsync(installation.Id));

        Assert.Contains("currently building", exception.Message);
        Assert.Single(await dbContext.AgentInstallations.ToListAsync());
    }

    [Fact]
    public async Task ListRunsAsync_IncludesLiveContainerOutput()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var containers = new TestAgentContainerRunner(containerExists: true, logs: "agent established MCP session");
        var service = CreateService(dbContext, containers);
        var installation = await service.InstallAsync(package.Id, ValidRequest());
        dbContext.AgentRuntimeInstances.Add(new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            ContainerId = "running-container",
            QueuedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var run = Assert.Single(await service.ListRunsAsync(installation.Id));

        Assert.Equal("agent established MCP session", run.LogExcerpt);
    }

    private static InstallAgentRequest ValidRequest() => new(
        "default",
        "Periodic",
        900,
        "Skip",
        ["research.execute.v1"],
        ["research.requested.v1"],
        [],
        [],
        [],
        600,
        512,
        50);

    private static async Task<AgentPackageVersion> SeedAsync(CSweetDbContext dbContext, bool supportsMultipleInstallations = false)
    {
        dbContext.AgentRuntimeGlobalSettings.Add(new AgentRuntimeGlobalSettings
        {
            Id = Guid.NewGuid(),
            EnableImportedAgents = true,
            DefaultActivationMode = ActivationMode.Periodic,
            DefaultOverlapPolicy = OverlapPolicy.Skip,
            DefaultRestartPolicy = RestartPolicy.Never,
            MinimumTickFrequencySeconds = 300,
            DefaultMaxRuntimeSeconds = 600,
            MaximumContainerMemoryMb = 2048,
            MaximumContainerCpuPercent = 200,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        var source = new AgentPackageSource
        {
            Id = Guid.NewGuid(),
            RepositoryUrl = "https://github.com/example/research-agent",
            Host = "github.com",
            RepositoryOwner = "example",
            RepositoryName = "research-agent",
            DefaultBranch = "main",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var package = new AgentPackageVersion
        {
            Id = Guid.NewGuid(),
            PackageSourceId = source.Id,
            CommitSha = "0123456789abcdef0123456789abcdef01234567",
            ManifestDigest = new string('a', 64),
            ManifestJson = JsonSerializer.Serialize(new
            {
                manifestVersion = "2.0",
                kind = "agent",
                id = "com.example.research-agent",
                name = "Research Agent",
                version = "1.2.3",
                publisher = new { id = "com.example", name = "Example" },
                runtime = new { type = "dotnet-project", supportsMultipleInstallations },
                protocol = new { minimumVersion = "2.0", maximumVersion = "2.x" },
                provides = new[] { new {
                    name = "research.execute.v1",
                    description = "Execute research",
                    inputSchema = new { type = "object" },
                    outputSchema = new { type = "object" },
                    executionTimeoutSeconds = 120,
                    idempotency = "work-item"
                } },
                requires = Array.Empty<object>(),
                events = new
                {
                    subscribes = new[] { "research.requested.v1" }
                },
                webAccess = new { mode = "None", rules = Array.Empty<object>() }
            }),
            ManifestFileName = "csweet-plugin.json",
            AgentId = "com.example.research-agent",
            AgentName = "Research Agent",
            Version = "1.2.3",
            PublisherId = "com.example",
            PublisherName = "Example",
            RuntimeType = "dotnet-project",
            Status = AgentPackageVersionStatus.Previewed,
            ImportedAt = DateTimeOffset.UtcNow
        };
        dbContext.AgentPackageSources.Add(source);
        dbContext.AgentPackageVersions.Add(package);
        await dbContext.SaveChangesAsync();
        return package;
    }

    private static CSweetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new CSweetDbContext(options);
    }

    private static AgentInstallationService CreateService(
        CSweetDbContext dbContext,
        TestAgentContainerRunner? containers = null,
        ILogger<AgentInstallationService>? logger = null) =>
        new(
            dbContext,
            new TestAuditEventWriter(),
            containers ?? new TestAgentContainerRunner(),
            Options.Create(new AgentRuntimeManagerOptions()),
            logger ?? NullLogger<AgentInstallationService>.Instance);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class TestAgentContainerRunner(bool containerExists = false, string logs = "") : IAgentContainerRunner
    {
        public List<string> Inspected { get; } = [];
        public List<string> Removed { get; } = [];
        public List<(string NetworkName, string BrokerGatewayContainer)> NetworksRemoved { get; } = [];

        public Task<AgentContainerStatus> StartAsync(AgentContainerStartRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StopAsync(string containerId, TimeSpan gracePeriod, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AgentManagedContainer>> ListManagedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentManagedContainer>>([]);

        public Task<AgentContainerStatus?> InspectAsync(string containerId, CancellationToken cancellationToken = default)
        {
            Inspected.Add(containerId);
            return Task.FromResult<AgentContainerStatus?>(containerExists
                ? new AgentContainerStatus(containerId, containerId, AgentContainerState.Exited, 0, null, null, null)
                : null);
        }

        public Task RemoveAsync(string containerId, bool force = false, CancellationToken cancellationToken = default)
        {
            Removed.Add(containerId);
            return Task.CompletedTask;
        }

        public Task RemoveNetworkAsync(string networkName, string brokerGatewayContainer, CancellationToken cancellationToken = default)
        {
            NetworksRemoved.Add((networkName, brokerGatewayContainer));
            return Task.CompletedTask;
        }

        public Task<string> GetLogsAsync(string containerId, int maximumBytes, CancellationToken cancellationToken = default) =>
            Task.FromResult(logs);
    }
}
