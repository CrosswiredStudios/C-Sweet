using CSweet.Contracts.Setup;
using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public class AgentRuntimeSettingsServiceTests
{
    [Fact]
    public async Task UpdateAsync_RejectsMinimumTickAboveStoredDefault()
    {
        await using var dbContext = CreateDbContext();
        var settings = CreateSettings();
        dbContext.AgentRuntimeGlobalSettings.Add(settings);
        await dbContext.SaveChangesAsync();

        var service = new AgentRuntimeSettingsService(dbContext, new TestAuditEventWriter());

        var result = await service.UpdateAsync(new UpdateAgentRuntimeSettingsRequest(
            MinimumTickFrequencySeconds: settings.DefaultTickFrequencySeconds + 1));

        Assert.False(result.Succeeded);
        Assert.Contains("Default tick frequency", result.Message);
        Assert.Equal(300, settings.MinimumTickFrequencySeconds);
    }

    [Fact]
    public async Task UpdateAsync_RejectsStoredDefaultMemoryAboveRequestedMaximum()
    {
        await using var dbContext = CreateDbContext();
        var settings = CreateSettings();
        dbContext.AgentRuntimeGlobalSettings.Add(settings);
        await dbContext.SaveChangesAsync();

        var service = new AgentRuntimeSettingsService(dbContext, new TestAuditEventWriter());

        var result = await service.UpdateAsync(new UpdateAgentRuntimeSettingsRequest(
            MaximumWorkloadMemoryMb: settings.DefaultWorkloadMemoryMb - 1));

        Assert.False(result.Succeeded);
        Assert.Contains("Default workload memory", result.Message);
        Assert.Equal(2048, settings.MaximumWorkloadMemoryMb);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("999")]
    public async Task UpdateAsync_RejectsInvalidActivationMode(string activationMode)
    {
        await using var dbContext = CreateDbContext();
        var settings = CreateSettings();
        dbContext.AgentRuntimeGlobalSettings.Add(settings);
        await dbContext.SaveChangesAsync();

        var service = new AgentRuntimeSettingsService(dbContext, new TestAuditEventWriter());

        var result = await service.UpdateAsync(new UpdateAgentRuntimeSettingsRequest(
            DefaultActivationMode: activationMode));

        Assert.False(result.Succeeded);
        Assert.Contains("activation mode is invalid", result.Message);
        Assert.Equal(ActivationMode.Periodic, settings.DefaultActivationMode);
    }

    [Fact]
    public async Task UpdateAsync_RejectsNonPositiveRuntimeLimit()
    {
        await using var dbContext = CreateDbContext();
        var settings = CreateSettings();
        dbContext.AgentRuntimeGlobalSettings.Add(settings);
        await dbContext.SaveChangesAsync();

        var service = new AgentRuntimeSettingsService(dbContext, new TestAuditEventWriter());

        var result = await service.UpdateAsync(new UpdateAgentRuntimeSettingsRequest(
            DefaultWorkloadProcessLimit: 0));

        Assert.False(result.Succeeded);
        Assert.Contains("Default workload process limit must be positive", result.Message);
        Assert.Equal(100, settings.DefaultWorkloadProcessLimit);
    }

    [Fact]
    public async Task UpdateAsync_ClearsOptionalStringSetting()
    {
        await using var dbContext = CreateDbContext();
        var settings = CreateSettings();
        settings.AllowedPackageFeedHosts = "api.nuget.org";
        dbContext.AgentRuntimeGlobalSettings.Add(settings);
        await dbContext.SaveChangesAsync();

        var service = new AgentRuntimeSettingsService(dbContext, new TestAuditEventWriter());

        var result = await service.UpdateAsync(new UpdateAgentRuntimeSettingsRequest(
            AllowedPackageFeedHosts: string.Empty));

        Assert.True(result.Succeeded);
        Assert.Equal(string.Empty, settings.AllowedPackageFeedHosts);
    }

    private static CSweetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new CSweetDbContext(options);
    }

    private static AgentRuntimeGlobalSettings CreateSettings()
    {
        return new AgentRuntimeGlobalSettings
        {
            Id = Guid.NewGuid(),
            DefaultActivationMode = ActivationMode.Periodic,
            DefaultOverlapPolicy = OverlapPolicy.Skip,
            DefaultRestartPolicy = RestartPolicy.Never,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }
}
