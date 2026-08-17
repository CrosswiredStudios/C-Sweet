using System.Text.Json;
using System.Text.Json.Nodes;
using CSweet.Application.Setup;
using CSweet.Office.Contracts.Workloads;
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
    public async Task InstallAsync_RequiredConfigurationMissing_RejectsBeforeCreatingInstallation()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext, requiresConfiguration: true);
        var service = CreateService(dbContext);

        var exception = await Assert.ThrowsAsync<AgentInstallationException>(
            () => service.InstallAsync(package.Id, ValidRequest()));

        Assert.Contains("LLM provider", exception.Message);
        Assert.Empty(await dbContext.AgentInstallations.ToListAsync());
    }

    [Fact]
    public async Task InstallAsync_RequiredConfigurationProvided_PersistsItWithInstallation()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext, requiresConfiguration: true);
        var providerId = await dbContext.LlmProviderProfiles.Select(x => x.Id).SingleAsync();
        var service = CreateService(dbContext);
        var request = ValidRequest() with
        {
            ConfigurationSettings = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(providerId.ToString("D")),
                ["llmModel"] = JsonSerializer.SerializeToElement("test-model")
            }
        };

        var installation = await service.InstallAsync(package.Id, request);

        var configuration = await dbContext.AgentInstallationConfigurations
            .SingleAsync(x => x.AgentInstallationId == installation.Id);
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(configuration.SettingsJson);
        Assert.Equal(providerId.ToString("D"), settings!["llmProviderId"]);
        Assert.Equal("test-model", settings["llmModel"]);
    }

    [Fact]
    public async Task InstallAsync_RejectsConfigurationOutsideCapabilitySchema()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext, requiresConfiguration: true);
        AddConstrainedConfigurationSchema(package);
        await dbContext.SaveChangesAsync();
        var providerId = await dbContext.LlmProviderProfiles.Select(x => x.Id).SingleAsync();
        var service = CreateService(dbContext);

        var invalidTone = ValidRequest() with
        {
            ConfigurationSettings = Configuration(
                providerId,
                responseTone: "Blunt",
                maxAlternatives: 2)
        };
        var toneError = await Assert.ThrowsAsync<AgentInstallationException>(() =>
            service.InstallAsync(package.Id, invalidTone));
        Assert.Contains("Response tone", toneError.Message);
        Assert.Contains("one of the values", toneError.Message);

        var invalidLimit = ValidRequest() with
        {
            ConfigurationSettings = Configuration(
                providerId,
                responseTone: "concise",
                maxAlternatives: 3)
        };
        var limitError = await Assert.ThrowsAsync<AgentInstallationException>(() =>
            service.InstallAsync(package.Id, invalidLimit));
        Assert.Contains("Maximum alternatives", limitError.Message);
        Assert.Contains("less than or equal to 2", limitError.Message);

        Assert.Empty(await dbContext.AgentInstallations.ToListAsync());
    }

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
    public async Task InstallAsync_RejectsUndersizedFirstPartyRuntimeMemory()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        package.PublisherId = "com.csweet";
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);

        var exception = await Assert.ThrowsAsync<AgentInstallationException>(() =>
            service.InstallAsync(package.Id, ValidRequest() with { MemoryMb = 512 }));

        Assert.Contains("at least 1024 MB", exception.Message);
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
            new UpdateAgentScheduleRequest("Scheduled", 900, "Skip", 86_400, true));

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
    public async Task InstallAsync_CreatesInstallationGrantAndScheduledSchedule()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var before = DateTimeOffset.UtcNow.AddSeconds(899);

        var result = await service.InstallAsync(package.Id, ValidRequest());

        Assert.True(result.IsEnabled);
        Assert.Equal("Scheduled", result.Schedule.ActivationMode);
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
    public async Task RetryBuildAsync_QueuesAnotherAttemptAndClearsStartupSuppression()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        (await dbContext.AgentRuntimeGlobalSettings.SingleAsync()).AllowAlwaysOnCommunityAgents = true;
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        var installed = await service.InstallAsync(
            package.Id,
            ValidRequest() with { ActivationMode = "AlwaysOn" });
        var failedBuild = await dbContext.AgentBuildJobs.SingleAsync();
        failedBuild.TransitionTo(AgentBuildStatus.Failed, DateTimeOffset.UtcNow);
        package.Status = AgentPackageVersionStatus.Failed;
        var schedule = await dbContext.AgentSchedules.SingleAsync();
        schedule.ConsecutiveStartupFailures = 3;
        schedule.AutomaticStartSuppressedAt = DateTimeOffset.UtcNow;
        schedule.NextTickAt = null;
        await dbContext.SaveChangesAsync();

        var result = await service.RetryBuildAsync(installed.Id);

        Assert.Equal("Queued", result.Build!.Status);
        Assert.Equal(2, result.Build.Attempt);
        Assert.Equal(0, result.Schedule.ConsecutiveStartupFailures);
        Assert.Null(result.Schedule.AutomaticStartSuppressedAt);
        Assert.NotNull(result.Schedule.NextTickAt);
        Assert.Equal(AgentPackageVersionStatus.Approved, package.Status);
    }

    [Fact]
    public async Task RetryStartupAsync_ClearsSuppressionAndMakesAlwaysOnAgentDueImmediately()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        (await dbContext.AgentRuntimeGlobalSettings.SingleAsync()).AllowAlwaysOnCommunityAgents = true;
        await dbContext.SaveChangesAsync();
        var service = CreateService(dbContext);
        var installed = await service.InstallAsync(
            package.Id,
            ValidRequest() with { ActivationMode = "AlwaysOn" });
        package.Status = AgentPackageVersionStatus.Built;
        var schedule = await dbContext.AgentSchedules.SingleAsync();
        schedule.ConsecutiveStartupFailures = 3;
        schedule.AutomaticStartSuppressedAt = DateTimeOffset.UtcNow;
        schedule.NextTickAt = null;
        await dbContext.SaveChangesAsync();
        var before = DateTimeOffset.UtcNow;

        var result = await service.RetryStartupAsync(installed.Id);

        Assert.Equal(0, result.Schedule.ConsecutiveStartupFailures);
        Assert.Null(result.Schedule.AutomaticStartSuppressedAt);
        Assert.NotNull(result.Schedule.NextTickAt);
        Assert.True(result.Schedule.NextTickAt >= before);
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
            IsolationProviderId = "test-vm",
            ProviderInstanceId = "old-version-container",
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
    public async Task UpdateAndApprove_PreservesCompatibleRequiredConfiguration()
    {
        await using var dbContext = CreateDbContext();
        var current = await SeedAsync(dbContext, requiresConfiguration: true);
        var providerId = await dbContext.LlmProviderProfiles.Select(x => x.Id).SingleAsync();
        var configuredRequest = ValidRequest() with
        {
            ConfigurationSettings = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["llmProviderId"] = JsonSerializer.SerializeToElement(providerId.ToString("D")),
                ["llmModel"] = JsonSerializer.SerializeToElement("test-model")
            }
        };
        var service = CreateService(dbContext);
        var installed = await service.InstallAsync(current.Id, configuredRequest);
        var currentConfiguration = await dbContext.AgentInstallationConfigurations
            .SingleAsync(x => x.AgentInstallationId == installed.Id);
        currentConfiguration.SettingsJson = JsonSerializer.Serialize(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["llmProviderId"] = providerId.ToString("D"),
                ["llmModel"] = "test-model",
                ["customInstructions"] = "Legacy instructions"
            });
        await dbContext.SaveChangesAsync();
        current.Status = AgentPackageVersionStatus.Built;

        var manifest = JsonNode.Parse(current.ManifestJson)!.AsObject();
        manifest["version"] = "2.0.0";
        var update = new AgentPackageVersion
        {
            Id = Guid.NewGuid(),
            PackageSourceId = current.PackageSourceId,
            CommitSha = new string('2', 40),
            ManifestDigest = new string('c', 64),
            ManifestJson = manifest.ToJsonString(),
            ManifestFileName = "csweet-plugin.json",
            AgentId = current.AgentId,
            AgentName = current.AgentName,
            Version = "2.0.0",
            PublisherId = current.PublisherId,
            PublisherName = current.PublisherName,
            RuntimeType = current.RuntimeType,
            Status = AgentPackageVersionStatus.Built,
            ImportedAt = DateTimeOffset.UtcNow
        };
        dbContext.AgentPackageVersions.Add(update);
        await dbContext.SaveChangesAsync();

        var staged = await service.UpdateAsync(
            installed.Id,
            new UpdateAgentInstallationRequest(update.Id));
        var stagedConfiguration = await dbContext.AgentInstallationConfigurations
            .SingleAsync(x => x.AgentInstallationId == staged.Id);
        Assert.Contains("test-model", stagedConfiguration.SettingsJson);
        Assert.DoesNotContain("customInstructions", stagedConfiguration.SettingsJson);

        var approved = await service.ApproveUpdateAsync(
            staged.Id,
            ValidRequest());

        Assert.True(approved.IsEnabled);
        var approvedConfiguration = await dbContext.AgentInstallationConfigurations
            .SingleAsync(x => x.AgentInstallationId == approved.Id);
        Assert.Contains("test-model", approvedConfiguration.SettingsJson);
        Assert.Contains(providerId.ToString("D"), approvedConfiguration.SettingsJson);
        Assert.DoesNotContain("customInstructions", approvedConfiguration.SettingsJson);
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
            IsolationProviderId = "test-vm",
            ProviderInstanceId = "retained-container",
            QueuedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        await service.RemoveAsync(installation.Id);

        Assert.Contains("retained-container", containers.Removed);
    }

    [Fact]
    public async Task RemoveAsync_RemovesWorkHistoryBeforeRuntimeHistory()
    {
        await using var dbContext = CreateDbContext();
        var package = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var installation = await service.InstallAsync(package.Id, ValidRequest());
        var now = DateTimeOffset.UtcNow;
        var runtime = new AgentRuntimeInstance
        {
            Id = Guid.NewGuid(),
            TickId = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            QueuedAt = now
        };
        var workItem = new AgentWorkItem
        {
            Id = Guid.NewGuid(),
            OrganizationId = installation.BusinessId,
            AgentInstallationId = installation.Id,
            Kind = AgentWorkKind.Capability,
            Name = "research.execute.v1",
            PayloadHash = new string('a', 64),
            CorrelationId = Guid.NewGuid().ToString("D"),
            IdempotencyKey = "remove-history-test",
            AvailableAt = now,
            DeadlineAt = now.AddMinutes(5),
            CreatedAt = now
        };
        var attempt = new AgentWorkAttempt
        {
            Id = Guid.NewGuid(),
            AgentWorkItemId = workItem.Id,
            RuntimeInstanceId = runtime.Id,
            Attempt = 1,
            LeaseTokenHash = new string('b', 64),
            ClaimedAt = now,
            LeaseExpiresAt = now.AddMinutes(1)
        };
        dbContext.AddRange(
            runtime,
            workItem,
            attempt,
            new AgentWorkProgress
            {
                Id = Guid.NewGuid(),
                AgentWorkItemId = workItem.Id,
                AgentWorkAttemptId = attempt.Id,
                Sequence = 1,
                SizeBytes = 2,
                OccurredAt = now
            });
        await dbContext.SaveChangesAsync();

        await service.RemoveAsync(installation.Id);

        Assert.Empty(await dbContext.AgentWorkProgress.ToListAsync());
        Assert.Empty(await dbContext.AgentWorkAttempts.ToListAsync());
        Assert.Empty(await dbContext.AgentWorkItems.ToListAsync());
        Assert.Empty(await dbContext.AgentRuntimeInstances.ToListAsync());
    }

    [Fact]
    public async Task RemoveAsync_SkipsHistoricalFailedStartWithoutProviderInstanceId()
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
            IsolationProviderId = "failed-start-container",
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
            IsolationProviderId = "test-vm",
            ProviderInstanceId = "running-container",
            QueuedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var run = Assert.Single(await service.ListRunsAsync(installation.Id));

        Assert.Equal("agent established MCP session", run.LogExcerpt);
    }

    private static InstallAgentRequest ValidRequest() => new(
        "default",
        "Scheduled",
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

    private static async Task<AgentPackageVersion> SeedAsync(
        CSweetDbContext dbContext,
        bool supportsMultipleInstallations = false,
        bool requiresConfiguration = false)
    {
        dbContext.AgentRuntimeGlobalSettings.Add(new AgentRuntimeGlobalSettings
        {
            Id = Guid.NewGuid(),
            EnableImportedAgents = true,
            DefaultActivationMode = ActivationMode.Scheduled,
            DefaultOverlapPolicy = OverlapPolicy.Skip,
            DefaultRestartPolicy = RestartPolicy.Never,
            MinimumTickFrequencySeconds = 300,
            DefaultMaxRuntimeSeconds = 600,
            MaximumWorkloadMemoryMb = 2048,
            MaximumWorkloadCpuPercent = 200,
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
                configuration = requiresConfiguration
                    ? new[]
                    {
                        new { key = "llmProviderId", type = "provider", label = "LLM provider", required = true, secret = false },
                        new { key = "llmModel", type = "model", label = "Model", required = true, secret = false }
                    }
                    : [],
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
        if (requiresConfiguration)
        {
            dbContext.LlmProviderProfiles.Add(new LlmProviderProfile
            {
                Id = Guid.NewGuid(),
                Name = "Test provider",
                ProviderType = LlmProviderType.OpenAiCompatible,
                BaseUrl = "http://localhost:1234",
                DefaultChatModel = string.Empty,
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        await dbContext.SaveChangesAsync();
        return package;
    }

    private static IReadOnlyDictionary<string, JsonElement> Configuration(
        Guid providerId,
        string responseTone,
        int maxAlternatives) =>
        new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["llmProviderId"] = JsonSerializer.SerializeToElement(providerId.ToString("D")),
            ["llmModel"] = JsonSerializer.SerializeToElement("test-model"),
            ["responseTone"] = JsonSerializer.SerializeToElement(responseTone),
            ["maxAlternatives"] = JsonSerializer.SerializeToElement(maxAlternatives)
        };

    private static void AddConstrainedConfigurationSchema(AgentPackageVersion package)
    {
        var manifest = JsonNode.Parse(package.ManifestJson)!.AsObject();
        var configuration = manifest["configuration"]!.AsArray();
        configuration.Add(new JsonObject
        {
            ["key"] = "responseTone",
            ["type"] = "select",
            ["label"] = "Response tone",
            ["required"] = true,
            ["secret"] = false
        });
        configuration.Add(new JsonObject
        {
            ["key"] = "maxAlternatives",
            ["type"] = "number",
            ["label"] = "Maximum alternatives",
            ["required"] = true,
            ["secret"] = false
        });
        manifest["provides"]!.AsArray().Add(JsonNode.Parse("""
        {
          "name": "agent.configuration.update.v1",
          "description": "Update constrained settings.",
          "inputSchema": {
            "type": "object",
            "additionalProperties": false,
            "required": ["settings"],
            "properties": {
              "settings": {
                "type": "object",
                "additionalProperties": false,
                "properties": {
                  "responseTone": {
                    "type": "string",
                    "enum": ["concise", "balanced", "detailed"]
                  },
                  "maxAlternatives": {
                    "type": "number",
                    "minimum": 0,
                    "maximum": 2
                  }
                }
              }
            }
          },
          "outputSchema": {
            "type": "object",
            "additionalProperties": false
          },
          "executionTimeoutSeconds": 30,
          "idempotency": "work-item"
        }
        """));
        package.ManifestJson = manifest.ToJsonString();
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
            new TestAgentBuildService(dbContext),
            containers ?? new TestAgentContainerRunner(),
            Options.Create(new AgentRuntimeManagerOptions()),
            logger ?? NullLogger<AgentInstallationService>.Instance);

    private sealed class TestAgentBuildService(CSweetDbContext dbContext) : IAgentBuildService
    {
        public async Task<Guid> QueueAsync(
            Guid packageVersionId,
            CancellationToken cancellationToken = default)
        {
            var package = await dbContext.AgentPackageVersions
                .SingleAsync(x => x.Id == packageVersionId, cancellationToken);
            var latest = await dbContext.AgentBuildJobs
                .Where(x => x.PackageVersionId == packageVersionId)
                .OrderByDescending(x => x.Attempt)
                .FirstOrDefaultAsync(cancellationToken);
            if (latest?.Status is AgentBuildStatus.Queued or AgentBuildStatus.Cloning or AgentBuildStatus.Building)
            {
                return latest.Id;
            }

            var retry = new AgentBuildJob
            {
                Id = Guid.NewGuid(),
                PackageVersionId = packageVersionId,
                Attempt = (latest?.Attempt ?? 0) + 1,
                QueuedAt = DateTimeOffset.UtcNow
            };
            package.Status = AgentPackageVersionStatus.Approved;
            dbContext.AgentBuildJobs.Add(retry);
            await dbContext.SaveChangesAsync(cancellationToken);
            return retry.Id;
        }

        public Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

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

    private sealed class TestAgentContainerRunner(bool containerExists = false, string logs = "") : IAgentWorkloadRunner
    {
        public List<string> Inspected { get; } = [];
        public List<string> Removed { get; } = [];

        public Task<IsolationWorkloadHandle> CreateAndStartAsync(RuntimeWorkloadSpecification workload, AgentTrustLevel trustLevel, string? preferredProviderId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StopAsync(IsolationWorkloadHandle handle, TimeSpan gracePeriod, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IsolationWorkloadStatus?> InspectAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default)
        {
            Inspected.Add(handle.ProviderInstanceId);
            return Task.FromResult<IsolationWorkloadStatus?>(containerExists
                ? new IsolationWorkloadStatus(handle, IsolationWorkloadState.Stopped, IsolationTerminationReason.Completed, 0, null, null, null, null)
                : null);
        }

        public Task DestroyAsync(IsolationWorkloadHandle handle, CancellationToken cancellationToken = default)
        {
            Removed.Add(handle.ProviderInstanceId);
            return Task.CompletedTask;
        }

        public Task<string> GetLogsAsync(IsolationWorkloadHandle handle, int maximumBytes, CancellationToken cancellationToken = default) =>
            Task.FromResult(logs);
    }
}
