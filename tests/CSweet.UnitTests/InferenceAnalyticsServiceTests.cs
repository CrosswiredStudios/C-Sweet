using System.Text.Json;
using CSweet.Contracts.Analytics;
using CSweet.Domain.Core;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Analytics;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public sealed class InferenceAnalyticsServiceTests
{
    [Fact]
    public async Task GetAsync_ScopesGroupsAndIncludesCurrentAndArchivedEmployees()
    {
        await using var db = CreateDbContext();
        var organizationId = Guid.NewGuid();
        var otherOrganizationId = Guid.NewGuid();
        var providerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.LlmProviderProfiles.Add(Provider(providerId));

        var active = AddAgent(db, organizationId, "Active employee", providerId, "current-model");
        var idle = AddAgent(db, organizationId, "Idle employee", providerId, "idle-model");
        var archived = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            DisplayName = "Archived employee",
            EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor,
            IsActive = false,
            ArchivedAt = now.AddDays(-1),
            CreatedAt = now.AddDays(-20)
        };
        db.CoreOrganizationUsers.Add(archived);
        db.CoreOrganizationUsers.Add(new OrganizationUser
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, DisplayName = "Human employee",
            EmployeeType = EmployeeType.Human, PermissionLevel = OrganizationPermissionLevel.Manager,
            IsActive = true, CreatedAt = now
        });

        db.AgentRunLogs.AddRange(
            Log(organizationId, active.Employee.Id, active.Installation.Id, providerId, "agent-a", "old-model", now.AddHours(-2), 10, 5),
            Log(organizationId, active.Employee.Id, active.Installation.Id, providerId, "agent-a", "current-model", now.AddHours(-1), 20, 10),
            Log(organizationId, archived.Id, null, providerId, "agent-archived", "archive-model", now.AddHours(-3), 7, 3),
            Log(organizationId, active.Employee.Id, active.Installation.Id, providerId, "agent-a", "boundary-model", now.AddHours(-24), 4, 1),
            Log(organizationId, active.Employee.Id, active.Installation.Id, providerId, "agent-a", "outside-24h", now.AddHours(-25), 100, 50),
            Log(otherOrganizationId, Guid.NewGuid(), null, providerId, "other-agent", "other-model", now.AddMinutes(-5), 999, 999));
        await db.SaveChangesAsync();

        var service = new InferenceAnalyticsService(
            db,
            new AgentInstallationConfigurationService(db, new TestAuditEventWriter()),
            new FixedTimeProvider(now));
        var result = await service.GetAsync(organizationId, InferenceAnalyticsWindow.Last24Hours);

        Assert.Equal("24h", result.Window);
        Assert.Equal(4, result.Totals.RequestCount);
        Assert.Equal(60, result.Totals.TotalTokens);
        Assert.Contains(result.Employees, x => x.Model == "boundary-model" && x.TotalTokens == 5);
        Assert.DoesNotContain(result.Employees, x => x.EmployeeName == "Human employee");
        Assert.DoesNotContain(result.Employees, x => x.Model == "outside-24h" || x.Model == "other-model");

        var current = Assert.Single(result.Employees, x => x.EmployeeId == active.Employee.Id && x.Model == "current-model");
        Assert.True(current.IsCurrentModel);
        Assert.Equal(30, current.TotalTokens);
        Assert.False(Assert.Single(result.Employees, x => x.EmployeeId == active.Employee.Id && x.Model == "old-model").IsCurrentModel);

        var zeroUsage = Assert.Single(result.Employees, x => x.EmployeeId == idle.Employee.Id);
        Assert.Equal("idle-model", zeroUsage.Model);
        Assert.True(zeroUsage.IsCurrentModel);
        Assert.Equal(0, zeroUsage.TotalTokens);

        var archivedUsage = Assert.Single(result.Employees, x => x.EmployeeId == archived.Id);
        Assert.False(archivedUsage.IsActive);
        Assert.Equal(10, archivedUsage.TotalTokens);
    }

    private static CSweetDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (OrganizationUser Employee, AgentInstallation Installation) AddAgent(
        CSweetDbContext db,
        Guid organizationId,
        string name,
        Guid providerId,
        string model)
    {
        var version = new AgentPackageVersion
        {
            Id = Guid.NewGuid(),
            PackageSourceId = Guid.NewGuid(),
            AgentId = $"agent-{Guid.NewGuid():N}",
            AgentName = name,
            Version = "1.0.0",
            CommitSha = "test",
            ManifestDigest = "test",
            CapabilityDescriptorsDigest = "test",
            ManifestJson = "{}",
            RuntimeType = "dotnet",
            ImportedAt = DateTimeOffset.UtcNow
        };
        var installation = new AgentInstallation
        {
            Id = Guid.NewGuid(),
            InstallationKey = Guid.NewGuid(),
            PackageVersionId = version.Id,
            PackageVersion = version,
            BusinessId = organizationId.ToString("D"),
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        installation.Configuration = new AgentInstallationConfiguration
        {
            Id = Guid.NewGuid(),
            AgentInstallationId = installation.Id,
            AgentInstallation = installation,
            SchemaVersion = "1",
            SettingsJson = JsonSerializer.Serialize(new
            {
                llmProviderId = providerId.ToString("D"),
                llmModel = model
            }),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var employee = new OrganizationUser
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            AgentInstallationId = installation.Id,
            AgentInstallation = installation,
            DisplayName = name,
            EmployeeType = EmployeeType.Agent,
            PermissionLevel = OrganizationPermissionLevel.Contributor,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.AgentInstallations.Add(installation);
        db.CoreOrganizationUsers.Add(employee);
        return (employee, installation);
    }

    private static LlmProviderProfile Provider(Guid id) => new()
    {
        Id = id,
        Name = "Test provider",
        ProviderType = LlmProviderType.LmStudio,
        BaseUrl = "http://localhost:1234/v1",
        DefaultChatModel = "current-model",
        IsEnabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static AgentRunLog Log(
        Guid organizationId,
        Guid? employeeId,
        Guid? installationId,
        Guid providerId,
        string agentKey,
        string model,
        DateTimeOffset startedAt,
        int input,
        int output) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        EmployeeId = employeeId,
        AgentInstallationId = installationId,
        ProviderProfileId = providerId,
        AgentKey = agentKey,
        Model = model,
        StartedAt = startedAt,
        CompletedAt = startedAt.AddSeconds(1),
        Status = "Completed",
        PromptHash = "test",
        TokenInputCount = input,
        TokenOutputCount = output,
        DurationMs = 1000
    };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
