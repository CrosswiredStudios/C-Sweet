using CSweet.Domain.Setup;
using CSweet.Infrastructure.Persistence;
using CSweet.Infrastructure.Setup;
using Microsoft.EntityFrameworkCore;

namespace CSweet.UnitTests;

public class SetupServiceTests
{
    [Fact]
    public async Task CreatingInitialSetupState_ReturnsIncompleteSetup()
    {
        await using var dbContext = CreateDbContext();
        var service = new SetupService(dbContext);

        var status = await service.GetStatusAsync();

        Assert.False(status.IsFirstRunComplete);
        Assert.Null(status.DefaultChatProviderId);
        Assert.Equal(5, status.Steps.Count);
        Assert.DoesNotContain(status.Steps, x => x.Key == "finish");
        Assert.Contains(status.Steps, x => x.Key == "llm-provider" && !x.IsComplete);
        Assert.Contains(status.Steps, x => x.Key == "communications" && !x.IsRequired && !x.IsComplete);
        Assert.DoesNotContain(status.Steps, x => x.Key == "model-capability-test");
        Assert.DoesNotContain(status.Steps, x => x.Key == "storage");
        Assert.DoesNotContain(status.Steps, x => x.Key == "worker-runtime");
    }

    [Fact]
    public async Task CompletingRequiredStep_SetsCompleteAndCompletedAt()
    {
        await using var dbContext = CreateDbContext();
        var service = new SetupService(dbContext);

        var result = await service.CompleteStepAsync("llm-provider");

        var step = await dbContext.OnboardingSteps.SingleAsync(x => x.Key == "llm-provider");
        Assert.True(result.Succeeded);
        Assert.True(step.IsComplete);
        Assert.NotNull(step.CompletedAt);
    }

    [Fact]
    public async Task FinishSetup_DoesNotRequireDefaultModelOrChatTest()
    {
        await using var dbContext = CreateDbContext();
        var service = new SetupService(dbContext);
        await service.EnsureSeededAsync();

        var provider = CreateEnabledProvider();
        dbContext.LlmProviderProfiles.Add(provider);
        await CompletePriorRequiredStepsAsync(dbContext);

        var result = await service.CompleteFirstRunAsync();

        Assert.True(result.Succeeded);
        Assert.Null((await dbContext.SystemConfigurations.SingleAsync()).DefaultChatProviderId);
    }

    [Fact]
    public async Task FinishSetup_FailsWhenNoEnabledProviderExists()
    {
        await using var dbContext = CreateDbContext();
        var service = new SetupService(dbContext);
        await service.EnsureSeededAsync();

        await CompletePriorRequiredStepsAsync(dbContext);

        var result = await service.CompleteFirstRunAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("provider_profile_required", result.ErrorCode);
    }

    [Fact]
    public async Task FinishSetup_SucceedsWhenPrerequisitesExistAndWritesAuditEvent()
    {
        await using var dbContext = CreateDbContext();
        var service = new SetupService(dbContext);
        await service.EnsureSeededAsync();

        var provider = CreateEnabledProvider();
        dbContext.LlmProviderProfiles.Add(provider);
        await CompletePriorRequiredStepsAsync(dbContext);

        var result = await service.CompleteFirstRunAsync();

        Assert.True(result.Succeeded);
        Assert.True((await dbContext.SystemConfigurations.SingleAsync()).IsFirstRunComplete);
        Assert.True((await dbContext.OnboardingSteps.SingleAsync(x => x.Key == "communications")).IsComplete);
        Assert.Contains(await dbContext.AuditEvents.ToListAsync(), x => x.EventType == "setup.first_run.completed");
    }

    private static CSweetDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CSweetDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new CSweetDbContext(options);
    }

    private static LlmProviderProfile CreateEnabledProvider()
    {
        return new LlmProviderProfile
        {
            Id = Guid.NewGuid(),
            Name = "Local LM Studio",
            ProviderType = LlmProviderType.LmStudio,
            BaseUrl = "http://localhost:1234/v1",
            DefaultChatModel = string.Empty,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static async Task CompletePriorRequiredStepsAsync(CSweetDbContext dbContext)
    {
        var now = DateTimeOffset.UtcNow;
        var steps = await dbContext.OnboardingSteps
            .Where(x => x.IsRequired)
            .ToListAsync();

        foreach (var step in steps)
        {
            step.IsComplete = true;
            step.CompletedAt = now;
            step.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync();
    }
}
